using GlideShell.Core.Configuration;
using GlideShell.Core.Gestures;

namespace GlideShell.Tests.Configuration;

public class SettingsModelTests
{
    private static GestureSlot Slot(int fingers, GestureAxis axis) => SettingsModel.Slots.Single(s => s.Fingers == fingers && s.Axis == axis);

    [Fact]
    public void Corner_radius_and_mouse_gestures_round_trip_and_are_clamped()
    {
        var config = new GlideShellConfig();
        var model = SettingsModel.FromConfig(config);
        Assert.Equal(12, model.CornerRadius);
        Assert.Equal(MouseDragButton.Back, model.MouseButton);

        var (saved, problems) = (model with { CornerRadius = 6, MouseButton = MouseDragButton.Middle, MouseSensitivity = 1.5 }).ApplyTo(config);
        Assert.Empty(problems);
        Assert.Equal(6, saved.Overview.CornerRadius);
        Assert.Equal(MouseDragButton.Middle, saved.Mouse.Button);
        Assert.Equal(1.5, saved.Mouse.Sensitivity);

        var json = ConfigStore.Serialize(saved);
        Assert.Contains("\"Middle\"", json); // 枚举按名字存，配置文件里看得懂

        var (clamped, clampProblems) = (config with { Overview = config.Overview with { CornerRadius = 99 }, Mouse = new MouseGesturesConfig { Sensitivity = 9 } }).Sanitize();
        Assert.Equal(32, clamped.Overview.CornerRadius);
        Assert.Equal(4, clamped.Mouse.Sensitivity);
        Assert.Equal(2, clampProblems.Count);
    }

    [Fact]
    public void Alt_tab_takeover_is_on_by_default_and_round_trips()
    {
        var config = new GlideShellConfig();
        var model = SettingsModel.FromConfig(config);
        Assert.True(model.TakeOverAltTab);

        var (saved, problems) = (model with { TakeOverAltTab = false }).ApplyTo(config);
        Assert.Empty(problems);
        Assert.False(saved.Switcher.TakeOverAltTab);
        Assert.False(ConfigStore.Parse(ConfigStore.Serialize(saved)).Config.Switcher.TakeOverAltTab);
    }

    [Fact]
    public void Three_finger_tap_middle_click_is_on_by_default_and_round_trips()
    {
        var config = new GlideShellConfig();
        Assert.True(SettingsModel.FromConfig(config).ThreeFingerTapMiddleClick);
        var (saved, _) = (SettingsModel.FromConfig(config) with { ThreeFingerTapMiddleClick = false }).ApplyTo(config);
        Assert.False(ConfigStore.Parse(ConfigStore.Serialize(saved)).Config.ThreeFingerTapMiddleClick);
    }

    [Fact]
    public void Update_check_is_on_by_default_and_round_trips()
    {
        var config = new GlideShellConfig();
        Assert.True(SettingsModel.FromConfig(config).CheckUpdates);
        var (saved, _) = (SettingsModel.FromConfig(config) with { CheckUpdates = false }).ApplyTo(config);
        Assert.False(ConfigStore.Parse(ConfigStore.Serialize(saved)).Config.Updates.Check);
    }

    [Fact]
    public void Default_config_round_trips_without_writing_a_custom_map()
    {
        var config = new GlideShellConfig();
        var model = SettingsModel.FromConfig(config);

        Assert.Equal(new[] { GestureAction.Overview, GestureAction.SwitchWindows, GestureAction.ShowDesktop, GestureAction.SwitchDesktops }, model.Actions);
        Assert.Same(SensitivityPreset.All[1], model.Preset);

        var (saved, problems) = model.ApplyTo(config);
        Assert.Empty(problems);
        Assert.Null(saved.Bindings); // 与默认映射一致：不写自定义映射
        Assert.Equal(ConfigStore.Serialize(config), ConfigStore.Serialize(saved));
    }

    [Fact]
    public void Changing_a_slot_writes_a_custom_map_and_keeps_fields_the_window_does_not_edit()
    {
        var config = new GlideShellConfig
        {
            Overview = new OverviewConfig { Spring = new SpringConfig { Response = 0.5 } },
            Bindings = [new(3, GestureAxis.Vertical, GestureAction.Overview), new(5, GestureAxis.Vertical, GestureAction.Overview)],
        };
        var model = SettingsModel.FromConfig(config).WithAction(Slot(4, GestureAxis.Horizontal), GestureAction.SwitchWindows);

        var (saved, problems) = model.ApplyTo(config);

        Assert.Empty(problems);
        Assert.Equal(GestureAction.SwitchWindows, saved.EffectiveMap.Resolve(4, GestureAxis.Horizontal));
        Assert.Equal(GestureAction.Overview, saved.EffectiveMap.Resolve(5, GestureAxis.Vertical)); // 5 指绑定保留
        Assert.Equal(0.5, saved.Overview.Spring.Response); // 弹簧等界面以外的字段保留
    }

    [Fact]
    public void Disabled_desktop_switching_shows_as_system_default_and_saves_consistently()
    {
        var config = new GlideShellConfig { Desktops = new DesktopConfig { Enabled = false } };
        var model = SettingsModel.FromConfig(config);
        Assert.Equal(GestureAction.None, model.ActionAt(Slot(4, GestureAxis.Horizontal)));

        var (saved, _) = model.ApplyTo(config);
        Assert.True(saved.Desktops.Enabled);
        Assert.Equal(GestureAction.None, saved.EffectiveMap.Resolve(4, GestureAxis.Horizontal)); // 仍归系统

        var (reenabled, _) = SettingsModel.FromConfig(saved).WithAction(Slot(4, GestureAxis.Horizontal), GestureAction.SwitchDesktops).ApplyTo(saved);
        Assert.Null(reenabled.Bindings); // 回到默认映射
    }

    [Fact]
    public void Three_finger_drag_moves_overview_to_four_fingers_and_back()
    {
        var model = SettingsModel.FromConfig(new GlideShellConfig()).WithThreeFingerDrag(true);

        Assert.Equal(new[] { GestureAction.Drag, GestureAction.Drag, GestureAction.Overview, GestureAction.SwitchDesktops }, model.Actions);
        Assert.Empty(model.Validate());
        var (saved, problems) = model.ApplyTo(new GlideShellConfig());
        Assert.Empty(problems);
        Assert.Null(saved.Bindings); // 正好是三指拖移的默认映射
        Assert.True(saved.ThreeFingerDrag);

        var off = model.WithThreeFingerDrag(false);
        Assert.Equal(GestureAction.Overview, off.ActionAt(Slot(3, GestureAxis.Vertical)));
        Assert.Equal(GestureAction.SwitchWindows, off.ActionAt(Slot(3, GestureAxis.Horizontal)));
        Assert.Equal(new[] { GestureAction.Drag }, SettingsModel.Choices(Slot(3, GestureAxis.Vertical), threeFingerDrag: true));
    }

    [Fact]
    public void Vertical_slots_never_offer_horizontal_only_actions()
    {
        foreach (var slot in SettingsModel.Slots.Where(s => s.Axis == GestureAxis.Vertical))
        {
            var choices = SettingsModel.Choices(slot, threeFingerDrag: false);
            Assert.DoesNotContain(GestureAction.SwitchWindows, choices);
            Assert.DoesNotContain(GestureAction.SwitchDesktops, choices);
        }
    }

    [Fact]
    public void Presets_and_defaults()
    {
        var config = SensitivityPreset.All[0].ApplyTo(new GlideShellConfig { ExcludedProcesses = ["mstsc"] });
        Assert.Same(SensitivityPreset.All[0], SensitivityPreset.Of(config));
        Assert.Null(SensitivityPreset.Of(config with { Overview = config.Overview with { TravelMm = 31 } }));

        var model = SettingsModel.FromConfig(config, autostart: true) with { BackdropDim = 0.2 };
        var defaults = model.Defaults();
        Assert.Equal(0.6, defaults.BackdropDim);
        Assert.Same(SensitivityPreset.All[1], defaults.Preset);
        Assert.Equal(new[] { "mstsc" }, defaults.ExcludedProcesses); // 用户数据保留
        Assert.True(defaults.Autostart);
    }
}
