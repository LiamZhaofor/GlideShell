using System.Numerics;
using GlideShell.Core.Geometry;
using GlideShell.Core.Motion;
using GlideShell.Core.Overview;
using GlideShell.Core.Search;
using GlideShell.Platform.Native;
using GlideShell.Platform.Shell;
using static GlideShell.Core.Localization.L;

namespace GlideShell.Platform.Overview;

/// <summary>
/// 总览里打字搜索程序的面板（合成层，在后台程序栏上面、拖动中的窗口下面）：屏幕上部居中一块深色毛玻璃，
/// 上面是搜索框（放大镜、输入的字、闪烁的光标），下面最多 8 行结果（图标、名字，匹配上的字标成蓝色），最底一行按键提示。
/// 面板下面整块覆盖层暗下去（窗口网格与底栏退到后面）。
/// <para>
/// 动画：出现 / 消失用与窗口相同的弹簧（不透明度 + 从上方 14 DIP 落下）；面板高度固定（见 <see cref="Panel"/>），打字时不伸缩；
/// 选中的那一行底下的高亮块平滑移过去。
/// </para>
/// 线程：<see cref="Show"/> / <see cref="Hide"/> / <see cref="HideAll"/> 在 UI 线程（文字、图标在这里画好，在锁里整批设进视觉）；<see cref="Apply"/> 在渲染线程（整帧在锁里）。
/// </summary>
internal sealed unsafe class SearchDecor
{
    // 以下单位都是 DIP
    public const double PanelW = 640, FieldH = 58, RowH = 44, ListPad = 6, FooterH = 34, Radius = 14, SideMargin = 48;
    private const double QueryDip = 21, NameDip = 15, HintDip = 12, IconDip = 28, MagDip = 22, CaretW = 2, CaretH = 24;
    private const double DropDip = 14, Dim = 0.35, GlassSigmaDip = 22;
    private static readonly D2D.ColorF TextColor = new(0.95f, 0.95f, 0.97f, 1);
    private static readonly D2D.ColorF MarkColor = new(0.42f, 0.68f, 1f, 1);
    private static readonly D2D.ColorF HintColor = new(0.78f, 0.79f, 0.83f, 1);
    private static readonly D2D.ColorF FillColor = new(0.11f, 0.11f, 0.13f, 0.62f);
    private static readonly D2D.ColorF EdgeColor = new(1, 1, 1, 0.13f);
    private static readonly D2D.ColorF SelectColor = new(1, 1, 1, 0.12f);
    private static readonly D2D.ColorF DimColor = new(0, 0, 0, 1);
    private static readonly SpringParameters ShowSpring = SpringParameters.Default;

    private sealed class Row
    {
        public nint Icon, Text;
        public double TextW, TextH;
        public bool IconHidden = true, TextHidden = true;
        public volatile bool HasIcon;
    }

    /// <summary>UI 线程写、渲染线程读的一份状态。</summary>
    private sealed record State(bool Open, int Rows, bool Empty, int Selected, double QueryW, double QueryH, double EmptyW, double EmptyH,
        double FooterW, double FooterH);

    private readonly DecorResources _res;
    private readonly OverlayComposition _branch;
    private readonly OverlayComposition _glassBranch; // 下面一支：暗幕与毛玻璃（玻璃要重建时加在暗幕上面、面板下面）
    private readonly nint _overlay;
    private readonly MonitorInfo _monitor;
    private NineSlice? _dim, _fill, _edge, _select;
    private GlassPatch? _glass;
    private nint _query, _caret, _mag, _footer, _empty;
    private readonly Row[] _rows = new Row[AppSearch.MaxResults];
    private volatile State _state = new(false, 0, false, -1, 0, 0, 0, 0, 0, 0);
    private bool _created;
    private readonly double _dimOpacity;

    /// <summary>渲染线程：已经收起而且淡出播完（单独弹出的搜索窗这时可以藏起窗口、停下渲染）。</summary>
    public bool IsGone => !_state.Open && _anim is null && _show <= 0.001;

    // 渲染线程
    private SpringAnimation? _anim;
    private bool _animOpen;
    private double _show, _velocity, _selTop = -1;
    private long _lastUs, _openedUs;
    private long _typedUs; // UI 线程写：最近一次更新输入的时间（光标从这时起先亮着，停下来才闪）
    private bool _hidden = true, _queryHidden = true, _caretHidden = true, _magHidden = true, _footerHidden = true, _emptyHidden = true;

    /// <param name="dim">面板下面整块暗幕的不透明度（总览里 0.35；单独弹出时 0，下面的程序照常看得见，底部提示 Esc 是「关闭」）。</param>
    public SearchDecor(DecorResources res, OverlayComposition branch, OverlayComposition glassBranch, nint overlay, MonitorInfo monitor, double dim = Dim)
    {
        _dimOpacity = dim;
        _res = res;
        _branch = branch;
        _glassBranch = glassBranch;
        _overlay = overlay;
        _monitor = monitor;
    }

    private double K => _monitor.DpiScale;

    /// <summary>
    /// 面板在屏幕上的位置：高度固定为 8 行结果，结果少了下面留空。
    /// 不随结果条数伸缩：面板下面常压着浅色窗口，伸缩时那一大块一会儿露出来、一会儿盖住，打字时看着一闪一闪（用户反馈）。
    /// </summary>
    public RectD Panel()
    {
        var area = _monitor.WorkArea;
        double k = K, w = Math.Round(Math.Min(PanelW * k, area.Width - 2 * SideMargin * k));
        double left = Math.Round(area.CenterX - w / 2), top = Math.Round(area.Top + area.Height * 0.2);
        double h = FieldH * k + 2 * ListPad * k + AppSearch.MaxResults * RowH * k + FooterH * k;
        return new RectD(left, top, w, Math.Round(h));
    }

    private RectD RowRect(RectD panel, int i) =>
        new(panel.Left + 8 * K, Math.Round(panel.Top + (FieldH + ListPad) * K + i * RowH * K), panel.Width - 16 * K, Math.Round(RowH * K));

    /// <summary>UI 线程：指针下的结果行（-1 = 没有）。</summary>
    public int HitTest(double x, double y)
    {
        var s = _state;
        if (!s.Open) return -1;
        var panel = Panel();
        for (int i = 0; i < s.Rows; i++)
            if (RowRect(panel, i).Contains(x, y)) return i;
        return -1;
    }

    /// <summary>UI 线程：指针在面板上。</summary>
    public bool Contains(double x, double y)
    {
        var s = _state;
        return s.Open && Panel().Contains(x, y);
    }

    /// <summary>
    /// UI 线程：显示 / 更新面板：搜索框里是 <paramref name="query"/>，结果 <paramref name="results"/>，选中第 <paramref name="selected"/> 行。
    /// <paramref name="iconOf"/> 给出每条结果的图标标识。
    /// <para>
    /// 先把所有文字、图标画好，再在锁里一次换上：8 行文字要画十几毫秒，边画边换的话渲染线程会提交出新旧混在一起的几帧
    /// （有的行已是新结果、有的还是旧的，光标压在新打的字上），打字时整个列表一闪一闪。
    /// </para>
    /// </summary>
    public void Show(string query, IReadOnlyList<SearchResult> results, int selected, Func<SearchEntry, AppId?> iconOf)
    {
        Create();
        var canvas = _res.Canvas;
        double k = K;
        var panel = Panel();
        double textMax = panel.Width - 64 * k;
        int count = Math.Min(results.Count, _rows.Length);
        int iconPx = (int)Math.Round(IconDip * k);
        var entries = results.Take(count).Select(r => r.Entry).ToArray();

        // 搜索框里的字（光标跟在后面）
        // 还没输入（快捷键打开的）：搜索框空着，只有光标（用户不要灰色的提示字）
        bool typed = query.Length > 0;
        nint q = TextSurface(canvas, query, QueryDip, Gdi32.FW_NORMAL, (int)(panel.Width - 80 * k), [], out double qw, out double qh);
        float advance = typed ? canvas.MeasureAdvance(query, (float)(QueryDip * k), Gdi32.FW_NORMAL) : 0;
        double ew = 0, eh = 0;
        nint empty = results.Count == 0 && _empty != 0
            ? TextSurface(canvas, typed ? T("没有找到程序") : T("从这里打开过的程序会列在这里"), NameDip, Gdi32.FW_NORMAL, (int)textMax, [], out ew, out eh, HintColor)
            : 0;
        var old = _state;
        double fw = old.FooterW, fh = old.FooterH;
        nint footer = old.FooterW == 0 && _footer != 0
            ? TextSurface(canvas, _dimOpacity > 0 ? T("↑↓ 选择　　Enter 打开　　Esc 返回窗口") : T("↑↓ 选择　　Enter 打开　　Esc 关闭"), HintDip, Gdi32.FW_NORMAL, (int)(panel.Width - 40 * k), [], out fw, out fh, HintColor)
            : 0;
        var texts = new (nint Surface, double W, double H)[count];
        var icons = new nint[count];
        for (int i = 0; i < count; i++)
        {
            var r = results[i];
            texts[i].Surface = TextSurface(canvas, r.Entry.Name, NameDip, Gdi32.FW_NORMAL, (int)textMax, r.Highlights, out texts[i].W, out texts[i].H);
            if (iconOf(r.Entry) is not { } id) continue;
            var row = _rows[i];
            var entry = r.Entry;
            int index = i;
            icons[i] = _res.Icon(id, iconPx, () =>
            {
                // 图标晚到：这一行还是它就补上
                if (_shownEntries.Length > index && ReferenceEquals(_shownEntries[index], entry)) SetIcon(row, _res.Icon(id, iconPx, static () => { }));
            });
        }

        lock (_res.Backend.Gate)
        {
            SetContent(_query, q);
            if (empty != 0) SetContent(_empty, empty);
            if (footer != 0) SetContent(_footer, footer);
            for (int i = 0; i < count; i++)
            {
                var row = _rows[i];
                SetContent(row.Text, texts[i].Surface);
                row.TextW = texts[i].W;
                row.TextH = texts[i].H;
                SetIcon(row, icons[i]);
            }
            _shownEntries = entries;
            _typedUs = HostClock.NowUs();
            _state = new State(true, count, results.Count == 0, selected, Math.Max(qw > 0 ? qw : 0, advance), qh > 0 ? qh : QueryDip * k * 1.33,
                ew, eh, fw, fh);
        }
    }

    private SearchEntry[] _shownEntries = [];

    /// <summary>UI 线程：只换选中的行。</summary>
    public void Select(int index)
    {
        var s = _state;
        if (s.Open && s.Selected != index) _state = s with { Selected = index };
    }

    /// <summary>UI 线程：收起面板（用弹簧淡出，播完全部藏起来）。</summary>
    public void Hide()
    {
        var s = _state;
        if (s.Open) _state = s with { Open = false };
    }

    /// <summary>UI 线程：立即全部隐藏（会话结束）。</summary>
    public void HideAll()
    {
        _state = _state with { Open = false, Selected = -1 };
        _shownEntries = [];
        if (!_created) return;
        lock (_res.Backend.Gate)
        {
            HideVisuals();
            _anim = null;
            _show = 0;
            _velocity = 0;
            _selTop = -1;
        }
    }

    /// <summary>
    /// 渲染线程（不提交）：按本帧摆放。<paramref name="wallpaper"/> 是总览背景里壁纸此刻的不透明度（毛玻璃里的壁纸与它同步）。
    /// 返回面板还在动画（弹簧没停、光标在闪）：渲染循环要继续画。
    /// </summary>
    public void Apply(long nowUs, double wallpaper)
    {
        if (!_created) return;
        lock (_res.Backend.Gate) ApplyLocked(nowUs, wallpaper);
    }

    private void ApplyLocked(long nowUs, double wallpaper)
    {
        var s = _state; // 锁内读：Show 在锁里整批换上文字与状态
        double dt = _lastUs == 0 ? 0 : Math.Clamp((nowUs - _lastUs) / 1e6, 0, 0.05);
        _lastUs = nowUs;
        if (s.Open != _animOpen || _anim is null && _show != (s.Open ? 1 : 0))
        {
            _animOpen = s.Open;
            if (s.Open && _show <= 0.001) _openedUs = nowUs;
            _anim = new SpringAnimation(ShowSpring, _show, s.Open ? 1 : 0, _velocity, nowUs);
        }
        if (_anim is { } anim)
        {
            var (value, velocity, settled) = anim.Sample(nowUs);
            _show = value;
            _velocity = velocity;
            if (settled)
            {
                _anim = null;
                _show = anim.Target;
                _velocity = 0;
            }
        }
        {
            if (_show <= 0.001 && !s.Open)
            {
                if (!_hidden) HideVisuals();
                return;
            }
            _hidden = false;
            double k = K, a = Math.Clamp(_show, 0, 1);
            var origin = _monitor.WorkArea;
            var target = Panel();
            double dy = Math.Round((1 - Math.Clamp(_show, 0, 1.2)) * -DropDip * k);
            var panel = target.Offset(0, dy);
            var local = panel.Offset(-origin.Left, -origin.Top);

            _dim?.Place(new RectD(0, 0, origin.Width, origin.Height), _dimOpacity * a);
            _glass?.Offset(dy);
            _glass?.Place(a, wallpaper);
            _fill?.Place(local, a);
            _edge?.Place(local, a);

            // 放大镜、输入的字、光标
            double fieldMid = local.Top + FieldH * k / 2;
            double magPx = Math.Round(MagDip * k);
            Place(_mag, local.Left + 20 * k, fieldMid - magPx / 2, a * 0.85, ref _magHidden);
            double textLeft = local.Left + 56 * k;
            Place(_query, textLeft, Math.Round(fieldMid - s.QueryH / 2), a, ref _queryHidden);
            bool blinkOn = (nowUs - Math.Max(_openedUs, _typedUs)) / 530_000 % 2 == 0;
            Place(_caret, textLeft + s.QueryW + 1 * k, Math.Round(fieldMid - CaretH * k / 2), blinkOn ? a : 0, ref _caretHidden);

            // 结果
            for (int i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                bool on = i < s.Rows;
                var rr = RowRect(panel, i).Offset(-origin.Left, -origin.Top);
                double ra = on ? a : 0;
                double iconPx = Math.Round(IconDip * k);
                Place(row.Icon, rr.Left + 12 * k, Math.Round(rr.CenterY - iconPx / 2), row.HasIcon ? ra : 0, ref row.IconHidden);
                Place(row.Text, rr.Left + 52 * k, Math.Round(rr.CenterY - row.TextH / 2), row.TextW > 0 ? ra : 0, ref row.TextHidden);
            }
            if (s.Empty)
            {
                var er = RowRect(panel, 0).Offset(-origin.Left, -origin.Top);
                Place(_empty, er.Left + 16 * k, Math.Round(er.CenterY - s.EmptyH / 2), a, ref _emptyHidden);
            }
            else Place(_empty, 0, 0, 0, ref _emptyHidden);

            // 选中行的高亮块：平滑移过去
            if (s.Selected >= 0 && s.Selected < s.Rows)
            {
                var sr = RowRect(panel, s.Selected).Offset(-origin.Left, -origin.Top);
                _selTop = _selTop < 0 ? sr.Top : Approach(_selTop, sr.Top, dt, 0.04);
                _select?.Place(new RectD(sr.Left, Math.Round(_selTop), sr.Width, sr.Height), a);
            }
            else
            {
                _selTop = -1;
                _select?.Place(default, 0);
            }

            Place(_footer, local.Left + 20 * k, Math.Round(local.Bottom - FooterH * k / 2 - s.FooterH / 2), a * 0.9, ref _footerHidden);
        }
    }

    // ---------- UI 线程 ----------

    /// <summary>第一次显示时建全部视觉（先建的在下面）：暗幕、毛玻璃（下面一支），底色、描边、选中高亮、各行、放大镜、文字、光标、提示。</summary>
    private void Create()
    {
        var backend = _res.Backend;
        double k = K;
        var max = Panel();
        if (_glass is { } old && old.Rect != max)
        {
            lock (backend.Gate) old.Remove(_glassBranch.Root);
            _glass = null;
        }
        if (_created && _glass is not null) return;
        void* glassRoot = _glassBranch.Root, root = _branch.Root;
        _dim ??= Make(glassRoot, _res.Shape(1, 0, DimColor));
        nint black = _res.Image("black-1x1", _ => new ShadowImage(1, 1, 1, 0, [0, 0, 0, 255])).Surface;
        _glass ??= GlassPatch.Create(backend, glassRoot, _overlay, max, _monitor.WorkArea, Radius * k, GlassSigmaDip * k, black);
        if (_created) return;
        _fill = Make(root, _res.Shape(Radius * k, 0, FillColor));
        _edge = Make(root, _res.Shape(Radius * k, Math.Max(1, Math.Round(k)), EdgeColor));
        _select = Make(root, _res.Shape(8 * k, 0, SelectColor));
        for (int i = 0; i < _rows.Length; i++)
        {
            var row = new Row();
            backend.CreateDecorVisual(root, 0, out row.Icon);
            backend.CreateDecorVisual(root, 0, out row.Text);
            _rows[i] = row;
        }
        backend.CreateDecorVisual(root, 0, out _empty);
        int mag = (int)Math.Round(MagDip * k);
        backend.CreateDecorVisual(root, MagnifierSurface(mag), out _mag);
        backend.CreateDecorVisual(root, 0, out _query);
        var caret = _res.Image($"caret-{k:F2}", c =>
        {
            int w = Math.Max(1, (int)Math.Round(CaretW * k)), h = (int)Math.Round(CaretH * k);
            if (!c.Begin(w, h)) return null;
            c.FillRoundRect(0, 0, w, h, w / 2f, MarkColor);
            return c.End();
        });
        backend.CreateDecorVisual(root, caret.Surface, out _caret);
        backend.CreateDecorVisual(root, 0, out _footer);
        lock (backend.Gate) HideVisuals();
        _created = true;

        NineSlice? Make(void* parent, NineSliceShape? shape) => shape is null ? null : NineSlice.Create(backend, parent, shape);
    }

    private nint MagnifierSurface(int px) => _res.Image($"magnifier-{px}", c =>
    {
        if (!c.Begin(px, px)) return null;
        float s = px, stroke = Math.Max(1.5f, s / 11);
        float r = s * 0.32f, cx = s * 0.42f, cy = s * 0.42f;
        c.StrokeRoundRect(cx - r, cy - r, cx + r, cy + r, r, stroke, HintColor);
        c.Line(cx + r * 0.72f, cy + r * 0.72f, s * 0.9f, s * 0.9f, stroke * 1.1f, HintColor);
        return c.End();
    }).Surface;

    private void SetContent(nint visual, nint surface)
    {
        if (visual == 0) return;
        lock (_res.Backend.Gate)
        {
            Dcomp.SetContent((void*)visual, (void*)surface);
            if (surface != 0) Dcomp.Release((void*)surface);
        }
    }

    private void SetIcon(Row row, nint surface)
    {
        if (row.Icon == 0) return;
        lock (_res.Backend.Gate) Dcomp.SetContent((void*)row.Icon, (void*)surface); // 图标表面归缓存，不释放
        row.HasIcon = surface != 0;
    }

    private nint TextSurface(DecorCanvas canvas, string text, double dip, int weight, int max, IReadOnlyList<int> marked, out double w, out double h,
        D2D.ColorF? color = null)
    {
        w = h = 0;
        if (text.Length == 0) return 0;
        float px = (float)(dip * K);
        var (tw, th) = canvas.MeasureText(text, px, 600, max); // 按半粗体量：标出来的字是半粗体
        int iw = (int)Math.Ceiling(tw) + 2, ih = (int)Math.Ceiling(th);
        if (!canvas.Begin(iw, ih)) return 0;
        if (marked.Count > 0) canvas.MarkedText(text, 0, 0, max, px, weight, color ?? TextColor, marked, MarkColor);
        else canvas.Text(text, 0, 0, max, px, weight, color ?? TextColor);
        nint surface = _res.Upload(canvas.End());
        if (surface != 0)
        {
            w = canvas.MeasureText(text, px, weight, max).Width;
            h = ih;
        }
        return surface;
    }

    // ---------- 锁内 ----------

    private void HideVisuals()
    {
        _hidden = true;
        _dim?.Hide();
        _glass?.Place(0, 0);
        foreach (var s in new[] { _fill, _edge, _select }) s?.Hide();
        foreach (var row in _rows)
        {
            if (row is null) continue;
            Place(row.Icon, 0, 0, 0, ref row.IconHidden);
            Place(row.Text, 0, 0, 0, ref row.TextHidden);
        }
        Place(_query, 0, 0, 0, ref _queryHidden);
        Place(_caret, 0, 0, 0, ref _caretHidden);
        Place(_mag, 0, 0, 0, ref _magHidden);
        Place(_footer, 0, 0, 0, ref _footerHidden);
        Place(_empty, 0, 0, 0, ref _emptyHidden);
    }

    private static double Approach(double value, double target, double dt, double tau)
    {
        if (dt <= 0) return target;
        double next = target + (value - target) * Math.Exp(-dt / tau);
        return Math.Abs(next - target) < 0.3 ? target : next;
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

    /// <summary>UI 线程：释放全部视觉（覆盖层销毁前）。</summary>
    public void Dispose()
    {
        if (!_created && _glass is null && _dim is null) return;
        lock (_res.Backend.Gate)
        {
            void* root = _branch.Root, glassRoot = _glassBranch.Root;
            _dim?.Remove(glassRoot);
            _glass?.Remove(glassRoot);
            foreach (var s in new[] { _fill, _edge, _select }) s?.Remove(root);
            var visuals = new List<nint> { _query, _caret, _mag, _footer, _empty };
            foreach (var row in _rows)
                if (row is not null) visuals.AddRange([row.Icon, row.Text]);
            foreach (nint v in visuals)
                if (v != 0)
                {
                    Dcomp.RemoveVisual(root, (void*)v);
                    Dcomp.Release((void*)v);
                }
        }
        _dim = _fill = _edge = _select = null;
        _glass = null;
        _created = false;
    }
}
