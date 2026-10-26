namespace GlideShell.Core.Gestures;

/// <summary>
/// 动作的接收方（总览、切换窗口、切换桌面控制器）。全部在输入线程上同步调用，必须立刻返回：
/// 重活（枚举窗口、截图、切桌面）投递到别的线程做，这里只更新进度状态。
/// </summary>
public interface IGestureTarget
{
    /// <summary>可能触发本动作的手指已落下：可以开始预热，但不能有任何可见变化。</summary>
    void Prepare(long hostUs);

    /// <summary>预热作废（变成了轻点、两指滚动，或别的动作）。</summary>
    void Unprepare(long hostUs);

    void Begin(in GestureEvent e);

    void Update(in GestureEvent e);

    /// <summary>手势结束（<see cref="GesturePhase.Ended"/>）或异常中止（<see cref="GesturePhase.Cancelled"/>）。</summary>
    void End(in GestureEvent e);
}

/// <summary>
/// 把识别出的手势按映射表分发给动作。「是否暂停接管」（排除程序在前台、全屏、用户暂停）
/// 由调用方维护成一个廉价查询，只在手指落下和开始滑动时各问一次——不在每份报文上做检测。
/// </summary>
public sealed class GestureRouter(Func<GestureMap> map, Func<GestureAction, IGestureTarget?> targets, Func<bool>? suppressed = null)
{
    private readonly List<IGestureTarget> _prepared = [];
    private IGestureTarget? _active;

    public GestureAction ActiveAction { get; private set; }

    public void OnGesture(GestureEvent e)
    {
        switch (e.Phase)
        {
            case GesturePhase.Possible:
                UnprepareAll(e.HostTimeUs);
                if (suppressed?.Invoke() == true) return;
                foreach (var action in map().Candidates(e.Fingers))
                {
                    if (targets(action) is { } t && !_prepared.Contains(t))
                    {
                        _prepared.Add(t);
                        t.Prepare(e.HostTimeUs);
                    }
                }
                break;

            case GesturePhase.Began:
                // 同一个识别器不会出现（上一次一定先 Ended / Cancelled）；识别器被替换（配置重载）时兜底，
                // 免得上一个动作卡在跟踪中（例如三指拖移的左键一直按着）
                if (_active is not null)
                {
                    _active.End(e with { Phase = GesturePhase.Cancelled });
                    _active = null;
                    ActiveAction = GestureAction.None;
                }
                var chosen = suppressed?.Invoke() == true ? GestureAction.None : map().Resolve(e.Fingers, e.Axis);
                var target = chosen == GestureAction.None ? null : targets(chosen);
                // 没被选中的预热作废
                foreach (var t in _prepared)
                    if (!ReferenceEquals(t, target)) t.Unprepare(e.HostTimeUs);
                _prepared.Clear();
                _active = target;
                ActiveAction = target is null ? GestureAction.None : chosen;
                _active?.Begin(e);
                break;

            case GesturePhase.Changed:
                _active?.Update(e);
                break;

            case GesturePhase.Ended:
            case GesturePhase.Cancelled:
                if (e.Kind == GestureKind.Tap)
                {
                    UnprepareAll(e.HostTimeUs);
                    return;
                }
                _active?.End(e);
                _active = null;
                ActiveAction = GestureAction.None;
                UnprepareAll(e.HostTimeUs);
                break;

            case GesturePhase.Failed:
                UnprepareAll(e.HostTimeUs);
                break;
        }
    }

    private void UnprepareAll(long hostUs)
    {
        foreach (var t in _prepared) t.Unprepare(hostUs);
        _prepared.Clear();
    }
}
