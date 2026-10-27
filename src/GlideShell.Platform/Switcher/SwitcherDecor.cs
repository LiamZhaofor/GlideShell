using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Core.Switcher;
using GlideShell.Platform.Native;
using GlideShell.Platform.Overview;
using GlideShell.Platform.Shell;

namespace GlideShell.Platform.Switcher;

/// <summary>
/// 切换条的文字与装饰（合成层，与总览同一套画法）：选中框跟着连续位置在相邻两项之间滑动；
/// 每个窗口的「程序图标 + 标题」在横带下部，按离中心的距离淡入淡出、随手指略微横移，手指在两项之间时两个标题交叉淡化。
/// 背景是 Fluent 的「烟灰」层：半透明深色圆角面板 + 很淡的亮描边（横带窗口本身透明）。系统的模糊（亚克力）只给激活的窗口画，
/// 切换条从不激活，所以不做模糊，后面的内容隐约透出来。
/// 线程：Prepare / HideAll 在 UI 线程；<see cref="Apply"/> 在渲染线程。
/// </summary>
internal sealed unsafe class SwitcherDecor
{
    private const double IconDip = 20, IconGapDip = 8, TextDip = 15, SlideDip = 48;
    private const double RingGapDip = 4, RingStrokeDip = 3;
    private static readonly D2D.ColorF TextColor = new(0.95f, 0.95f, 0.97f, 1);
    private static readonly D2D.ColorF RingColor = new(0.42f, 0.67f, 1f, 1);
    private static readonly D2D.ColorF PanelColor = new(0.09f, 0.09f, 0.1f, 0.88f); // 不模糊，透得太多后面的文字会显得乱
    private static readonly D2D.ColorF PanelEdgeColor = new(1, 1, 1, 0.1f);
    private const double PanelRadiusDip = 12;

    private sealed class Label(long id)
    {
        public long Id { get; } = id;
        public string Title = "";
        public int TextMax;
        public nint Text, Icon;
        public double TextW, TextH;
        public bool HasIcon;
        public AppId? App;
        public bool TextHidden = true, IconHidden = true; // 渲染线程
    }

    private readonly DecorResources _res;
    private readonly OverlayComposition _branch;
    private readonly MonitorInfo _monitor;
    private readonly RectD _origin; // 横带窗口在屏幕上的位置（视觉坐标相对它）
    private readonly RectD _band; // 横带（面板）在屏幕上的位置
    private readonly Dictionary<long, Label> _cache = []; // UI 线程，按窗口缓存
    private volatile Label?[] _labels = [];
    private volatile SwitcherScene _scene = SwitcherScene.Empty;
    private NineSlice? _ring;
    private readonly OverlayComposition _panelBranch;
    private NineSlice? _panel, _panelEdge;
    private bool _renderPosted;

    /// <param name="origin">横带在屏幕上的位置（视觉坐标相对它）。</param>
    /// <param name="cardRadiusDip">窗口原尺寸下的圆角（DIP）：缩略图按自己的缩放缩小它，选中框与之同心。</param>
    /// <param name="panelBranch">缩略图下面的一支：烟灰面板。</param>
    /// <param name="band">横带（面板）在屏幕上的位置。</param>
    public SwitcherDecor(DecorResources res, OverlayComposition branch, OverlayComposition panelBranch, MonitorInfo monitor, RectD origin, RectD band,
        double cardRadiusDip)
    {
        _band = band;
        _res = res;
        _branch = branch;
        _panelBranch = panelBranch;
        _monitor = monitor;
        _origin = origin;
        CardRadiusDip = cardRadiusDip;
        double r = PanelRadiusDip * monitor.DpiScale;
        if (res.Shape(r, 0, PanelColor) is { } fill) _panel = NineSlice.Create(res.Backend, panelBranch.Root, fill);
        if (res.Shape(r, Math.Max(1, Math.Round(monitor.DpiScale)), PanelEdgeColor) is { } edge) _panelEdge = NineSlice.Create(res.Backend, panelBranch.Root, edge);
    }

    private double _ringRadius = -1; // 建选中框用的圆角（物理像素）

    /// <summary>选中框的圆角：选中的缩略图（放大后）上卡片的圆角加上框与卡片的间距；各窗口缩放不同，取中位数（取到半像素）。</summary>
    private void EnsureRing(SwitcherScene scene)
    {
        var scales = Enumerable.Range(0, scene.Count)
            .Select(i => scene.Layout.ItemRect(i, i).Width / Math.Max(1, scene.Windows[i].Source.Width)).Order().ToList();
        double scale = scales.Count > 0 ? scales[scales.Count / 2] : 0.2;
        double radius = Math.Round((CardRadiusDip * scale + RingGapDip + RingStrokeDip) * Dpi * 2) / 2;
        if (_ring is not null && radius == _ringRadius) return;
        lock (_res.Backend.Gate) _ring?.Remove(_branch.Root);
        _ring = _res.Shape(radius, RingStrokeDip * Dpi, RingColor) is { } shape ? NineSlice.Create(_res.Backend, _branch.Root, shape) : null;
        _ringRadius = radius;
    }

    private double Dpi => _monitor.DpiScale;

    public double CardRadiusDip { get; }

    /// <summary>UI 线程：本次会话的窗口。文字与图标在之后的 UI 消息里画（不拖慢第一帧）。</summary>
    public void Prepare(SwitcherScene scene)
    {
        HideAll();
        EnsureRing(scene);
        int max = (int)Math.Round(scene.Layout.LabelRect(Dpi).Width - (IconDip + IconGapDip) * Dpi);
        var labels = new Label?[scene.Count];
        for (int i = 0; i < scene.Count; i++)
        {
            var w = scene.Windows[i];
            if (!_cache.TryGetValue(w.Id, out var l))
            {
                l = new Label(w.Id);
                if (_res.Backend.CreateDecorVisual(_branch.Root, 0, out l.Text) < 0 || _res.Backend.CreateDecorVisual(_branch.Root, 0, out l.Icon) < 0) continue;
                _cache[w.Id] = l;
            }
            if (l.Title != w.Title || l.TextMax != max) l.TextW = 0;
            l.Title = w.Title;
            l.TextMax = max;
            labels[i] = l;
        }
        // 关掉的窗口：释放
        foreach (var id in _cache.Keys.Where(id => !User32.IsWindow((nint)id)).ToList())
        {
            Release(_cache[id]);
            _cache.Remove(id);
        }
        _labels = labels;
        _scene = scene;
        if (!_renderPosted)
        {
            _renderPosted = true;
            _res.Ui.Post(Render);
        }
    }

    /// <summary>UI 线程：会话结束。</summary>
    public void HideAll()
    {
        _labels = [];
        lock (_res.Backend.Gate)
        {
            _ring?.Hide();
            _panel?.Hide();
            _panelEdge?.Hide();
            foreach (var l in _cache.Values)
            {
                if (l.Text != 0) Dcomp.SetOpacity((void*)l.Text, 0);
                if (l.Icon != 0) Dcomp.SetOpacity((void*)l.Icon, 0);
                l.TextHidden = l.IconHidden = true;
            }
        }
    }

    /// <summary>渲染线程（不提交）：按本帧的连续位置与出现进度摆放。</summary>
    public void Apply(in SwitcherFrame frame)
    {
        var labels = _labels;
        var scene = _scene;
        var layout = scene.Layout;
        int n = Math.Min(labels.Length, scene.Count);
        if (n == 0) return;
        double dpi = Dpi, p = frame.Position;
        // 松手后选中的缩略图飞回窗口原位：面板、选中框、标题随之淡出
        double appear = Math.Clamp(frame.Appear, 0, 1) * (frame.LaunchIndex >= 0 ? 1 - OverviewVisuals.SmoothStep(0, 0.6, frame.Launch) : 1);
        lock (_res.Backend.Gate)
        {
            // 选中框：在 p 两侧的两项之间插值（两端橡皮筋时停在首 / 末项）
            double c = Math.Clamp(p, 0, n - 1);
            int f = (int)Math.Floor(c);
            int g = Math.Min(n - 1, f + 1);
            var rect = RectD.Lerp(layout.ItemRect(f, p), layout.ItemRect(g, p), c - f);
            double out_ = (RingGapDip + RingStrokeDip) * dpi;
            _ring?.Place(rect.Offset(-_origin.Left, -_origin.Top).Inflate(out_, out_), appear);
            var band = _band.Offset(-_origin.Left, -_origin.Top);
            _panel?.Place(band, appear);
            _panelEdge?.Place(band, appear);

            var box = layout.LabelRect(dpi).Offset(-_origin.Left, -_origin.Top);
            double iconPx = Math.Round(IconDip * dpi), gapPx = Math.Round(IconGapDip * dpi);
            for (int i = 0; i < n; i++)
            {
                if (labels[i] is not { } l || l.TextW <= 0) continue;
                double d = i - p;
                double alpha = appear * Math.Clamp(1 - Math.Abs(d), 0, 1);
                double total = (l.HasIcon ? iconPx + gapPx : 0) + l.TextW;
                double x0 = Math.Round(box.CenterX - total / 2 + d * SlideDip * dpi);
                if (l.HasIcon) Place(l.Icon, x0, box.CenterY - iconPx / 2, alpha, ref l.IconHidden);
                Place(l.Text, x0 + (l.HasIcon ? iconPx + gapPx : 0), box.CenterY - l.TextH / 2, alpha, ref l.TextHidden);
            }
        }
    }

    private static void Place(nint visual, double x, double y, double opacity, ref bool hidden)
    {
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

    private void Render()
    {
        _renderPosted = false;
        var canvas = _res.Canvas;
        float px = (float)(TextDip * Dpi);
        int iconPx = (int)Math.Round(IconDip * Dpi);
        foreach (var l in _labels.OfType<Label>())
        {
            l.App ??= AppIcons.AppOf((nint)l.Id);
            if (!l.HasIcon)
            {
                nint s = _res.Icon(l.App.Value, iconPx, () =>
                {
                    if (_renderPosted) return;
                    _renderPosted = true;
                    _res.Ui.Post(Render);
                });
                if (s != 0)
                {
                    lock (_res.Backend.Gate) Dcomp.SetContent((void*)l.Icon, (void*)s);
                    l.HasIcon = true;
                }
            }
            if (l.TextW > 0) continue;
            var (tw, th) = canvas.MeasureText(l.Title.Length > 0 ? l.Title : " ", px, Gdi32.FW_NORMAL, l.TextMax);
            int pad = (int)Math.Ceiling(2 * Dpi);
            int w = (int)Math.Ceiling(tw) + 2 * pad, h = (int)Math.Ceiling(th) + 2 * pad;
            if (!canvas.Begin(w, h)) continue;
            WindowDecor.TextWithShadow(canvas, l.Title, pad, pad, l.TextMax, px, Gdi32.FW_NORMAL, TextColor, Dpi);
            nint surface = _res.Upload(canvas.End());
            if (surface == 0) continue;
            lock (_res.Backend.Gate)
            {
                Dcomp.SetContent((void*)l.Text, (void*)surface);
                Dcomp.Release((void*)surface);
                l.TextH = h;
                l.TextW = w;
            }
        }
    }

    private void Release(Label l)
    {
        lock (_res.Backend.Gate)
            foreach (nint v in new[] { l.Text, l.Icon })
                if (v != 0)
                {
                    Dcomp.RemoveVisual(_branch.Root, (void*)v);
                    Dcomp.Release((void*)v);
                }
    }

    /// <summary>UI 线程：释放全部视觉（横带销毁前）。</summary>
    public void Dispose()
    {
        _labels = [];
        foreach (var l in _cache.Values) Release(l);
        _cache.Clear();
        lock (_res.Backend.Gate)
        {
            _ring?.Remove(_branch.Root);
            _panel?.Remove(_panelBranch.Root);
            _panelEdge?.Remove(_panelBranch.Root);
        }
        _ring = _panel = _panelEdge = null;
    }
}
