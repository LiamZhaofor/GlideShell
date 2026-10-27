using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 一块屏总览里窗口的装饰，全部是合成层里的视觉（在窗口缩略图那一支上面）：标题行（程序图标 + 标题）、悬停框、关闭按钮、
/// 没有画面的最小化窗口的占位卡片。每帧跟着窗口当前的矩形走、按进度淡入淡出，所以翻页、补位、拖动、展开桌面条时都不用藏起来。
/// 悬停框与关闭按钮的出现 / 消失、拖动窗口时它的标题隐去都是渲染线程上按时间常数逼近的小动画。
/// 视觉按窗口缓存、跨会话复用（标题或可用宽度变了才重画文字）；文字与图标在会话开始后才画（不拖慢第一帧）。
/// 线程：Prepare / ReplaceScene / SetHover 等在 UI 线程；<see cref="BeginFrame"/> / <see cref="Apply"/> 在渲染线程。
/// </summary>
internal sealed unsafe class WindowDecor
{
    private const double IconDip = 18, IconGapDip = 6, TextDip = 13.5, LabelTopDip = 4, LabelBoxDip = 32;
    private const double RingGapDip = 3, RingStrokeDip = 3, RingRadiusDip = 9, CardRadiusDip = 8, CardIconDip = 48;
    private static readonly D2D.ColorF TextColor = new(0.89f, 0.89f, 0.91f, 1);
    private static readonly D2D.ColorF RingColor = new(0.42f, 0.67f, 1f, 1);
    private static readonly D2D.ColorF CardColor = new(0.16f, 0.16f, 0.18f, 1);

    private sealed class Entry(long id)
    {
        public long Id { get; } = id;
        public AppId? App;
        public string Title = "";
        public int TextMax; // 按这个宽度画的文字（0 = 还没画）
        public int WantTextMax;
        public nint Text, Icon, Close;
        public NineSlice? Ring, Card;
        public nint CardIcon;
        public bool WantsCard;
        public volatile bool HasIcon, HasCardIcon;
        public double TextW, TextH; // 文字图的像素尺寸（UI 线程写，渲染线程读）

        // 渲染线程
        public double Show = 1, Hover;
        public bool TextHidden = true, IconHidden = true, CloseHidden = true, CardIconHidden = true;
    }

    private readonly DecorResources _res;
    private readonly OverlayComposition _branch;
    private readonly MonitorInfo _monitor;
    private readonly Dictionary<long, Entry> _entries = []; // UI 线程
    private volatile Entry?[][] _slots = [];
    private volatile int _hoverPage = -1, _hoverIndex = -1;
    private long _lastUs;
    private double _dt;
    private bool _renderPosted;

    public WindowDecor(DecorResources res, OverlayComposition branch, MonitorInfo monitor)
    {
        _res = res;
        _branch = branch;
        _monitor = monitor;
    }

    private double Dpi => _monitor.DpiScale;

    /// <summary>
    /// UI 线程：本次会话的各页（没有多页时只有一页）。<paramref name="members"/>[k] 是第 k 页里画在本屏的窗口，
    /// <paramref name="showsContent"/>(k, i) 表示那个窗口有没有缩略图画面（没有的最小化窗口画占位卡片）。
    /// </summary>
    public void Prepare(IReadOnlyList<OverviewScene> scenes, IReadOnlyList<IReadOnlyList<int>> members, Func<int, int, bool> showsContent)
    {
        HideAll();
        var used = new HashSet<long>();
        var slots = new Entry?[scenes.Count][];
        for (int k = 0; k < scenes.Count; k++)
        {
            var scene = scenes[k];
            slots[k] = new Entry?[scene.Count];
            foreach (int i in members[k])
            {
                var w = scene.Windows[i];
                var e = Ensure(w.Id);
                if (e is null) continue;
                used.Add(w.Id);
                Update(e, scene, i, w.IsMinimized && !showsContent(k, i));
                slots[k][i] = e;
            }
        }
        // 关掉的窗口：释放它的视觉
        foreach (var id in _entries.Keys.Where(id => !used.Contains(id) && !User32.IsWindow((nint)id)).ToList())
        {
            Release(_entries[id]);
            _entries.Remove(id);
        }
        _slots = slots;
        PostRender();
    }

    /// <summary>UI 线程：第 <paramref name="page"/> 页的场景换了（窗口移走后补位）：移走的不再画，其余按新的平铺宽度重画标题。</summary>
    public void ReplaceScene(int page, OverviewScene scene)
    {
        var slots = _slots;
        if (page < 0 || page >= slots.Length) return;
        var row = (Entry?[])slots[page].Clone();
        for (int i = 0; i < row.Length && i < scene.Count; i++)
        {
            if (row[i] is not { } e) continue;
            if (scene.IsRemoved(i))
            {
                row[i] = null;
                if (!slots.Where((_, k) => k != page).Any(r => r.Contains(e))) Hide(e);
                continue;
            }
            Update(e, scene, i, e.WantsCard);
        }
        _slots = Replace(slots, page, row);
        PostRender();
    }

    /// <summary>UI 线程：第 <paramref name="page"/> 页加入了一个窗口（拖到了那个桌面），它是 <paramref name="scene"/> 的最后一个。</summary>
    public void AddWindow(int page, OverviewScene scene, bool showsContent)
    {
        var slots = _slots;
        if (page < 0 || page >= slots.Length || scene.Count == 0) return;
        int index = scene.Count - 1;
        var w = scene.Windows[index];
        var row = new Entry?[scene.Count];
        Array.Copy(slots[page], row, Math.Min(row.Length, slots[page].Length));
        if (Ensure(w.Id) is { } e)
        {
            e.Show = 1;
            Update(e, scene, index, w.IsMinimized && !showsContent);
            row[index] = e;
        }
        _slots = Replace(slots, page, row);
        PostRender();
    }

    /// <summary>UI 线程：悬停（或键盘选中）第 <paramref name="page"/> 页的第 <paramref name="index"/> 个窗口；-1 = 没有。</summary>
    public void SetHover(int page, int index)
    {
        var slots = _slots;
        if (index >= 0 && page >= 0 && page < slots.Length && index < slots[page].Length && slots[page][index] is { } e)
        {
            // 悬停框与关闭按钮第一次用到时才建
            var backend = _res.Backend;
            if (e.Ring is null && _res.Shape(RingRadiusDip * Dpi, RingStrokeDip * Dpi, RingColor) is { } ring)
                e.Ring = NineSlice.Create(backend, _branch.Root, ring);
            if (e.Close == 0)
            {
                nint surface = _res.CloseButtonSurface((int)Math.Round(CloseButton.DiameterDip * Dpi));
                if (surface != 0 && backend.CreateDecorVisual(_branch.Root, surface, out nint close) >= 0) e.Close = close;
            }
        }
        _hoverPage = page;
        _hoverIndex = index;
    }

    /// <summary>UI 线程：全部隐藏（会话结束），渲染线程的动画状态复位。</summary>
    public void HideAll()
    {
        _slots = [];
        _hoverPage = _hoverIndex = -1;
        foreach (var e in _entries.Values) Hide(e);
        _lastUs = 0;
    }

    // ---------- 渲染线程 ----------

    /// <summary>渲染线程：每帧开始时调用一次（算出这一帧的时间步长）。</summary>
    public void BeginFrame(long nowUs)
    {
        _dt = _lastUs == 0 ? 0 : Math.Clamp((nowUs - _lastUs) / 1e6, 0, 0.05);
        _lastUs = nowUs;
    }

    /// <summary>
    /// 渲染线程：按第 <paramref name="page"/> 页本帧的窗口矩形（屏幕坐标）与不透明度摆放装饰（不提交）。
    /// <paramref name="labels"/> 是标题整体的不透明度（随展开进度淡入）；<paramref name="dragIndex"/> 是正在拖动的窗口（它的标题隐去）。
    /// </summary>
    public void Apply(int page, OverviewScene scene, ReadOnlySpan<RectD> rects, ReadOnlySpan<double> opacity, double labels, int dragIndex)
    {
        var slots = _slots;
        if (page < 0 || page >= slots.Length) return;
        var row = slots[page];
        double dpi = Dpi, dt = _dt;
        var origin = _monitor.WorkArea;
        int hoverIndex = _hoverPage == page ? _hoverIndex : -1;
        double iconPx = Math.Round(IconDip * dpi), gapPx = Math.Round(IconGapDip * dpi), cardIconPx = Math.Round(CardIconDip * dpi);
        double ringOut = (RingGapDip + RingStrokeDip) * dpi;
        lock (_res.Backend.Gate)
        {
            int n = Math.Min(row.Length, Math.Min(scene.Count, Math.Min(rects.Length, opacity.Length)));
            for (int i = 0; i < n; i++)
            {
                if (row[i] is not { } e) continue;
                var r = rects[i].Offset(-origin.Left, -origin.Top);
                double op = scene.IsPresent(i) ? Math.Clamp(opacity[i], 0, 1) : 0;
                e.Show = Approach(e.Show, i == dragIndex ? 0 : 1, dt, 0.06);

                // 占位卡片：代替缩略图，跟窗口一样的不透明度
                if (e.Card is { } card)
                {
                    card.Place(r, op);
                    if (e.HasCardIcon) SetVisual(e.CardIcon, r.CenterX - cardIconPx / 2, r.CenterY - cardIconPx / 2, op, ref e.CardIconHidden);
                }

                // 标题行：图标 + 文字，整体在窗口下方居中
                double alpha = labels * op * e.Show;
                if (e.TextW > 0)
                {
                    bool icon = e.HasIcon;
                    double total = (icon ? iconPx + gapPx : 0) + e.TextW;
                    double x0 = Math.Round(r.CenterX - total / 2), top = r.Bottom + LabelTopDip * dpi, box = LabelBoxDip * dpi;
                    if (icon) SetVisual(e.Icon, x0, top + (box - iconPx) / 2, alpha, ref e.IconHidden);
                    SetVisual(e.Text, x0 + (icon ? iconPx + gapPx : 0), top + (box - e.TextH) / 2, alpha, ref e.TextHidden);
                }

                // 悬停框与关闭按钮：淡入快、淡出稍慢
                double target = i == hoverIndex ? 1 : 0;
                e.Hover = Approach(e.Hover, target, dt, target > e.Hover ? 0.045 : 0.08);
                double hover = e.Hover * op * e.Show;
                e.Ring?.Place(r.Inflate(ringOut, ringOut), hover);
                if (e.Close != 0)
                {
                    var b = CloseButton.Rect(r, dpi);
                    SetVisual(e.Close, b.Left, b.Top, hover, ref e.CloseHidden);
                }
            }
        }
    }

    private static double Approach(double value, double target, double dt, double tau)
    {
        if (dt <= 0) return value;
        double next = target + (value - target) * Math.Exp(-dt / tau);
        return Math.Abs(next - target) < 0.002 ? target : next;
    }

    /// <summary>锁内：把一个不缩放的视觉摆到整像素位置；不透明度为 0 时只设一次。</summary>
    private static void SetVisual(nint visual, double x, double y, double opacity, ref bool hidden)
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

    // ---------- UI 线程的小工具 ----------

    private Entry? Ensure(long id)
    {
        if (_entries.TryGetValue(id, out var e)) return e;
        var backend = _res.Backend;
        e = new Entry(id);
        if (backend.CreateDecorVisual(_branch.Root, 0, out e.Text) < 0 || backend.CreateDecorVisual(_branch.Root, 0, out e.Icon) < 0)
        {
            Release(e);
            return null;
        }
        _entries[id] = e;
        return e;
    }

    private void Update(Entry e, OverviewScene scene, int index, bool wantsCard)
    {
        var w = scene.Windows[index];
        double tile = scene.Slots[index].Tile.Width;
        e.WantTextMax = Math.Max(24, (int)Math.Round(tile - (IconDip + IconGapDip) * Dpi));
        if (e.Title != w.Title) e.TextMax = 0; // 标题变了：重画
        e.Title = w.Title;
        if (wantsCard && e.Card is null && _res.Shape(CardRadiusDip * Dpi, 0, CardColor) is { } shape)
        {
            e.Card = NineSlice.Create(_res.Backend, _branch.Root, shape);
            if (_res.Backend.CreateDecorVisual(_branch.Root, 0, out nint icon) >= 0) e.CardIcon = icon;
        }
        e.WantsCard = wantsCard;
        if (!wantsCard && e.Card is not null) lock (_res.Backend.Gate) e.Card.Hide();
    }

    /// <summary>会话开始后把文字与图标画出来（投递到 UI 线程，第一帧之后才做）。</summary>
    private void PostRender()
    {
        if (_renderPosted) return;
        _renderPosted = true;
        _res.Ui.Post(RenderPending);
    }

    private void RenderPending()
    {
        _renderPosted = false;
        var backend = _res.Backend;
        var canvas = _res.Canvas;
        float textPx = (float)(TextDip * Dpi);
        foreach (var e in _slots.SelectMany(r => r).OfType<Entry>().Distinct())
        {
            e.App ??= AppIcons.AppOf((nint)e.Id);
            if (!e.HasIcon || (e.WantsCard && !e.HasCardIcon)) LoadIcons(e);
            if (e.TextMax == e.WantTextMax) continue;
            var (tw, th) = canvas.MeasureText(e.Title.Length > 0 ? e.Title : " ", textPx, Gdi32.FW_NORMAL, e.WantTextMax);
            int pad = (int)Math.Ceiling(2 * Dpi);
            int width = (int)Math.Ceiling(tw) + 2 * pad, height = (int)Math.Ceiling(th) + 2 * pad;
            if (!canvas.Begin(width, height)) continue;
            TextWithShadow(canvas, e.Title, pad, pad, e.WantTextMax, textPx, Gdi32.FW_NORMAL, TextColor, Dpi);
            nint surface = _res.Upload(canvas.End());
            if (surface == 0) continue;
            lock (backend.Gate)
            {
                Dcomp.SetContent((void*)e.Text, (void*)surface);
                Dcomp.Release((void*)surface); // 视觉持有它
                e.TextH = height;
                e.TextW = width;
            }
            e.TextMax = e.WantTextMax;
        }
    }

    /// <summary>
    /// 浅色文字 + 一圈很淡的深色描边与向下 1 像素的投影：直接压在壁纸上，壁纸再亮也看得清（不加底色块，和 macOS 一样干净）。
    /// </summary>
    public static void TextWithShadow(DecorCanvas canvas, string text, float x, float y, float maxWidth, float sizePx, int weight, D2D.ColorF color, double dpi)
    {
        float d = (float)Math.Max(1, Math.Round(dpi));
        var halo = new D2D.ColorF(0, 0, 0, 0.16f);
        for (int k = 0; k < 8; k++)
        {
            double a = k * Math.PI / 4;
            canvas.Text(text, x + (float)Math.Cos(a) * d, y + (float)Math.Sin(a) * d, maxWidth, sizePx, weight, halo);
        }
        canvas.Text(text, x, y + d, maxWidth, sizePx, weight, new D2D.ColorF(0, 0, 0, 0.4f));
        canvas.Text(text, x, y, maxWidth, sizePx, weight, color);
    }

    private void LoadIcons(Entry e)
    {
        if (e.App is not { } app) return;
        var backend = _res.Backend;
        if (!e.HasIcon)
        {
            nint s = _res.Icon(app, (int)Math.Round(IconDip * Dpi), PostRender);
            if (s != 0)
            {
                lock (backend.Gate) Dcomp.SetContent((void*)e.Icon, (void*)s);
                e.HasIcon = true;
            }
        }
        if (e.WantsCard && !e.HasCardIcon && e.CardIcon != 0)
        {
            nint s = _res.Icon(app, (int)Math.Round(CardIconDip * Dpi), PostRender);
            if (s != 0)
            {
                lock (backend.Gate) Dcomp.SetContent((void*)e.CardIcon, (void*)s);
                e.HasCardIcon = true;
            }
        }
    }

    private void Hide(Entry e)
    {
        lock (_res.Backend.Gate)
        {
            foreach (nint v in new[] { e.Text, e.Icon, e.Close, e.CardIcon })
                if (v != 0) Dcomp.SetOpacity((void*)v, 0);
            e.Ring?.Hide();
            e.Card?.Hide();
            e.TextHidden = e.IconHidden = e.CloseHidden = e.CardIconHidden = true;
            e.Show = 1;
            e.Hover = 0;
        }
    }

    private void Release(Entry e)
    {
        lock (_res.Backend.Gate)
        {
            void* root = _branch.Root;
            foreach (nint v in new[] { e.Text, e.Icon, e.Close, e.CardIcon })
                if (v != 0)
                {
                    Dcomp.RemoveVisual(root, (void*)v);
                    Dcomp.Release((void*)v);
                }
            e.Ring?.Remove(root);
            e.Card?.Remove(root);
        }
    }

    /// <summary>UI 线程：释放全部视觉（覆盖层销毁前）。</summary>
    public void Dispose()
    {
        _slots = [];
        foreach (var e in _entries.Values) Release(e);
        _entries.Clear();
    }

    private static Entry?[][] Replace(Entry?[][] slots, int page, Entry?[] row)
    {
        var next = (Entry?[][])slots.Clone();
        next[page] = row;
        return next;
    }
}
