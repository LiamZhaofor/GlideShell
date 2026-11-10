using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Core.Motion;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 总览底部的后台程序栏（合成层，在窗口与桌面条上面、拖动中的窗口下面）：毛玻璃底的圆角栏（与桌面名同一套材质：
/// 模糊的壁纸上叠很淡的白色与描边），每个程序一个「图标 + 名字」。悬停（或键盘选中）的程序底下出现淡白色圆角块，栏上方出现名字提示。
/// <para>
/// 动画与窗口同一套标准：栏的位置直接跟总览的展开进度走（手指上滑多少，栏就从任务栏后面升起多少；倒退、松手的弹簧都一样），
/// 进度 0 时整条沉在工作区底边以下（覆盖层只盖工作区，看不见）。总览已经开着时才出现 / 撤走（列表晚到、换成应用窗口视图），
/// 用与窗口相同的弹簧升起 / 沉下。
/// </para>
/// 线程：Prepare / Retract / SetHover / HideAll 在 UI 线程；<see cref="Apply"/> 在渲染线程。
/// </summary>
internal sealed unsafe class DockDecor
{
    private const double LabelDip = 12, TipDip = 12.5, GlassSigmaDip = 16, HighlightRadiusDip = 8, TipRadiusDip = 6;
    private const double TipPadXDip = 10, TipPadYDip = 5, TipGapDip = 8;
    private const double BounceDip = 9, BouncePeriod = 0.5; // 正在打开的程序：图标弹跳的高度与一次起落的时间（秒）
    private static readonly D2D.ColorF LabelColor = new(0.93f, 0.93f, 0.95f, 1);
    private static readonly D2D.ColorF FillColor = new(1, 1, 1, 0.07f); // 叠在毛玻璃上
    private static readonly D2D.ColorF EdgeColor = new(1, 1, 1, 0.14f);
    private static readonly D2D.ColorF HighlightColor = new(1, 1, 1, 0.13f);
    private static readonly D2D.ColorF TipColor = new(0.17f, 0.17f, 0.19f, 0.96f);
    private static readonly D2D.ColorF TipEdgeColor = new(1, 1, 1, 0.10f);
    private static readonly SpringParameters AppearSpring = SpringParameters.Default;
    private static readonly SpringParameters RemoveSpring = new(0.32, 1.0); // 少了一个程序：其余的往里收拢、栏变窄（不回弹）

    /// <summary>栏里少了一个程序的过渡：其余各项从 <see cref="Old"/> 里的位置收拢到新位置，被去掉的那个原地淡出。</summary>
    private sealed class Removal(TrayDockGeometry old, int index, Item item)
    {
        public TrayDockGeometry Old { get; } = old;
        public int Index { get; } = index;
        public Item Item { get; } = item;
        public SpringAnimation? Anim; // 渲染线程：第一帧才起弹簧
    }

    private volatile Removal? _removal;

    private sealed class Item
    {
        public TrayApp? App;
        public nint Icon, Label;
        public int IconPx;
        public volatile bool HasIcon;
        public string LabelText = "";
        public int LabelMax;
        public double LabelW, LabelH; // 文字图的像素尺寸（UI 线程写，渲染线程读）
        public NineSlice? Highlight;

        // 渲染线程
        public double Hover;
        public bool IconHidden = true, LabelHidden = true;
    }

    private readonly DecorResources _res;
    private readonly OverlayComposition _branch;
    private readonly OverlayComposition _glassBranch; // 毛玻璃单独一支，在栏的装饰下面
    private readonly nint _overlay;
    private readonly MonitorInfo _monitor;
    private readonly List<Item> _pool = []; // UI 线程建、渲染线程读
    private volatile Item[] _items = [];
    private volatile TrayDockGeometry? _geometry;
    private GlassPatch? _glass;
    private NineSlice? _fill, _edge, _tipBg, _tipEdge;
    private double _shapeScale = -1; // 建圆角形状时的缩放（变了要重建）
    private nint _tipText;
    private volatile int _hover = -1; // UI 线程写
    private volatile int _launching = -1; // UI 线程写：正在打开的程序（图标弹跳）
    private int _bounceItem = -1; // 渲染线程：正在弹跳的图标
    private long _bounceStartUs;
    private double _bounceStopAt = double.MaxValue; // 不再打开后，弹完这一下（第几次落地）就停
    private volatile int _tipFor = -1; // 提示文字画的是哪个程序（UI 线程写）
    private double _tipW, _tipH;
    private bool _renderPosted;

    // 自己出现 / 撤走的动画：UI 线程提出请求，渲染线程按本帧时间起弹簧
    private const int RequestNone = 0, RequestShow = 1, RequestAnimateIn = 2, RequestRetract = 3;
    private volatile int _request;
    private SpringAnimation? _appearAnim; // 渲染线程
    private double _appear = 1, _appearVelocity; // 渲染线程：0 = 沉在底边以下，1 = 在位
    private bool _retracted; // 渲染线程：撤走的动画播完了，全部藏起来
    private double _tipShow;
    private bool _tipHidden = true;
    private bool _first = true;
    private long _lastUs;

    public DockDecor(DecorResources res, OverlayComposition branch, OverlayComposition glassBranch, nint overlay, MonitorInfo monitor)
    {
        _res = res;
        _branch = branch;
        _glassBranch = glassBranch;
        _overlay = overlay;
        _monitor = monitor;
    }

    private double Dpi => _monitor.DpiScale;

    /// <summary>
    /// UI 线程：本次会话的栏（null 或没有程序 = 不显示）。<paramref name="apps"/> 与几何的前 <see cref="TrayDockGeometry.Count"/> 个对应。
    /// <paramref name="animateIn"/>：总览已经展开好了，栏用弹簧从底边升起；否则直接跟总览的展开进度走。
    /// </summary>
    public void Prepare(TrayDockGeometry? geometry, IReadOnlyList<TrayApp> apps, bool animateIn)
    {
        HideAll();
        _removal = null;
        if (geometry is null || geometry.Count == 0 || apps.Count < geometry.Count) return;
        var backend = _res.Backend;
        void* root = _branch.Root;
        double k = geometry.Scale;
        if (Math.Abs(k - _shapeScale) > 0.001)
        {
            // 缩放变了：圆角形状重建
            lock (backend.Gate)
            {
                foreach (var s in new[] { _fill, _edge, _tipBg, _tipEdge }) s?.Remove(root);
                foreach (var item in _pool)
                {
                    item.Highlight?.Remove(root);
                    item.Highlight = null;
                }
            }
            _fill = _edge = _tipBg = _tipEdge = null;
            _shapeScale = k;
        }
        // 先建的在下面：底色、描边、各程序（高亮、图标、名字）、提示
        _fill ??= Make(_res.Shape(geometry.Radius, 0, FillColor));
        _edge ??= Make(_res.Shape(geometry.Radius, Math.Max(1, Math.Round(Dpi)), EdgeColor));
        while (_pool.Count < geometry.Count)
        {
            var item = new Item();
            backend.CreateDecorVisual(root, 0, out item.Icon);
            backend.CreateDecorVisual(root, 0, out item.Label);
            _pool.Add(item);
        }
        foreach (var item in _pool) item.Highlight ??= Make(_res.Shape(HighlightRadiusDip * k, 0, HighlightColor));
        _tipBg ??= Make(_res.Shape(TipRadiusDip * Dpi, 0, TipColor));
        _tipEdge ??= Make(_res.Shape(TipRadiusDip * Dpi, Math.Max(1, Math.Round(Dpi)), TipEdgeColor));
        if (_tipText == 0) backend.CreateDecorVisual(root, 0, out _tipText);

        // 毛玻璃：位置在创建时定下（升起时整体平移），栏的大小、位置变了就重建
        nint black = _res.Image("black-1x1", _ => new ShadowImage(1, 1, 1, 0, [0, 0, 0, 255])).Surface;
        if (_glass is { } old && old.Rect != geometry.Dock)
        {
            lock (backend.Gate) old.Remove(_glassBranch.Root);
            _glass = null;
        }
        _glass ??= GlassPatch.Create(backend, _glassBranch.Root, _overlay, geometry.Dock, geometry.Area, geometry.Radius, GlassSigmaDip * Dpi, black);

        var items = _pool.Take(geometry.Count).ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var app = apps[i];
            int iconPx = (int)Math.Round(geometry.Icons[i].Width);
            if (item.App?.Exe != app.Exe || item.IconPx != iconPx)
            {
                item.HasIcon = false;
                item.IconPx = iconPx;
            }
            int max = (int)Math.Round(geometry.Labels[i].Width);
            if (item.LabelText != app.Name || item.LabelMax != max) item.LabelW = 0;
            item.LabelText = app.Name;
            item.LabelMax = max;
            item.App = app;
        }
        _items = items;
        _hover = -1;
        _tipFor = -1;
        _request = animateIn ? RequestAnimateIn : RequestShow;
        _geometry = geometry;
        PostRender();

        NineSlice? Make(NineSliceShape? shape) => shape is null ? null : NineSlice.Create(backend, root, shape);
    }

    /// <summary>
    /// UI 线程：第 <paramref name="index"/> 个程序没了（退出、去掉了托盘图标）：它原地淡出，其余的往里收拢，栏两端跟着收窄
    /// （<paramref name="next"/> 是少了它之后的几何，要正好少一个；否则调用方重新 <see cref="Prepare"/>）。返回是否按动画处理了。
    /// </summary>
    public bool RemoveAt(int index, TrayDockGeometry next)
    {
        var old = _geometry;
        var items = _items;
        if (old is null || index < 0 || index >= items.Length || next.Count != items.Length - 1 || _removal is not null) return false;
        if (next.Dock.Left < old.Dock.Left || next.Dock.Right > old.Dock.Right || next.Dock.Top != old.Dock.Top || next.Dock.Height != old.Dock.Height)
            return false; // 放不下而缩小过的栏少了一个反而变大：没有可收拢的，直接重摆
        _removal = new Removal(old, index, items[index]);
        _items = items.Where((_, k) => k != index).ToArray();
        _hover = -1;
        _tipFor = -1;
        _geometry = next;
        return true;
    }

    /// <summary>UI 线程：总览还开着时撤走栏（换成应用窗口视图）：用弹簧沉到底边以下，播完全部藏起来。</summary>
    public void Retract()
    {
        _hover = -1;
        if (_geometry is not null) _request = RequestRetract;
    }

    /// <summary>
    /// UI 线程：正在打开的程序（-1 = 没有）：它的图标一下一下地弹跳、底下保持高亮，等它的窗口出来。
    /// 撤销时图标弹完正在弹的这一下才停（不在半空中跳回去）。
    /// </summary>
    public void SetLaunching(int index) => _launching = index;

    /// <summary>UI 线程：悬停 / 键盘选中的程序（-1 = 没有）。提示文字按需重画。</summary>
    public void SetHover(int index)
    {
        if (index >= _items.Length) index = -1;
        _hover = index;
        if (index >= 0 && _tipFor != index) PostRender();
    }

    /// <summary>UI 线程：全部隐藏（会话结束、换场景前）。</summary>
    public void HideAll()
    {
        _geometry = null;
        _hover = -1;
        _launching = -1;
        _request = RequestNone;
        _removal = null;
        lock (_res.Backend.Gate)
        {
            _glass?.Place(0, 0);
            foreach (var s in new[] { _fill, _edge, _tipBg, _tipEdge }) s?.Hide();
            foreach (var item in _pool)
            {
                item.Highlight?.Hide();
                foreach (nint v in new[] { item.Icon, item.Label })
                    if (v != 0) Dcomp.SetOpacity((void*)v, 0);
                item.IconHidden = item.LabelHidden = true;
                item.Hover = 0;
            }
            if (_tipText != 0) Dcomp.SetOpacity((void*)_tipText, 0);
            _tipHidden = true;
        }
        _tipShow = 0;
        _first = true;
    }

    /// <summary>
    /// 渲染线程（不提交）：按本帧摆放。栏的位置跟 <paramref name="frame"/> 的展开进度走；
    /// <paramref name="wallpaper"/> 是总览背景里壁纸此刻的不透明度（毛玻璃里的壁纸与它同步变暗）。
    /// </summary>
    public void Apply(in OverviewFrame frame, long nowUs, double wallpaper)
    {
        var g = _geometry;
        var items = _items;
        if (g is null || items.Length != g.Count) return;
        double dt = _first ? 0 : Math.Clamp((nowUs - _lastUs) / 1e6, 0, 0.05);
        _first = false;
        _lastUs = nowUs;

        switch (Interlocked.Exchange(ref _request, RequestNone))
        {
            case RequestShow:
                _appearAnim = null;
                _appear = 1;
                _appearVelocity = 0;
                _retracted = false;
                break;
            case RequestAnimateIn:
                if (_retracted || _appearAnim is null && _appear >= 1) _appear = 0;
                _appearAnim = new SpringAnimation(AppearSpring, _appear, 1, _appearVelocity, nowUs);
                _retracted = false;
                break;
            case RequestRetract:
                _appearAnim = new SpringAnimation(AppearSpring, _appear, 0, _appearVelocity, nowUs);
                break;
        }
        if (_appearAnim is { } anim)
        {
            var (value, velocity, settled) = anim.Sample(nowUs);
            _appear = value;
            _appearVelocity = velocity;
            if (settled)
            {
                _appearAnim = null;
                _appearVelocity = 0;
                _appear = anim.Target;
                if (anim.Target <= 0) _retracted = true;
            }
        }

        // 升起的程度 = 总览展开进度 × 栏自己的出现程度（进度的回弹也照样传过来，与窗口同步）
        double rise = _retracted ? 0 : Math.Clamp(frame.Progress, 0, 1.15) * _appear;
        double dy = Math.Round((1 - rise) * g.HiddenOffset);
        double a = Math.Clamp(rise * 3, 0, 1) * (1 - Math.Clamp(frame.Zoom, 0, 1)) * (1 - 0.5 * Math.Clamp(frame.BarExpansion, 0, 1));
        if (_retracted) a = 0;
        var origin = g.Area;
        int hover = rise > 0.97 ? _hover : -1;
        int launching = _launching;
        var removal = _removal;
        double t = 1;
        if (removal is not null)
        {
            removal.Anim ??= new SpringAnimation(RemoveSpring, 0, 1, 0, nowUs);
            var (value, _, settled) = removal.Anim.Value.Sample(nowUs);
            t = settled ? 1 : Math.Clamp(value, 0, 1.05);
        }
        RectD At(IReadOnlyList<RectD> now, Func<TrayDockGeometry, IReadOnlyList<RectD>> of, int i) =>
            removal is null ? now[i] : RectD.Lerp(of(removal.Old)[i < removal.Index ? i : i + 1], now[i], t);
        if (launching >= 0 && launching != _bounceItem)
        {
            _bounceItem = launching;
            _bounceStartUs = nowUs;
            _bounceStopAt = double.MaxValue;
        }
        double bounce = 0;
        if (_bounceItem >= 0)
        {
            double phase = (nowUs - _bounceStartUs) / 1e6 / BouncePeriod; // 每过 1 落地一次
            if (launching != _bounceItem && _bounceStopAt == double.MaxValue) _bounceStopAt = Math.Ceiling(phase);
            if (phase >= _bounceStopAt) _bounceItem = -1;
            else bounce = Math.Abs(Math.Sin(Math.PI * phase)) * BounceDip * Dpi;
        }
        lock (_res.Backend.Gate)
        {
            var dockRect = removal is null ? g.Dock : RectD.Lerp(removal.Old.Dock, g.Dock, t);
            var dock = Local(dockRect, origin, dy);
            _glass?.Clip(dockRect, origin, g.Radius);
            _glass?.Offset(dy);
            _glass?.Place(a, wallpaper);
            _fill?.Place(dock, a);
            _edge?.Place(dock, a);
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                bool lit = i == hover || i == launching;
                item.Hover = Approach(item.Hover, lit ? 1 : 0, dt, lit ? 0.05 : 0.09);
                item.Highlight?.Place(Local(At(g.Tiles, x => x.Tiles, i), origin, dy), a * item.Hover);
                var icon = Local(At(g.Icons, x => x.Icons, i), origin, dy - (i == _bounceItem ? Math.Round(bounce) : 0));
                if (item.HasIcon) Place(item.Icon, icon.Left, icon.Top, a, ref item.IconHidden);
                if (item.LabelW > 0)
                {
                    var label = Local(At(g.Labels, x => x.Labels, i), origin, dy);
                    Place(item.Label, Math.Round(label.CenterX - item.LabelW / 2), Math.Round(label.CenterY - item.LabelH / 2),
                        a * (0.82 + 0.18 * item.Hover), ref item.LabelHidden);
                }
            }

            if (removal is not null)
            {
                // 被去掉的程序：原地淡出（比收拢快，收拢到一半就看不见了）
                var gone = removal.Item;
                double fade = a * Math.Clamp(1 - 2 * t, 0, 1);
                gone.Highlight?.Place(default, 0);
                var icon = Local(removal.Old.Icons[removal.Index], origin, dy);
                Place(gone.Icon, icon.Left, icon.Top, gone.HasIcon ? fade : 0, ref gone.IconHidden);
                var label = Local(removal.Old.Labels[removal.Index], origin, dy);
                Place(gone.Label, Math.Round(label.CenterX - gone.LabelW / 2), Math.Round(label.CenterY - gone.LabelH / 2), gone.LabelW > 0 ? fade * 0.82 : 0, ref gone.LabelHidden);
                if (removal.Anim is { } done && done.Sample(nowUs).Settled) _removal = null;
            }

            // 名字提示：在栏上方、悬停的程序正上面（不超出屏幕左右）。换到别的程序时直接换，离开栏时立即消失
            bool tip = hover >= 0 && hover < items.Length && _tipFor == hover && _tipW > 0 && a > 0.99;
            _tipShow = tip ? Approach(_tipShow, 1, dt, 0.06) : 0;
            if (tip)
            {
                var tile = Local(g.Tiles[hover], origin, dy);
                double w = _tipW + 2 * TipPadXDip * Dpi, h = _tipH + 2 * TipPadYDip * Dpi;
                double left = Math.Clamp(tile.CenterX - w / 2, 8 * Dpi, Math.Max(8 * Dpi, origin.Width - w - 8 * Dpi));
                var box = new RectD(Math.Round(left), Math.Round(dock.Top - TipGapDip * Dpi - h), Math.Round(w), Math.Round(h));
                _tipBg?.Place(box, _tipShow);
                _tipEdge?.Place(box, _tipShow);
                Place(_tipText, box.Left + TipPadXDip * Dpi, box.Top + TipPadYDip * Dpi, _tipShow, ref _tipHidden);
            }
            else
            {
                _tipBg?.Place(default, 0);
                _tipEdge?.Place(default, 0);
                Place(_tipText, 0, 0, 0, ref _tipHidden);
            }
        }
    }

    private static RectD Local(RectD r, RectD origin, double dy) => r.Offset(-origin.Left, -origin.Top + dy);

    private static double Approach(double value, double target, double dt, double tau)
    {
        if (dt <= 0) return target;
        double next = target + (value - target) * Math.Exp(-dt / tau);
        return Math.Abs(next - target) < 0.002 ? target : next;
    }

    private static void Place(nint visual, double x, double y, double opacity, ref bool hidden)
    {
        if (visual == 0) return;
        if (opacity <= 0.002)
        {
            if (!hidden) Dcomp.SetOpacity((void*)visual, 0);
            hidden = true;
            return;
        }
        hidden = false;
        var m = new Matrix3x2(1, 0, 0, 1, (float)Math.Round(x), (float)Math.Round(y));
        Dcomp.SetTransform((void*)visual, &m);
        Dcomp.SetOpacity((void*)visual, (float)Math.Min(1, opacity));
    }

    private void PostRender()
    {
        if (_renderPosted) return;
        _renderPosted = true;
        _res.Ui.Post(Render);
    }

    /// <summary>UI 线程：补齐图标、名字与提示文字（会话开始后才画，不拖慢第一帧）。</summary>
    private void Render()
    {
        _renderPosted = false;
        var items = _items;
        var backend = _res.Backend;
        var canvas = _res.Canvas;
        foreach (var item in items)
        {
            if (!item.HasIcon && item.App is { } app && item.Icon != 0)
            {
                nint s = _res.Icon(new AppId(app.Exe, app.Exe), item.IconPx, PostRender);
                if (s != 0)
                {
                    lock (backend.Gate) Dcomp.SetContent((void*)item.Icon, (void*)s);
                    item.HasIcon = true;
                }
            }
            if (item.LabelW == 0 && item.LabelText.Length > 0 && item.Label != 0)
            {
                nint s = TextSurface(canvas, item.LabelText, LabelDip, Gdi32.FW_NORMAL, item.LabelMax, shadow: true, out double w, out double h);
                if (s != 0)
                {
                    lock (backend.Gate)
                    {
                        Dcomp.SetContent((void*)item.Label, (void*)s);
                        Dcomp.Release((void*)s);
                        item.LabelW = w;
                        item.LabelH = h;
                    }
                }
            }
        }
        int hover = _hover;
        if (hover >= 0 && hover < items.Length && _tipFor != hover && items[hover].App is { } tipApp && _tipText != 0)
        {
            nint s = TextSurface(canvas, tipApp.Name, TipDip, Gdi32.FW_NORMAL, (int)Math.Round(360 * Dpi), shadow: false, out double w, out double h);
            if (s != 0)
            {
                lock (backend.Gate)
                {
                    Dcomp.SetContent((void*)_tipText, (void*)s);
                    Dcomp.Release((void*)s);
                    _tipW = w;
                    _tipH = h;
                    _tipFor = hover;
                }
            }
        }
    }

    private nint TextSurface(DecorCanvas canvas, string text, double dip, int weight, int max, bool shadow, out double w, out double h)
    {
        w = h = 0;
        float px = (float)(dip * Dpi);
        var (tw, th) = canvas.MeasureText(text, px, weight, max);
        int pad = shadow ? (int)Math.Ceiling(2 * Dpi) : 0;
        int iw = (int)Math.Ceiling(tw) + 2 * pad, ih = (int)Math.Ceiling(th) + 2 * pad;
        if (!canvas.Begin(iw, ih)) return 0;
        if (shadow) WindowDecor.TextWithShadow(canvas, text, pad, pad, max, px, weight, LabelColor, Dpi);
        else canvas.Text(text, 0, 0, max, px, weight, LabelColor);
        nint surface = _res.Upload(canvas.End());
        if (surface != 0)
        {
            w = iw;
            h = ih;
        }
        return surface;
    }

    /// <summary>UI 线程：释放全部视觉（覆盖层销毁前）。</summary>
    public void Dispose()
    {
        _geometry = null;
        _items = [];
        lock (_res.Backend.Gate)
        {
            void* root = _branch.Root;
            foreach (var s in new[] { _fill, _edge, _tipBg, _tipEdge }) s?.Remove(root);
            _glass?.Remove(_glassBranch.Root);
            _glass = null;
            foreach (var item in _pool)
            {
                item.Highlight?.Remove(root);
                foreach (nint v in new[] { item.Icon, item.Label })
                    if (v != 0)
                    {
                        Dcomp.RemoveVisual(root, (void*)v);
                        Dcomp.Release((void*)v);
                    }
            }
            if (_tipText != 0)
            {
                Dcomp.RemoveVisual(root, (void*)_tipText);
                Dcomp.Release((void*)_tipText);
            }
        }
        _pool.Clear();
        _fill = _edge = _tipBg = _tipEdge = null;
        _tipText = 0;
    }
}
