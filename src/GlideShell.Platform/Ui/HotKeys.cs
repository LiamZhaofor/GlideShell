using GlideShell.Platform.Native;

namespace GlideShell.Platform.Ui;

/// <summary>全局热键（RegisterHotKey），回调在 UI 线程上执行。</summary>
public sealed class HotKeys : IDisposable
{
    private readonly UiThread _ui;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public HotKeys(UiThread ui)
    {
        _ui = ui;
        ui.OnMessage(User32.WM_HOTKEY, (wParam, _) =>
        {
            if (_actions.TryGetValue((int)wParam, out var action)) action();
            return 0;
        });
    }

    /// <summary>注册热键；被其他程序占用时返回 false。</summary>
    /// <param name="modifiers">User32 MOD_* 组合。</param>
    /// <param name="virtualKey">虚拟键码，例如 'O' = 0x4F。</param>
    public bool Register(uint modifiers, uint virtualKey, Action action)
    {
        int id = _nextId++;
        if (!User32.RegisterHotKey(_ui.Handle, id, modifiers | User32.MOD_NOREPEAT, virtualKey)) return false;
        _actions[id] = action;
        return true;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys) User32.UnregisterHotKey(_ui.Handle, id);
        _actions.Clear();
    }
}
