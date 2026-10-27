using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Core.Overview;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using GlideShell.Platform.VirtualDesktops;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 一块屏总览桌面条上的文字与装饰（合成层，在桌面缩略图那一支上面）：桌面名、当前桌面与悬停桌面的高亮、展开后的「×」、末尾的「+」。
/// 随桌面条展开程度逐帧变形：桌面名从顶部一排滑到缩略图下面；当前 / 悬停的高亮从名字底下的圆角块淡变成缩略图外的框；
/// 「+」从小圆块长成与缩略图同高的方块。随总览展开进度淡入、随放大进入别的桌面淡出。
/// 收起时当前 / 悬停桌面名底下是毛玻璃（模糊的壁纸，<see cref="GlassPatch"/>，在桌面条装饰下面单独一支）上叠很淡的白色与描边（Windows 11 Fluent 的半透明填充），
/// 不是不透明的灰块。
/// 线程：Prepare / SetState / HideAll 在 UI 线程；<see cref="Apply"/> 在渲染线程。
/// </summary>
internal sealed unsafe class BarDecor
{
    private const double NameDip = 13, SymbolDip = 16;
    private static readonly D2D.ColorF NameColor = new(0.95f, 0.95f, 0.97f, 1);
    private static readonly D2D.ColorF PillColor = new(1, 1, 1, 0.14f); // 叠在毛玻璃上的白色（当前桌面、悬停的「+」「×」）
    private static readonly D2D.ColorF HoverPillColor = new(1, 1, 1, 0.07f);
    private static readonly D2D.ColorF EdgeColor = new(1, 1, 1, 0.16f); // 玻璃的细描边
    private const double GlassSigmaDip = 12;
    private static readonly D2D.ColorF CurrentRingColor = new(0.93f, 0.93f, 0.95f, 1);
    private static readonly D2D.ColorF HoverRingColor = new(0.42f, 0.67f, 1f, 1);
    private const double DimName = 0.72; // 其余桌面的名字（相当于原来的 185 灰）

    private sealed class Desktop
    {
        public string Name = "";
        public int NameMax;
        public nint Text;
        public double TextW, TextH;
        public NineSlice? CurrentPill, CurrentRing, HoverPill, HoverRing, RemoveDisc, Edge;
        public GlassPatch? Glass;
        public nint RemoveGlyph;

        // 渲染线程
        public double Current, Hover, RemoveHover;
        public bool TextHidden = true, GlyphHidden = true;
    }

    private readonly DecorResources _res;
    private readonly OverlayComposition _branch;
    private readonly OverlayComposition _glassBranch; // 毛玻璃在装饰下面单独一支（重建时不会盖到文字上）
    private readonly nint _overlay;
    private readonly MonitorInfo _monitor;
    private readonly List<Desktop> _desktops = []; // UI 线程建、渲染线程读（会话中不变）
    private volatile Desktop[] _active = [];
    private volatile DesktopBarGeometry? _geometry;
    private volatile int _current = -1, _hoverIndex = -1;
    private volatile DesktopBarPart _hoverPart = DesktopBarPart.None;
    private NineSlice? _addBg, _addBgHover;
    private nint _plusSmall, _plusBig;
    private int _plusSmallSize, _plusBigSize;
    private double _addHover;
    private bool _plusSmallHidden = true, _plusBigHidden = true;
    private bool _first = true;
    private double _thumbRadiusDip = -1;
    private long _lastUs;
    private bool _renderPosted;

    public BarDecor(DecorResources res, OverlayComposition branch, OverlayComposition glassBranch, nint overlay, MonitorInfo monitor)
    {
        _res = res;
        _branch = branch;
        _glassBranch = glassBranch;
        _overlay = overlay;
        _monitor = monitor;
    }

    private double Dpi => _monitor.DpiScale;

    /// <summary>UI 线程：本次会话的桌面条（null = 没有桌面条）。<paramref name="thumbRadiusDip"/> 是桌面缩略图的圆角，高亮框与它同心。</summary>
    public void Prepare(DesktopBarGeometry? geometry, DesktopState state, double thumbRadiusDip = 0)
    {
        if (thumbRadiusDip != _thumbRadiusDip)
        {
            // 圆角变了：高亮框重建
            Dispose();
            _thumbRadiusDip = thumbRadiusDip;
        }
        HideAll();
        if (geometry is null || geometry.Count != state.Count)
        {
            _geometry = null;
            _active = [];
            return;
        }
        var backend = _res.Backend;
        void* root = _branch.Root;
        double dpi = Dpi;
        double pillR = Math.Floor(geometry.AddPill.Height / 2) - 1, removeR = Math.Floor(geometry.RemoveButton(0).Width / 2) - 1;
        while (_desktops.Count < state.Count)
        {
            var d = new Desktop();
            // 先建的在下面：底块、文字、框、「×」
            d.CurrentPill = Make(_res.Shape(pillR, 0, PillColor));
            d.HoverPill = Make(_res.Shape(pillR, 0, HoverPillColor));
            d.Edge = Make(_res.Shape(pillR, Math.Max(1, Math.Round(dpi)), EdgeColor));
            backend.CreateDecorVisual(root, 0, out d.Text);
            d.CurrentRing = Make(_res.Shape((thumbRadiusDip + 2) * dpi, 2 * dpi, CurrentRingColor));
            d.HoverRing = Make(_res.Shape((thumbRadiusDip + 3) * dpi, 3 * dpi, HoverRingColor));
            d.RemoveDisc = Make(_res.Shape(removeR, 0, PillColor));
            var glyph = _res.Image($"bar-x/{dpi:F2}", c => Glyph(c, "×", (int)Math.Round(geometry.RemoveButton(0).Width)));
            if (glyph.Surface != 0) backend.CreateDecorVisual(root, glyph.Surface, out d.RemoveGlyph);
            _desktops.Add(d);
        }
        if (_addBg is null)
        {
            _addBg = Make(_res.Shape(pillR, 0, HoverPillColor));
            _addBgHover = Make(_res.Shape(pillR, 0, PillColor));
            var small = _res.Image($"bar-plus-s/{dpi:F2}", c => Plus(c, (int)Math.Round(geometry.AddPill.Height), 10 * dpi));
            var big = _res.Image($"bar-plus-b/{dpi:F2}", c => Plus(c, (int)Math.Round(26 * dpi), 18 * dpi));
            if (small.Surface != 0 && backend.CreateDecorVisual(root, small.Surface, out _plusSmall) >= 0) _plusSmallSize = small.Width;
            if (big.Surface != 0 && backend.CreateDecorVisual(root, big.Surface, out _plusBig) >= 0) _plusBigSize = big.Width;
        }
        double removeW = geometry.RemoveButton(0).Width;
        nint black = _res.Image("black-1x1", _ => new ShadowImage(1, 1, 1, 0, [0, 0, 0, 255])).Surface;
        for (int i = 0; i < state.Count; i++)
        {
            var d = _desktops[i];
            if (d.Glass is { } old && old.Rect != geometry.Pills[i])
            {
                lock (backend.Gate) old.Remove(_glassBranch.Root);
                d.Glass = null;
            }
            d.Glass ??= GlassPatch.Create(backend, _glassBranch.Root, _overlay, geometry.Pills[i], geometry.Area, pillR, GlassSigmaDip * dpi, black);
            int max = Math.Max(20, (int)Math.Round(geometry.Labels[i].Width - 2 * removeW));
            if (d.Name != state.Names[i] || d.NameMax != max) d.TextW = 0;
            d.Name = state.Names[i];
            d.NameMax = max;
        }
        _active = [.. _desktops.Take(state.Count)];
        _current = state.CurrentIndex;
        _geometry = geometry;
        _first = true;
        if (_active.Any(d => d.TextW == 0) && !_renderPosted)
        {
            _renderPosted = true;
            _res.Ui.Post(RenderNames);
        }

        NineSlice? Make(NineSliceShape? shape) => shape is null ? null : NineSlice.Create(backend, root, shape);
    }

    /// <summary>UI 线程：当前桌面（翻页后）与指针下的东西。</summary>
    public void SetState(int current, DesktopBarHit hovered)
    {
        _current = current;
        _hoverIndex = hovered.Index;
        _hoverPart = hovered.Part;
    }

    /// <summary>UI 线程：全部隐藏（会话结束）。</summary>
    public void HideAll()
    {
        _geometry = null;
        lock (_res.Backend.Gate)
        {
            foreach (var d in _desktops)
            {
                d.CurrentPill?.Hide();
                d.Edge?.Hide();
                d.Glass?.Place(0, 0);
                d.CurrentRing?.Hide();
                d.HoverPill?.Hide();
                d.HoverRing?.Hide();
                d.RemoveDisc?.Hide();
                foreach (nint v in new[] { d.Text, d.RemoveGlyph })
                    if (v != 0) Dcomp.SetOpacity((void*)v, 0);
                d.TextHidden = d.GlyphHidden = true;
            }
            _addBg?.Hide();
            _addBgHover?.Hide();
            foreach (nint v in new[] { _plusSmall, _plusBig })
                if (v != 0) Dcomp.SetOpacity((void*)v, 0);
            _plusSmallHidden = _plusBigHidden = true;
        }
    }

    /// <summary>
    /// 渲染线程（不提交）：按本帧的桌面条展开程度、放大进度摆放。<paramref name="alpha"/> 是整体不透明度（随总览展开淡入），
    /// <paramref name="nowUs"/> 是本帧时间（悬停与当前桌面高亮的过渡用）。
    /// </summary>
    /// <param name="wallpaper">总览背景里壁纸此刻的不透明度（毛玻璃里的壁纸与它同步变暗）。</param>
    public void Apply(in OverviewFrame frame, double alpha, long nowUs, double wallpaper)
    {
        var g = _geometry;
        var desktops = _active;
        if (g is null || desktops.Length != g.Count) return;
        double dt = _first ? 0 : Math.Clamp((nowUs - _lastUs) / 1e6, 0, 0.05); // 新会话：高亮直接到位，不从上一次的状态过渡
        _lastUs = nowUs;
        double e = Math.Clamp(frame.BarExpansion, 0, 1), z = Math.Clamp(frame.Zoom, 0, 1);
        double a = Math.Clamp(alpha, 0, 1) * (1 - z);
        int current = _current, hoverIndex = _hoverIndex;
        var part = _hoverPart;
        var origin = g.Area;
        double dpi = Dpi;
        lock (_res.Backend.Gate)
        {
            for (int i = 0; i < desktops.Length; i++)
            {
                var d = desktops[i];
                bool hovered = hoverIndex == i && part is DesktopBarPart.Desktop or DesktopBarPart.Remove;
                d.Current = _first ? (i == current ? 1 : 0) : Approach(d.Current, i == current ? 1 : 0, dt, 0.07);
                d.Hover = Approach(d.Hover, hovered ? 1 : 0, dt, hovered ? 0.05 : 0.09);
                d.RemoveHover = Approach(d.RemoveHover, hovered && part == DesktopBarPart.Remove ? 1 : 0, dt, 0.05);

                var pill = Local(g.Pills[i], origin);
                var thumb = Local(g.ThumbRect(i, e), origin);
                double hover = d.Hover * (1 - d.Current);
                d.Glass?.Place(a * (1 - e) * Math.Max(d.Current, d.Hover), wallpaper);
                d.CurrentPill?.Place(pill, a * (1 - e) * d.Current);
                d.HoverPill?.Place(pill, a * (1 - e) * hover);
                d.Edge?.Place(pill, a * (1 - e) * Math.Max(d.Current, d.Hover));
                d.CurrentRing?.Place(thumb.Inflate(2 * dpi, 2 * dpi), a * e * d.Current * (1 - d.Hover));
                d.HoverRing?.Place(thumb.Inflate(3 * dpi, 3 * dpi), a * e * d.Hover);

                // 桌面名：收起时在名字条里，展开时在缩略图下面
                if (d.TextW > 0)
                {
                    var label = Local(g.Labels[i], origin);
                    double cx = pill.CenterX + (label.CenterX - pill.CenterX) * e, cy = pill.CenterY + (label.CenterY - pill.CenterY) * e;
                    double bright = DimName + (1 - DimName) * Math.Max(d.Current, d.Hover);
                    Place(d.Text, cx - d.TextW / 2, cy - d.TextH / 2, a * bright, ref d.TextHidden);
                }

                // 「×」：展开后悬停的桌面名右端（最后一个桌面不能删）
                var x = Local(g.RemoveButton(i), origin);
                double removable = desktops.Length >= 2 ? a * e * d.Hover : 0;
                d.RemoveDisc?.Place(x, removable * d.RemoveHover);
                Place(d.RemoveGlyph, x.Left, x.Top, removable * (0.8 + 0.2 * d.RemoveHover), ref d.GlyphHidden);
            }

            // 末尾的「+」：收起时是小圆块（悬停才有底色），展开时是与缩略图同高的方块
            bool addHovered = part == DesktopBarPart.Add;
            _addHover = Approach(_addHover, addHovered ? 1 : 0, dt, addHovered ? 0.05 : 0.09);
            var add = Local(RectD.Lerp(g.AddPill, g.AddTile, e), origin);
            _addBg?.Place(add, a * e * (1 - _addHover));
            _addBgHover?.Place(add, a * _addHover);
            double plus = a * (0.72 + 0.28 * _addHover);
            if (_plusSmall != 0) Place(_plusSmall, add.CenterX - _plusSmallSize / 2.0, add.CenterY - _plusSmallSize / 2.0, plus * (1 - e), ref _plusSmallHidden);
            if (_plusBig != 0) Place(_plusBig, add.CenterX - _plusBigSize / 2.0, add.CenterY - _plusBigSize / 2.0, plus * e, ref _plusBigHidden);
        }
        _first = false;
    }

    private static RectD Local(RectD r, RectD origin) => r.Offset(-origin.Left, -origin.Top);

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

    private void RenderNames()
    {
        _renderPosted = false;
        var canvas = _res.Canvas;
        float px = (float)(NameDip * Dpi);
        foreach (var d in _active)
        {
            if (d.TextW > 0 || d.Text == 0) continue;
            var (tw, th) = canvas.MeasureText(d.Name.Length > 0 ? d.Name : " ", px, Gdi32.FW_SEMIBOLD, d.NameMax);
            int pad = (int)Math.Ceiling(2 * Dpi);
            int w = (int)Math.Ceiling(tw) + 2 * pad, h = (int)Math.Ceiling(th) + 2 * pad;
            if (!canvas.Begin(w, h)) continue;
            WindowDecor.TextWithShadow(canvas, d.Name, pad, pad, d.NameMax, px, Gdi32.FW_SEMIBOLD, NameColor, Dpi);
            nint surface = _res.Upload(canvas.End());
            if (surface == 0) continue;
            lock (_res.Backend.Gate)
            {
                Dcomp.SetContent((void*)d.Text, (void*)surface);
                Dcomp.Release((void*)surface);
                d.TextH = h;
                d.TextW = w;
            }
        }
    }

    /// <summary>居中画一个符号（「×」），白色。</summary>
    private ShadowImage? Glyph(DecorCanvas canvas, string glyph, int size)
    {
        float px = (float)(SymbolDip * Dpi);
        var (tw, th) = canvas.MeasureText(glyph, px, Gdi32.FW_SEMIBOLD, size * 2);
        if (!canvas.Begin(size, size)) return null;
        canvas.Text(glyph, (size - tw) / 2, (size - th) / 2, size * 2, px, Gdi32.FW_SEMIBOLD, new D2D.ColorF(1, 1, 1, 1));
        return canvas.End();
    }

    /// <summary>「+」：两条圆头细线，<paramref name="arm"/> 是整条线的长度。</summary>
    private ShadowImage? Plus(DecorCanvas canvas, int size, double arm)
    {
        if (!canvas.Begin(size, size)) return null;
        float c = size / 2f, h = (float)arm / 2, w = (float)Math.Max(1.5, 1.6 * Dpi);
        var white = new D2D.ColorF(1, 1, 1, 1);
        canvas.Line(c - h, c, c + h, c, w, white);
        canvas.Line(c, c - h, c, c + h, w, white);
        return canvas.End();
    }

    /// <summary>UI 线程：释放全部视觉（覆盖层销毁前）。</summary>
    public void Dispose()
    {
        _geometry = null;
        _active = [];
        lock (_res.Backend.Gate)
        {
            void* root = _branch.Root;
            foreach (var d in _desktops)
            {
                foreach (var s in new[] { d.CurrentPill, d.HoverPill, d.CurrentRing, d.HoverRing, d.RemoveDisc, d.Edge }) s?.Remove(root);
                d.Glass?.Remove(_glassBranch.Root);
                d.Glass = null;
                foreach (nint v in new[] { d.Text, d.RemoveGlyph })
                    if (v != 0)
                    {
                        Dcomp.RemoveVisual(root, (void*)v);
                        Dcomp.Release((void*)v);
                    }
            }
            _addBg?.Remove(root);
            _addBgHover?.Remove(root);
            foreach (nint v in new[] { _plusSmall, _plusBig })
                if (v != 0)
                {
                    Dcomp.RemoveVisual(root, (void*)v);
                    Dcomp.Release((void*)v);
                }
        }
        _desktops.Clear();
        _addBg = _addBgHover = null;
        _plusSmall = _plusBig = 0;
    }
}
