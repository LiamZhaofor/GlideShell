using System.Text;

namespace GlideShell.Core.Search;

/// <summary>
/// 汉字的拼音（不带声调，ü 写作 v）。数据由 <c>tools/pinyin/gen_pinyin.py</c> 从 Unicode Unihan 生成（kMandarin + 《现代汉语词典》
/// + 《通用规范汉字字典》的读音，最常用的排第一），编进程序（Pinyin.bin，约 66 KB），只收 U+4E00–U+9FFF。
/// 第一次用时解析，之后各线程可读。
/// </summary>
public static class Pinyin
{
    private sealed class Table(string[] syllables, int first, ushort[][] readings)
    {
        public string[] Syllables { get; } = syllables;
        public int First { get; } = first;
        public ushort[][] Readings { get; } = readings;
        public string[]?[] Cache { get; } = new string[readings.Length][];
    }

    private static readonly Lazy<Table> s_table = new(Load);

    /// <summary><paramref name="c"/> 的全部读音（最常用的在前）；不是汉字或没有读音时为空。</summary>
    public static IReadOnlyList<string> Readings(char c)
    {
        var t = s_table.Value;
        int i = c - t.First;
        if (i < 0 || i >= t.Readings.Length || t.Readings[i].Length == 0) return [];
        return t.Cache[i] ??= Array.ConvertAll(t.Readings[i], k => t.Syllables[k]);
    }

    /// <summary>是不是收录了读音的汉字。</summary>
    public static bool IsHan(char c) => Readings(c).Count > 0;

    private static Table Load()
    {
        using var stream = typeof(Pinyin).Assembly.GetManifestResourceStream("GlideShell.Core.Search.Pinyin.bin")
                           ?? throw new InvalidOperationException("缺少拼音表资源");
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (reader.ReadUInt32() != 0x00315950) throw new InvalidDataException("拼音表格式不对"); // "PY1\0"
        var syllables = new string[reader.ReadUInt16()];
        var sb = new StringBuilder();
        for (int i = 0; i < syllables.Length; i++)
        {
            sb.Clear();
            for (byte b = reader.ReadByte(); b != 0; b = reader.ReadByte()) sb.Append((char)b);
            syllables[i] = sb.ToString();
        }
        int first = (int)reader.ReadUInt32();
        var readings = new ushort[reader.ReadUInt32()][];
        for (int i = 0; i < readings.Length; i++)
        {
            int n = reader.ReadByte();
            var ids = n == 0 ? [] : new ushort[n];
            for (int k = 0; k < n; k++) ids[k] = reader.ReadUInt16();
            readings[i] = ids;
        }
        return new Table(syllables, first, readings);
    }
}
