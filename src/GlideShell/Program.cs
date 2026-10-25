namespace GlideShell;

// 常驻宿主入口。P0 只占位：输入线程、渲染线程、托盘在 P2/P3 接入（见 docs/ROADMAP.md 第 4 节）。
internal static class Program
{
    [STAThread]
    private static int Main() => 0;
}
