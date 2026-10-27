using GlideShell.Core.Geometry;
using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui.Settings;

/// <summary>设置窗口里的一个元素的种类。</summary>
internal enum ItemKind
{
    /// <summary>左侧导航项。</summary>
    Nav,

    /// <summary>页标题。</summary>
    Title,

    /// <summary>分组小标题。</summary>
    Section,

    /// <summary>一段说明文字（可换行）。</summary>
    Paragraph,

    /// <summary>卡片：图标 + 标题 + 说明，右边可以挂一个控件（<see cref="Item.Control"/>）。</summary>
    Card,

    Toggle,
    Dropdown,
    Slider,

    /// <summary>几个并排的选项（灵敏度预设）。</summary>
    Segmented,

    Button,
    AccentButton,

    /// <summary>可展开的卡片（「细调」）：点它展开 / 收起下面的卡片。</summary>
    Expander,

    /// <summary>手势示意：触摸板上几根手指 + 方向箭头。</summary>
    Gesture,

    /// <summary>屏幕示意（触发角）：四个角各一个下拉。</summary>
    Screen,

    /// <summary>屏幕示意上的一个角。</summary>
    Corner,

    /// <summary>顶部的提示条（设置有冲突时）。</summary>
    InfoBar,

    /// <summary>输入框的外框（里面是系统的编辑框）。</summary>
    EditBox,
}

/// <summary>
/// 设置窗口里的一个元素（每次布局重新生成；矩形是物理像素，内容区的元素不含滚动偏移）。
/// 交互元素用委托读写模型：开关 <see cref="On"/> / <see cref="SetOn"/>，下拉与分段 <see cref="Index"/> / <see cref="SetIndex"/>，
/// 滑块 <see cref="Value"/> / <see cref="SetValue"/>，按钮 <see cref="Click"/>。
/// </summary>
internal sealed class Item(ItemKind kind, string key)
{
    public ItemKind Kind { get; } = kind;

    /// <summary>稳定的标识（跨布局保持悬停、焦点与动画）。</summary>
    public string Key { get; } = key;

    public RectD Rect { get; set; }
    public string Text { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Icon { get; init; } = "";
    public bool Enabled { get; init; } = true;

    /// <summary>卡片文字往右让出的宽度（物理像素，左边放手势示意时用）。</summary>
    public double Indent { get; init; }
    public bool Selected { get; init; }

    public Func<bool>? On { get; init; }
    public Action<bool>? SetOn { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];
    public Func<int>? Index { get; init; }
    public Action<int>? SetIndex { get; init; }

    public double Min { get; init; }
    public double Max { get; init; } = 1;
    public double Step { get; init; } = 0.1;
    public Func<double>? Value { get; init; }
    public Action<double>? SetValue { get; init; }
    public Func<double, string>? Format { get; init; }

    public Action? Click { get; init; }

    /// <summary>卡片右边的控件（与卡片一起布局，单独参与命中与焦点）。</summary>
    public Item? Control { get; set; }

    /// <summary>手势示意：手指数与方向（0 上下、1 左右）。</summary>
    public int Fingers { get; init; }
    public bool Horizontal { get; init; }

    /// <summary>能获得键盘焦点、能点。</summary>
    public bool Interactive => Enabled && Kind is ItemKind.Nav or ItemKind.Toggle or ItemKind.Dropdown or ItemKind.Slider or ItemKind.Segmented
        or ItemKind.Button or ItemKind.AccentButton or ItemKind.Expander or ItemKind.Corner;

    /// <summary>分段控件（屏幕上在 <paramref name="r"/>）里第 <paramref name="i"/> 段的矩形。</summary>
    public RectD Segment(RectD r, int i)
    {
        int n = Math.Max(1, Options.Count);
        double w = r.Width / n;
        return new RectD(r.Left + w * i, r.Top, w, r.Height);
    }
}

/// <summary>元素的画法（Windows 11 Fluent 控件的样子）。</summary>
internal static class ItemPainter
{
    /// <param name="hover">指针在它上面。</param>
    /// <param name="pressed">正按着它。</param>
    /// <param name="focus">键盘焦点在它上面（画焦点框）。</param>
    /// <param name="anim">开关滑块的位置（0 关 ~ 1 开），用于过渡动画。</param>
    public static void Paint(UiCanvas c, FluentTheme t, double s, Item it, RectD r, bool hover, bool pressed, bool focus, double anim)
    {
        var body = new UiFont(FluentTheme.Font, 14 * s);
        var caption = new UiFont(FluentTheme.Font, 12 * s, 400, Wrap: true);
        var icon = new UiFont(FluentTheme.IconFont, 16 * s);
        double radius = 4 * s;
        var text = it.Enabled ? t.TextPrimary : t.TextTertiary;
        switch (it.Kind)
        {
            case ItemKind.Nav:
            {
                if (it.Selected || hover) c.Fill(r, radius, it.Selected ? t.Subtle : pressed ? t.SubtlePressed : t.Subtle);
                if (it.Selected) c.Fill(new RectD(r.Left, r.CenterY - 8 * s, 3 * s, 16 * s), 1.5 * s, t.Accent);
                c.Text(it.Icon, new RectD(r.Left + 14 * s, r.Top, 20 * s, r.Height), icon, t.TextPrimary, UiAlign.Center);
                c.Text(it.Text, new RectD(r.Left + 46 * s, r.Top, r.Width - 52 * s, r.Height), body, t.TextPrimary);
                break;
            }
            case ItemKind.Title:
                c.Text(it.Text, r, new UiFont(FluentTheme.DisplayFont, 28 * s, 600), t.TextPrimary, UiAlign.Left, center: false);
                break;
            case ItemKind.Section:
                c.Text(it.Text, r, new UiFont(FluentTheme.Font, 14 * s, 600), t.TextPrimary, UiAlign.Left, center: false);
                break;
            case ItemKind.Paragraph:
                c.Text(it.Text, r, new UiFont(FluentTheme.Font, 13 * s, 400, Wrap: true), t.TextSecondary, UiAlign.Left, center: false);
                break;
            case ItemKind.InfoBar:
            {
                c.Fill(r, radius, t.Dark ? new D2D.ColorF(0.26f, 0.21f, 0.10f, 1) : new D2D.ColorF(1f, 0.957f, 0.808f, 1));
                c.Stroke(r, radius, 1, t.CardStroke);
                c.Text(FluentTheme.Icons.Warning, new RectD(r.Left + 16 * s, r.Top + 14 * s, 18 * s, 20 * s), icon, t.Caution, UiAlign.Center, center: false);
                c.Text(it.Text, new RectD(r.Left + 46 * s, r.Top + 14 * s, r.Width - 62 * s, r.Height - 20 * s), new UiFont(FluentTheme.Font, 13 * s, 400, Wrap: true),
                    t.TextPrimary, UiAlign.Left, center: false);
                break;
            }
            case ItemKind.Card:
            case ItemKind.Expander:
            {
                c.Fill(r, radius, hover && it.Kind == ItemKind.Expander ? t.CardHover : t.Card);
                c.Stroke(r, radius, 1, t.CardStroke);
                double x = r.Left + 16 * s + it.Indent;
                if (it.Icon.Length > 0)
                {
                    c.Text(it.Icon, new RectD(x, r.Top, 20 * s, r.Height), new UiFont(FluentTheme.IconFont, 20 * s), text, UiAlign.Center);
                    x += 40 * s;
                }
                double right = (it.Control?.Rect.Left ?? (it.Kind == ItemKind.Expander ? r.Right - 48 * s : r.Right)) - 16 * s;
                double w = Math.Max(40 * s, right - x);
                if (it.Sub.Length > 0)
                {
                    var (_, th) = c.Measure(it.Text, body, w);
                    var (_, sh) = c.Measure(it.Sub, caption, w);
                    double top = r.CenterY - (th + 2 * s + sh) / 2;
                    c.Text(it.Text, new RectD(x, top, w, th), body, text, UiAlign.Left, center: false);
                    c.Text(it.Sub, new RectD(x, top + th + 2 * s, w, sh + 2 * s), caption, it.Enabled ? t.TextSecondary : t.TextTertiary, UiAlign.Left, center: false);
                }
                else c.Text(it.Text, new RectD(x, r.Top, w, r.Height), body, text);
                if (it.Kind == ItemKind.Expander)
                    c.Text(it.Selected ? FluentTheme.Icons.ChevronUp : FluentTheme.Icons.ChevronDown, new RectD(r.Right - 44 * s, r.Top, 28 * s, r.Height),
                        new UiFont(FluentTheme.IconFont, 12 * s), t.TextPrimary, UiAlign.Center);
                if (focus && it.Kind == ItemKind.Expander) FocusRing(c, t, s, r, radius);
                break;
            }
            case ItemKind.Toggle:
            {
                bool on = it.On?.Invoke() == true;
                // 「开 / 关」文字在开关左边（Windows 11 设置的样子）
                c.Text(on ? "开" : "关", new RectD(r.Left, r.Top, r.Width - 52 * s, r.Height), body, text, UiAlign.Right);
                var pill = new RectD(r.Right - 40 * s, r.CenterY - 10 * s, 40 * s, 20 * s);
                double a = Math.Clamp(anim, 0, 1);
                var fill = Mix(hover ? t.ControlHover : t.Control, it.Enabled ? t.Accent : t.TextTertiary, a);
                c.Fill(pill, 10 * s, fill);
                if (a < 0.999) c.Stroke(pill, 10 * s, 1, WithAlpha(t.StrongStroke, (float)(t.StrongStroke.A * (1 - a))));
                double knob = (hover ? 7 : 6) * s * (pressed ? 1.15 : 1);
                double kx = pill.Left + 10 * s + (pill.Width - 20 * s) * a;
                c.Circle(kx, pill.CenterY, knob, Mix(t.StrongStroke, t.OnAccent, a));
                if (focus) FocusRing(c, t, s, pill, 10 * s);
                break;
            }
            case ItemKind.Dropdown:
            case ItemKind.Corner:
            {
                if (it.Kind == ItemKind.Corner)
                {
                    // 屏幕示意上的按钮：不透明（下面是彩色的「屏幕」）
                    var solid = t.Dark ? new D2D.ColorF(0.17f, 0.17f, 0.17f, 1) : new D2D.ColorF(1, 1, 1, 1);
                    c.Fill(r, radius, pressed ? Mix(solid, t.TextPrimary, 0.06) : hover ? Mix(solid, t.TextPrimary, 0.03) : solid);
                }
                else c.Fill(r, radius, pressed ? t.ControlPressed : hover ? t.ControlHover : t.Control);
                c.Stroke(r, radius, 1, t.ControlStroke);
                int i = it.Index?.Invoke() ?? -1;
                string label = i >= 0 && i < it.Options.Count ? it.Options[i] : it.Text;
                var font = it.Kind == ItemKind.Corner ? new UiFont(FluentTheme.Font, 12 * s) : body;
                c.Text(label, new RectD(r.Left + 11 * s, r.Top, r.Width - 40 * s, r.Height), font, text);
                c.Text(FluentTheme.Icons.ChevronDown, new RectD(r.Right - 30 * s, r.Top, 20 * s, r.Height), new UiFont(FluentTheme.IconFont, 10 * s),
                    t.TextSecondary, UiAlign.Center);
                if (focus) FocusRing(c, t, s, r, radius);
                break;
            }
            case ItemKind.Slider:
            {
                double v = it.Value?.Invoke() ?? 0;
                string value = it.Format?.Invoke(v) ?? v.ToString("0.##");
                var labelBox = new RectD(r.Right - 64 * s, r.Top, 64 * s, r.Height);
                c.Text(value, labelBox, body, t.TextSecondary, UiAlign.Right);
                var track = SliderTrack(r, s);
                double f = it.Max > it.Min ? Math.Clamp((v - it.Min) / (it.Max - it.Min), 0, 1) : 0;
                c.Fill(new RectD(track.Left, track.CenterY - 2 * s, track.Width, 4 * s), 2 * s, t.Rail);
                c.Fill(new RectD(track.Left, track.CenterY - 2 * s, track.Width * f, 4 * s), 2 * s, it.Enabled ? t.Accent : t.TextTertiary);
                double tx = track.Left + track.Width * f;
                c.Circle(tx, track.CenterY, 10 * s, t.Dark ? new D2D.ColorF(0.27f, 0.27f, 0.27f, 1) : new D2D.ColorF(1, 1, 1, 1));
                c.Stroke(new RectD(tx - 10 * s, track.CenterY - 10 * s, 20 * s, 20 * s), 10 * s, 1, t.ControlStroke);
                c.Circle(tx, track.CenterY, (pressed ? 4.5 : hover ? 6.5 : 5) * s, it.Enabled ? t.Accent : t.TextTertiary);
                if (focus) FocusRing(c, t, s, new RectD(tx - 12 * s, track.CenterY - 12 * s, 24 * s, 24 * s), 12 * s);
                break;
            }
            case ItemKind.Segmented:
            {
                c.Fill(r, radius, t.Control);
                c.Stroke(r, radius, 1, t.ControlStroke);
                int sel = it.Index?.Invoke() ?? -1;
                for (int i = 0; i < it.Options.Count; i++)
                {
                    var seg = it.Segment(r, i).Inflate(-3 * s, -3 * s);
                    if (i == sel) c.Fill(seg, 3 * s, t.Accent);
                    c.Text(it.Options[i], seg, body, i == sel ? t.OnAccent : text, UiAlign.Center);
                }
                if (focus) FocusRing(c, t, s, r, radius);
                break;
            }
            case ItemKind.Button:
            case ItemKind.AccentButton:
            {
                bool accent = it.Kind == ItemKind.AccentButton;
                var fill = accent ? WithAlpha(t.Accent, pressed ? 0.8f : hover ? 0.9f : 1) : pressed ? t.ControlPressed : hover ? t.ControlHover : t.Control;
                c.Fill(r, radius, fill);
                if (!accent) c.Stroke(r, radius, 1, t.ControlStroke);
                var color = accent ? t.OnAccent : text;
                if (it.Icon.Length > 0)
                {
                    var (tw, _) = c.Measure(it.Text, body, r.Width);
                    double total = 16 * s + 8 * s + tw, x0 = r.CenterX - total / 2;
                    c.Text(it.Icon, new RectD(x0, r.Top, 16 * s, r.Height), new UiFont(FluentTheme.IconFont, 14 * s), color, UiAlign.Center);
                    c.Text(it.Text, new RectD(x0 + 24 * s, r.Top, tw + 4 * s, r.Height), body, color);
                }
                else c.Text(it.Text, r, body, color, UiAlign.Center);
                if (focus) FocusRing(c, t, s, r, radius);
                break;
            }
            case ItemKind.Gesture:
                PaintGesture(c, t, s, it, r);
                break;
            case ItemKind.EditBox:
                c.Fill(r, radius, t.Dark ? new D2D.ColorF(45 / 255f, 45 / 255f, 45 / 255f, 1) : new D2D.ColorF(1, 1, 1, 1));
                c.Stroke(r, radius, 1, t.ControlStroke);
                c.Fill(new RectD(r.Left + radius, r.Bottom - 1 * s, r.Width - 2 * radius, 1 * s), 0, t.StrongStroke);
                break;
            case ItemKind.Screen:
            {
                // 显示器示意：细边框 + 浅色「桌面」+ 任务栏 + 底座
                var screen = ScreenArea(r, s);
                var bezel = t.Dark ? new D2D.ColorF(0.24f, 0.24f, 0.25f, 1) : new D2D.ColorF(0.82f, 0.82f, 0.84f, 1);
                c.Fill(screen.Inflate(5 * s, 5 * s), 9 * s, bezel);
                var desk = t.Dark ? Mix(new D2D.ColorF(0.11f, 0.11f, 0.12f, 1), t.Accent, 0.35) : Mix(new D2D.ColorF(1, 1, 1, 1), t.Accent, 0.28);
                c.Fill(screen, 4 * s, desk);
                c.Fill(new RectD(screen.Left, screen.Bottom - 14 * s, screen.Width, 14 * s), 0, t.Dark ? new D2D.ColorF(0, 0, 0, 0.3f) : new D2D.ColorF(1, 1, 1, 0.6f));
                c.Fill(new RectD(screen.CenterX - 30 * s, screen.Bottom + 5 * s, 60 * s, 12 * s), 0, bezel);
                c.Fill(new RectD(screen.CenterX - 70 * s, screen.Bottom + 17 * s, 140 * s, 5 * s), 2.5 * s, bezel);
                break;
            }
        }
    }

    /// <summary>屏幕示意里「屏幕」的范围（触发角按钮放在它的四个角上）。</summary>
    public static RectD ScreenArea(RectD r, double s) => new(r.Left + 8 * s, r.Top + 8 * s, r.Width - 16 * s, r.Height - 44 * s);

    /// <summary>滑块的轨道（左边留给轨道，右边 64 DIP 是数值）。</summary>
    /// <param name="r">滑块在屏幕上的矩形（已减去滚动偏移）。</param>
    public static RectD SliderTrack(RectD r, double s) => new(r.Left + 10 * s, r.Top, Math.Max(10 * s, r.Width - 84 * s), r.Height);

    /// <summary>触摸板示意：圆角矩形里 <see cref="Item.Fingers"/> 个手指圆点，两侧箭头表示滑动方向。</summary>
    private static void PaintGesture(UiCanvas c, FluentTheme t, double s, Item it, RectD r)
    {
        c.Fill(r, 6 * s, t.Dark ? new D2D.ColorF(1, 1, 1, 0.06f) : new D2D.ColorF(0, 0, 0, 0.04f));
        c.Stroke(r, 6 * s, 1, t.ControlStroke);
        int n = Math.Max(1, it.Fingers);
        double gap = 9 * s, dot = 3.6 * s;
        double x0 = r.CenterX - gap * (n - 1) / 2;
        for (int i = 0; i < n; i++) c.Circle(x0 + gap * i, r.CenterY + (i == 0 || i == n - 1 ? 2 * s : 0), dot, t.Accent);
        var arrow = t.TextSecondary;
        double w = 1.6 * s;
        if (it.Horizontal)
        {
            double y = r.CenterY + 12 * s, a = 6 * s;
            c.Line(r.CenterX - 16 * s, y, r.CenterX + 16 * s, y, w, arrow);
            c.Line(r.CenterX - 16 * s, y, r.CenterX - 16 * s + a * 0.7, y - a * 0.6, w, arrow);
            c.Line(r.CenterX - 16 * s, y, r.CenterX - 16 * s + a * 0.7, y + a * 0.6, w, arrow);
            c.Line(r.CenterX + 16 * s, y, r.CenterX + 16 * s - a * 0.7, y - a * 0.6, w, arrow);
            c.Line(r.CenterX + 16 * s, y, r.CenterX + 16 * s - a * 0.7, y + a * 0.6, w, arrow);
        }
        else
        {
            double x = r.Right - 12 * s, a = 6 * s;
            c.Line(x, r.CenterY - 11 * s, x, r.CenterY + 11 * s, w, arrow);
            c.Line(x, r.CenterY - 11 * s, x - a * 0.6, r.CenterY - 11 * s + a * 0.7, w, arrow);
            c.Line(x, r.CenterY - 11 * s, x + a * 0.6, r.CenterY - 11 * s + a * 0.7, w, arrow);
            c.Line(x, r.CenterY + 11 * s, x - a * 0.6, r.CenterY + 11 * s - a * 0.7, w, arrow);
            c.Line(x, r.CenterY + 11 * s, x + a * 0.6, r.CenterY + 11 * s - a * 0.7, w, arrow);
        }
    }

    /// <summary>键盘焦点框（Windows 11：外圈 2px 主文字色）。</summary>
    private static void FocusRing(UiCanvas c, FluentTheme t, double s, RectD r, double radius)
        => c.Stroke(r.Inflate(3 * s, 3 * s), radius + 3 * s, 2 * s, t.TextPrimary);

    public static D2D.ColorF WithAlpha(D2D.ColorF c, float a) => new(c.R, c.G, c.B, a);

    public static D2D.ColorF Mix(D2D.ColorF a, D2D.ColorF b, double t)
    {
        float k = (float)Math.Clamp(t, 0, 1);
        return new D2D.ColorF(a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k, a.A + (b.A - a.A) * k);
    }
}
