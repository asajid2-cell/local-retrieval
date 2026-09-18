using System;

namespace CodexLocalRetrieval.Core.Remote;

// The cadence at which the GUI asks the local muxd control port whether it is there.
//
// Why this needs its own policy rather than a fixed tick: a failed muxd request is not a cheap failed
// request. The app answers it by relaunching muxd's scheduled task, which is a whole
// schtasks -> wscript -> powershell -> pythonw spawn chain, and then waits 1.2s for the retry. Against a
// muxd that is alive but no longer serving its control port that relaunch can NEVER succeed: muxd guards
// itself with a single-instance mutex, so the fresh launch starts, logs "refusing to start duplicate
// muxd", and exits. The 5s tab tick therefore became a permanent process-spawning loop - observed on this
// machine at ~5 spawns/minute continuously for three days, each one a full process tree plus a FATAL log
// line plus a second of a core.
//
// The fix is pacing, not removal: keep the fast cadence while muxd answers, stretch once it has proved
// unreachable, and snap straight back the moment it answers again so a recovered muxd is noticed
// immediately. User-initiated mux actions do not go through this path at all, so they still relaunch on
// demand - only the background watcher waits.
//
// Deliberately numbers-only and pure (the state machine itself lives in RemotePollBackoff) so the shipped
// cadence is testable rather than asserted in a comment.
public static class MuxdProbeCadence
{
    /// The cadence while muxd is answering. Matches the tab tick, so a live control port is still polled
    /// as often as it ever was.
    public static readonly TimeSpan Healthy = TimeSpan.FromSeconds(5);

    /// The ceiling once muxd has proved unreachable. Five minutes keeps a dead control port a background
    /// retry (a handful of probes an hour) while still recovering on its own inside the window a user
    /// would tolerate, and bounds the relaunch loop that this exists to stop.
    public static readonly TimeSpan Unreachable = TimeSpan.FromMinutes(5);

    /// Consecutive failures before the cadence starts stretching. A single blip - muxd restarting, a
    /// transient connect failure - must not push the probe out to minutes.
    public const int EmptyProbesBeforeBackoff = 3;

    /// The ramp this produces: 5s, 5s, 5s, 10s, 20s, 40s, 80s, 160s, then 300s forever.
    public static RemotePollBackoff NewBackoff() => new(Healthy, Unreachable, EmptyProbesBeforeBackoff);
}
