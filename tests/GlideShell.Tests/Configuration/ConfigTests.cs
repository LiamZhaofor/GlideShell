using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;

namespace GlideShell.Tests.Configuration;

public class ConfigTests
{
    [Fact]
    public void Empty_json_gives_defaults()
    {
        var (config, problems) = ConfigStore.Parse("{}");

        Assert.Empty(problems);
        Assert.Equal(30, config.Overview.TravelMm);
        Assert.Equal(3, config.Recognition.MinFingers);
        Assert.Same(GestureMap.Default, config.EffectiveMap);
    }

    [Fact]
    public void Round_trips_through_json_with_comments_and_string_enums()
    {
        var original = new GlideShellConfig
        {
            Overview = new OverviewConfig { TravelMm = 26 },
            Bindings = [new(4, GestureAxis.Vertical, GestureAction.Overview)],
            ExcludedProcesses = ["Photoshop"],
        };
        string json = "// 用户注释\n" + ConfigStore.Serialize(original);

        var (loaded, problems) = ConfigStore.Parse(json);

        Assert.Empty(problems);
        Assert.Contains("\"Vertical\"", json);
        Assert.Equal(26, loaded.Overview.TravelMm);
        Assert.Equal(GestureAction.Overview, loaded.EffectiveMap.Resolve(4, GestureAxis.Vertical));
        Assert.True(loaded.IsExcluded("photoshop"));
    }

    [Fact]
    public void Hand_written_example_from_the_docs_parses()
    {
        // docs/CONFIG.md 里的例子：注释、末尾逗号、未知字段（旧版本写进去的 effectiveMap）都不应出错
        const string json = """
            {
              // 三指留给系统、全部功能改用四指
              "bindings": [
                { "fingers": 4, "axis": "Vertical",   "action": "Overview" },
                { "fingers": 4, "axis": "Horizontal", "action": "SwitchWindows" },
              ],
              "excludedProcesses": ["mstsc", "vmware.exe"],
              "desktops": { "enabled": false },
              "effectiveMap": { "bindings": [] },
            }
            """;

        var (config, problems) = ConfigStore.Parse(json);

        Assert.Empty(problems);
        Assert.Equal(GestureAction.Overview, config.EffectiveMap.Resolve(4, GestureAxis.Vertical));
        Assert.Equal(GestureAction.None, config.EffectiveMap.Resolve(3, GestureAxis.Vertical));
        Assert.True(config.IsExcluded("VMware"));
        Assert.False(config.Desktops.Enabled);
        Assert.Equal(55, config.Desktops.TravelMm); // 同一节里缺失的字段仍取默认值
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_reported()
    {
        var (config, problems) = ConfigStore.Parse("""{ "recognition": { "slopMm": 0.01, "tapMaxMoveMm": 50 }, "overview": { "travelMm": 1000 } }""");

        Assert.Equal(0.5, config.Recognition.SlopMm);
        Assert.Equal(0.5, config.Recognition.TapMaxMoveMm); // 轻点位移不能大于滑动阈值
        Assert.Equal(100, config.Overview.TravelMm);
        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void Conflicting_bindings_fall_back_to_default_map()
    {
        var (config, problems) = ConfigStore.Parse("""
            { "bindings": [ { "fingers": 3, "axis": "Vertical", "action": "Overview" },
                            { "fingers": 3, "axis": "Vertical", "action": "SwitchDesktops" } ] }
            """);

        Assert.NotEmpty(problems);
        Assert.Same(GestureMap.Default, config.EffectiveMap);
    }

    [Fact]
    public void Three_finger_drag_switches_default_map()
    {
        var (config, _) = ConfigStore.Parse("""{ "threeFingerDrag": true }""");
        Assert.Same(GestureMap.WithThreeFingerDrag, config.EffectiveMap);
    }

    [Fact]
    public void Broken_json_never_throws()
    {
        var (config, problems) = ConfigStore.Parse("{ not json");

        Assert.Single(problems);
        Assert.Equal(new GlideShellConfig().Overview, config.Overview);
    }

    [Fact]
    public void Excluded_process_names_are_normalized()
    {
        var (config, _) = ConfigStore.Parse("""{ "excludedProcesses": [" Game.exe ", "game", ""] }""");

        Assert.Equal(new[] { "Game" }, config.ExcludedProcesses);
    }

    [Fact]
    public void Save_then_load_from_disk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glideshell-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "config.json");
        try
        {
            ConfigStore.Save(new GlideShellConfig { PauseInFullscreen = false }, path);
            var (loaded, problems) = ConfigStore.Load(path);

            Assert.Empty(problems);
            Assert.False(loaded.PauseInFullscreen);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
