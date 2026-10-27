using GlideShell.Core.Geometry;

namespace GlideShell.Platform.Ui.Settings;

/// <summary>
/// 一页内容的竖排布局：标题、分组小标题、说明文字、卡片（右侧挂控件）一个接一个往下排，记下每个元素的矩形（物理像素，不含滚动）。
/// 尺寸按 Windows 11「设置」：卡片间距 4、分组前空 20、控件高 32（单位 DIP，乘 <see cref="S"/>）。
/// </summary>
internal sealed class PageBuilder(UiCanvas canvas, double s, double left, double width, double top, List<Item> items)
{
    public double Y { get; set; } = top;
    public double Left => left;
    public double Width => width;
    public double S => s;
    public List<Item> Items => items;

    public void Title(string text)
    {
        items.Add(new Item(ItemKind.Title, "title") { Rect = new RectD(left, Y, width, 40 * s), Text = text });
        Y += 52 * s;
    }

    public void Paragraph(string text, string key = "")
    {
        var font = new UiFont(Settings.FluentTheme.Font, 13 * s, 400, Wrap: true);
        var (_, h) = canvas.Measure(text, font, width);
        items.Add(new Item(ItemKind.Paragraph, "p" + key + Y.ToString("0")) { Rect = new RectD(left, Y, width, h + 4 * s), Text = text });
        Y += h + 14 * s;
    }

    public void Section(string text)
    {
        Y += 14 * s;
        items.Add(new Item(ItemKind.Section, "s" + text) { Rect = new RectD(left, Y, width, 22 * s), Text = text });
        Y += 30 * s;
    }

    public void InfoBar(string text)
    {
        var font = new UiFont(Settings.FluentTheme.Font, 13 * s, 400, Wrap: true);
        var (_, h) = canvas.Measure(text, font, width - 62 * s);
        double height = Math.Max(48 * s, h + 28 * s);
        items.Add(new Item(ItemKind.InfoBar, "info" + Y.ToString("0")) { Rect = new RectD(left, Y, width, height), Text = text });
        Y += height + 12 * s;
    }

    /// <summary>
    /// 一张卡片：图标 + 标题 + 说明，右边挂 <paramref name="control"/>（宽 <paramref name="controlWidthDip"/>，高 32）。
    /// <paramref name="indentDip"/> 给左边的示意图让位；<paramref name="expander"/> 时整张卡可以点（展开 / 收起）。返回卡片。
    /// </summary>
    public Item Card(string key, string icon, string title, string sub, Item? control = null, double controlWidthDip = 0, double indentDip = 0,
        bool expander = false, bool expanded = false, bool enabled = true, double x = double.NaN, double w = double.NaN, bool advance = true)
    {
        double cx = double.IsNaN(x) ? left : x, cw = double.IsNaN(w) ? width : w;
        double controlW = control is null ? 0 : controlWidthDip * s;
        double textW = cw - 32 * s - (icon.Length > 0 ? 40 * s : 0) - indentDip * s - (controlW > 0 ? controlW + 24 * s : 0) - (expander ? 40 * s : 0);
        var body = new UiFont(Settings.FluentTheme.Font, 14 * s);
        var caption = new UiFont(Settings.FluentTheme.Font, 12 * s, 400, Wrap: true);
        var (_, th) = canvas.Measure(title, body, Math.Max(40 * s, textW));
        double sh = sub.Length > 0 ? canvas.Measure(sub, caption, Math.Max(40 * s, textW)).Height : 0;
        double h = Math.Max(sub.Length > 0 ? 68 * s : 56 * s, 26 * s + th + (sub.Length > 0 ? 2 * s + sh : 0));
        var card = new Item(expander ? ItemKind.Expander : ItemKind.Card, key)
        {
            Rect = new RectD(cx, Y, cw, h), Icon = icon, Text = title, Sub = sub, Selected = expanded, Indent = indentDip * s, Enabled = enabled,
        };
        items.Add(card);
        if (control is not null)
        {
            double right = cx + cw - 16 * s - (expander ? 40 * s : 0);
            control.Rect = new RectD(right - controlW, Y + (h - 32 * s) / 2, controlW, 32 * s);
            card.Control = control;
            items.Add(control);
        }
        if (advance) Y += h + 4 * s;
        return card;
    }

    public void Gap(double dip) => Y += dip * s;

    /// <summary>按钮的宽度（文字宽 + 内边距，至少 96 DIP），单位 DIP。</summary>
    public double ButtonWidthDip(string text, bool icon = false)
    {
        var (w, _) = canvas.Measure(text, new UiFont(Settings.FluentTheme.Font, 14 * s), 1000 * s);
        return Math.Max(96, w / s + 32 + (icon ? 24 : 0));
    }
}
