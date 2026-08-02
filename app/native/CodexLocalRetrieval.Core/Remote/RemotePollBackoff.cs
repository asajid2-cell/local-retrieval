using System;

namespace CodexLocalRetrieval.Core.Remote;

// Why this exists: the command poll (POST /api/app-commands/lease) is the app's ONLY way to learn that the
// web queued something, and every poll costs a brand-new ssh.exe — full TCP + key exchange + auth + process
// spawn + teardown. At a fixed 3s that is ~20 handshakes/minute against a queue that is almost always empty.
//
// The two cheaper-looking fixes do not apply here, both checked rather than assumed:
//   * SSH connection multiplexing (ControlMaster/ControlPath/ControlPersist) is NOT supported by Windows
//     OpenSSH — 9.5p2 on this host fails the moment a ControlPath is handed to it ("getsockname failed: Not
//     a socket"), while the identical command without ControlMaster connects fine. So it is not an option.
//   * A server-side long poll (block until a command arrives) is the real architectural fix, but the relay's
//     lease handler answers synchronously and takes no wait parameter, so it needs a relay change first.
//
// That leaves shaping the poll itself. An empty queue is the overwhelmingly common case, so we hold the fast
// cadence while anything is happening and stretch toward an idle cadence once the queue has proved quiet,
// snapping back the instant a command actually lands. Latency is only ever spent on an idle system.
//
// Deliberately a pure state machine — no clock, no timer, no I/O — so the policy is testable on its own and
// both callers (the GUI DispatcherTimer and the headless RemoteBridge loop delay) share one behaviour.
public sealed class RemotePollBackoff
{
    public static readonly TimeSpan DefaultActive = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromSeconds(15);
    public const int DefaultEmptyPollsBeforeBackoff = 5;

    private readonly object _gate = new();
    private int _consecutiveEmpty;
    private TimeSpan _current;

    public RemotePollBackoff(
        TimeSpan? active = null,
        TimeSpan? idle = null,
        int emptyPollsBeforeBackoff = DefaultEmptyPollsBeforeBackoff)
    {
        var a = active ?? DefaultActive;
        var i = idle ?? DefaultIdle;
        if (a <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(active), "active interval must be positive");
        if (i < a)
            throw new ArgumentOutOfRangeException(nameof(idle), "idle interval must be >= the active interval");
        if (emptyPollsBeforeBackoff < 1)
            throw new ArgumentOutOfRangeException(nameof(emptyPollsBeforeBackoff), "threshold must be >= 1");

        ActiveInterval = a;
        IdleInterval = i;
        EmptyPollsBeforeBackoff = emptyPollsBeforeBackoff;
        _current = a;
    }

    public TimeSpan ActiveInterval { get; }
    public TimeSpan IdleInterval { get; }
    public int EmptyPollsBeforeBackoff { get; }

    /// The interval the next poll should wait. Starts at (and returns to) <see cref="ActiveInterval"/>.
    public TimeSpan Current { get { lock (_gate) return _current; } }

    /// How many polls in a row have come back with nothing to do.
    public int ConsecutiveEmpty { get { lock (_gate) return _consecutiveEmpty; } }

    /// True once the poll has actually stretched past its fast cadence.
    public bool IsBackedOff { get { lock (_gate) return _current > ActiveInterval; } }

    /// A poll that returned no commands. Holds the fast cadence until the queue has been quiet
    /// <see cref="EmptyPollsBeforeBackoff"/> times in a row, then doubles per empty poll up to the idle cap.
    public TimeSpan OnEmptyPoll()
    {
        lock (_gate)
        {
            if (_consecutiveEmpty < int.MaxValue) _consecutiveEmpty++;

            if (_consecutiveEmpty < EmptyPollsBeforeBackoff)
            {
                _current = ActiveInterval;
                return _current;
            }

            // Double from the active cadence for each empty poll past the threshold, capped at idle.
            var steps = _consecutiveEmpty - EmptyPollsBeforeBackoff + 1;
            var ticks = ActiveInterval.Ticks;
            for (var s = 0; s < steps; s++)
            {
                if (ticks >= IdleInterval.Ticks) break;
                ticks *= 2;
            }
            _current = ticks >= IdleInterval.Ticks ? IdleInterval : TimeSpan.FromTicks(ticks);
            return _current;
        }
    }

    /// A poll that actually carried work. Snaps all the way back to the fast cadence — a user who just tapped
    /// the web is about to tap again, so one command re-arms full responsiveness immediately.
    public TimeSpan OnCommandsReceived()
    {
        lock (_gate)
        {
            _consecutiveEmpty = 0;
            _current = ActiveInterval;
            return _current;
        }
    }

    /// Local activity (settings change, sync push, app resume) — treat the system as live again.
    public void Reset() => OnCommandsReceived();
}
