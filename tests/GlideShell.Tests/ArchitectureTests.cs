using System.Reflection;
using System.Runtime.Versioning;

namespace GlideShell.Tests;

// 架构守卫：Core 必须保持平台无关，才能脱离 Windows 用录制轨迹做确定性回放测试。
// 依赖方向 GlideShell → Platform → Core 由项目引用保证；这里守住项目引用管不到的部分。
public class ArchitectureTests
{
    private static readonly Assembly Core = Assembly.Load("GlideShell.Core");

    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "GlideShell.Platform",
        "System.Windows",       // WinForms / WPF
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "Microsoft.Win32",      // 注册表等
    ];

    [Fact]
    public void Core_does_not_declare_platform_invoke()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // DllImport 和 LibraryImport（生成器最终也产出 DllImport 方法）都会带 PinvokeImpl 标记
        var offenders = Core.GetTypes()
            .SelectMany(t => t.GetMethods(all))
            .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Core_does_not_reference_windows_specific_assemblies()
    {
        var offenders = Core.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => ForbiddenReferencePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Core_targets_no_specific_os()
    {
        // -windows TFM 会给程序集打上 SupportedOSPlatform("windows")
        Assert.Null(Core.GetCustomAttribute<SupportedOSPlatformAttribute>());
    }
}
