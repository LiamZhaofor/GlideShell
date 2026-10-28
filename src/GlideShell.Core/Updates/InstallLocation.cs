namespace GlideShell.Core.Updates;

public enum InstallKind
{
    /// <summary>发布包「安装.cmd」装的位置（%LOCALAPPDATA%\Programs\GlideShell）：一键升级，开始菜单与开机启动照旧。</summary>
    Standard,

    /// <summary>解压到别处直接运行：一键升级装回原处，不碰开始菜单（开机启动看原来指不指向这里）。</summary>
    Elsewhere,

    /// <summary>winget 装的：交给 <c>winget upgrade</c>，不自己覆盖（winget 记着装了哪个版本）。</summary>
    WinGet,

    /// <summary>Program Files 下（从源码构建的 UIAccess 版）：发布包是普通权限版，覆盖过去会丢掉 UIAccess，不一键升级。</summary>
    ProgramFiles,
}

/// <summary>按程序所在目录判断是怎么装的，决定能不能一键升级（纯函数，路径不区分大小写）。</summary>
public static class InstallLocation
{
    public static InstallKind Classify(string exeDirectory, string localAppData, string programFiles)
    {
        string dir = Normalize(exeDirectory);
        if (dir == Normalize(Path.Combine(localAppData, "Programs", "GlideShell"))) return InstallKind.Standard;
        if (dir.StartsWith(Normalize(Path.Combine(localAppData, "Microsoft", "WinGet")) + "\\", StringComparison.Ordinal)) return InstallKind.WinGet;
        if (programFiles.Length > 0 && dir.StartsWith(Normalize(programFiles) + "\\", StringComparison.Ordinal)) return InstallKind.ProgramFiles;
        return InstallKind.Elsewhere;
    }

    /// <summary>能不能一键升级。</summary>
    public static bool CanUpgradeInPlace(InstallKind kind) => kind is InstallKind.Standard or InstallKind.Elsewhere;

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
