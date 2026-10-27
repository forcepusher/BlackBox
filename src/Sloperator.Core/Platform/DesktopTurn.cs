using System.Runtime.CompilerServices;

namespace Sloperator.Core.Platform;

/// <summary>
/// The mouse and keyboard, shared by the windows of a Sloperator, each of which may have an agent at
/// work: one agent uses them at a time, from its first click or key press until its task ends, or
/// until it has left them alone for <see cref="IdleHandover"/> while another waits for them. Two
/// at once, each one's clicks moved the focus from under the other's typing. An agent is known by
/// its run's <see cref="Tools.ToolContext"/>.
/// </summary>
public sealed class DesktopTurn(Func<long>? clock = null)
{
    /// <summary>
    /// How long an agent leaves the mouse and keyboard alone before another that waits for them
    /// gets them: longer than a model thinks between two clicks, so the other's clicks don't come
    /// between them. Never in the middle of an action, however long it waits for the user's typing.
    /// </summary>
    internal static TimeSpan IdleHandover = TimeSpan.FromSeconds(60);

    private readonly Func<long> _now = clock ?? (() => Environment.TickCount64);
    private readonly Lock _gate = new();
    private object? _holder;
    private bool _busy;
    private long _lastUse;

    /// <summary>The actions done so far, by every agent: each may have changed the screen.</summary>
    private long _actions;

    /// <summary>
    /// For each agent, <see cref="_actions"/> as it was when the agent last saw the screen or acted;
    /// kept by its window (<see cref="Tools.ToolContext.DesktopSeat"/>), as the chat keeps its last
    /// screenshot from one run to the next, and no longer than the window.
    /// </summary>
    private readonly ConditionalWeakTable<object, StrongBox<long>> _seen = new();

    private static object Seat(object who) => who is Tools.ToolContext { DesktopSeat: { } seat } ? seat : who;

    /// <summary>
    /// Waits until <paramref name="who"/> has the mouse and keyboard, for up to <paramref name="maxWait"/>;
    /// <paramref name="waiting"/> is told for how long so far, a few times a second. Null when the
    /// wait ran out; otherwise whether the screen may no longer be what this agent last saw: another
    /// agent acted since, or had the mouse and keyboard while this one waited. An action starts: see
    /// <see cref="Done"/>.
    /// </summary>
    public async Task<bool?> TakeAsync(object who, TimeSpan maxWait, Action<TimeSpan>? waiting, CancellationToken ct)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var hadToWait = false;
        while (true)
        {
            lock (_gate)
            {
                if (_holder is null || ReferenceEquals(_holder, who) || !_busy && _now() - _lastUse >= IdleHandover.TotalMilliseconds)
                {
                    (_holder, _busy, _lastUse) = (who, true, _now());
                    // One that never looked, after another acted: its chat's last screenshot may be of a
                    // screen another agent has changed since ("press Enter", typed into that one's program).
                    return hadToWait || (_seen.TryGetValue(Seat(who), out var seen) ? seen.Value < _actions : _actions > 0);
                }
            }
            hadToWait = true;
            if (waited.Elapsed >= maxWait)
                return null;
            waiting?.Invoke(waited.Elapsed);
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary><paramref name="who"/> has just seen the screen, as the actions so far left it.</summary>
    public void Looked(object who)
    {
        lock (_gate)
            _seen.AddOrUpdate(Seat(who), new StrongBox<long>(_actions));
    }

    /// <summary>
    /// The action <paramref name="who"/> took the turn for is over, <paramref name="acted"/> when it
    /// clicked or pressed anything: the time the mouse and keyboard are left alone counts from here.
    /// </summary>
    public void Done(object who, bool acted)
    {
        lock (_gate)
        {
            if (acted)
                // Its own action it knows of.
                _seen.AddOrUpdate(Seat(who), new StrongBox<long>(++_actions));
            if (ReferenceEquals(_holder, who))
                (_busy, _lastUse) = (false, _now());
        }
    }

    /// <summary>The task of <paramref name="who"/> is over: another agent may have them at once.</summary>
    public void Release(object who)
    {
        lock (_gate)
            if (ReferenceEquals(_holder, who))
                (_holder, _busy) = (null, false);
    }
}
