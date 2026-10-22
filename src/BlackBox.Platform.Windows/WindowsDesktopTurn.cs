using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;

namespace BlackBox.Platform.Windows;

/// <summary>
/// Whose turn it is with the mouse and keyboard among the BlackBoxes on the PC: a named mutex,
/// which Windows lets go of when the process holding it ends, however it ends. A mutex belongs to
/// the thread that took it, and a tool's wait moves between threads, so one thread of its own
/// takes and lets go of it. A window that wants the turn says so through a named event; the one
/// that has it lets go once it has left the mouse and keyboard alone for <see cref="IdleHandover"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDesktopTurn : IDesktopTurn
{
    /// <summary>
    /// How long an agent leaves the mouse and keyboard alone before another window's agent that
    /// waits for them gets them: longer than a model takes to think between two clicks, so the
    /// other agent's clicks don't come between them.
    /// </summary>
    internal static TimeSpan IdleHandover = TimeSpan.FromSeconds(60);

    private readonly BlockingCollection<Action> _work = [];
    private readonly Mutex _turn = new(false, @"Local\BlackBox.DesktopTurn");
    private readonly EventWaitHandle _wanted = new(false, EventResetMode.ManualReset, @"Local\BlackBox.DesktopTurnWanted");

    /// <summary>One take at a time in this process; there is one agent per window, but a reply can hold several actions.</summary>
    private readonly SemaphoreSlim _one = new(1, 1);

    // Used on the turn's thread only.
    private bool _held;
    private bool _handedOver;
    private long _lastUse;

    public WindowsDesktopTurn()
    {
        new Thread(Run) { IsBackground = true, Name = "BlackBox desktop turn" }.Start();
    }

    private void Run()
    {
        while (true)
        {
            if (_work.TryTake(out var job, TimeSpan.FromSeconds(1)))
                job();
            // Left alone long enough, and another window waits: its turn.
            else if (_held && Environment.TickCount64 - _lastUse >= IdleHandover.TotalMilliseconds && _wanted.WaitOne(0))
            {
                _turn.ReleaseMutex();
                _held = false;
                _handedOver = true;
            }
        }
    }

    private Task<T> OnThread<T>(Func<T> job)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { done.SetResult(job()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public async Task<bool?> TakeAsync(TimeSpan maxWait, Action<TimeSpan>? waiting, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var waited = Stopwatch.StartNew();
            var hadToWait = false;
            while (true)
            {
                if (await OnThread(TryTake).ConfigureAwait(false))
                {
                    // Taken after another window had it: the screen is no longer what this agent saw.
                    return await OnThread(() =>
                    {
                        var changed = hadToWait || _handedOver;
                        _handedOver = false;
                        return changed;
                    }).ConfigureAwait(false);
                }
                hadToWait = true;
                _wanted.Set();
                if (waited.Elapsed >= maxWait)
                    return null;
                waiting?.Invoke(waited.Elapsed);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>On the turn's thread: has it, or takes it if no one else has it.</summary>
    private bool TryTake()
    {
        if (!_held)
        {
            try
            {
                if (!_turn.WaitOne(0))
                    return false;
            }
            catch (AbandonedMutexException)
            {
                // The BlackBox that had it ended without letting go: it is this one's now.
            }
            _held = true;
            // Whoever waited for it has it now; another that still waits says so again.
            _wanted.Reset();
        }
        _lastUse = Environment.TickCount64;
        return true;
    }

    public void Release() => _work.Add(() =>
    {
        if (_held)
            _turn.ReleaseMutex();
        _held = false;
        _handedOver = false;
    });
}
