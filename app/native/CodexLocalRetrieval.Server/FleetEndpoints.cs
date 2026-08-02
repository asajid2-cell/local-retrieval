using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Server;

public sealed record FleetSaveRequest(string? Name);
public sealed record FleetRestoreRequest(string? Name, List<string>? Ids, bool? DryRun);
public sealed record FleetRestoreRowResult(string SessionId, string Tool, string Outcome, string Message);

// The fleet surface: the always-on recorder's current view, saved states, and claim-gated bulk
// restore. Restore reuses the exact per-session open path (canonical resolve from the archive →
// SessionLauncher with its launch governor/claims) — this file adds selection and reporting, never
// its own launch mechanics.
public static class FleetEndpoints
{
    public static void MapFleet(this WebApplication app, FleetSnapshotService fleet, SessionOpenService openService)
    {
        app.MapGet("/api/fleet", () =>
        {
            FleetStore.TryReadCurrent(out var current, out _);
            FleetStore.TryListStates(out var states, out var listDetail);
            FleetWriterLock.TryReadHolder(out var holder, out _);
            var bootStamp = FleetStore.CurrentBootStampUtc();
            return Results.Json(new
            {
                bootStampUtc = bootStamp,
                writer = holder is null ? null : new { holder.Pid, holder.Host, holder.AcquiredAtUtc },
                current = current is null ? null : Summarize(current, bootStamp),
                states = states.Select(s => Summarize(s, bootStamp)).ToList(),
                listDetail,
            });
        });

        app.MapPost("/api/fleet/save", (FleetSaveRequest? req) =>
        {
            var name = (req?.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "name required" });
            var (ok, detail) = fleet.SaveNamed(name);
            return ok ? Results.Json(new { ok = true, name }) : Results.BadRequest(new { error = detail });
        });

        app.MapDelete("/api/fleet/states/{name}", (string name) =>
            FleetStore.TryDeleteState(name, out var detail)
                ? Results.Json(new { ok = true })
                : Results.BadRequest(new { error = detail }));

        app.MapPost("/api/fleet/restore", async (FleetRestoreRequest? req, CancellationToken ct) =>
        {
            var name = (req?.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "name required ('current' = the rolling record)" });

            FleetState? state;
            string detail;
            if (string.Equals(name, "current", StringComparison.OrdinalIgnoreCase))
            {
                if (!FleetStore.TryReadCurrent(out state, out detail) || state is null)
                    return Results.BadRequest(new { error = detail.Length > 0 ? detail : "no rolling fleet record yet" });
            }
            else if (!FleetStore.TryReadState(name, out state, out detail) || state is null)
                return Results.BadRequest(new { error = detail });

            var picks = state.Sessions;
            if (req?.Ids is { Count: > 0 } ids)
            {
                var wanted = new HashSet<string>(ids.Select(i => (i ?? "").Trim()).Where(i => i.Length > 0), StringComparer.OrdinalIgnoreCase);
                picks = picks.Where(s => wanted.Contains(s.SessionId)).ToList();
                var missing = wanted.Except(picks.Select(p => p.SessionId), StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0)
                    return Results.BadRequest(new { error = "not in state '" + name + "': " + string.Join(", ", missing) });
            }
            if (picks.Count == 0) return Results.BadRequest(new { error = "state has no sessions to restore" });

            // One liveness sweep for friendly pre-filtering; the launcher's claim gate remains the
            // authority (it re-checks under its reservation), so a stale answer here can only
            // produce a clearer message, never a double writer.
            RunningSessions.TryAllLiveSessionIds(out var liveIds, out _);

            var rows = new List<FleetRestoreRowResult>();
            foreach (var s in picks)
            {
                if (ct.IsCancellationRequested) break;
                if (liveIds.Contains(s.SessionId))
                {
                    rows.Add(new FleetRestoreRowResult(s.SessionId, s.Tool, "skipped", "already live on this PC"));
                    continue;
                }
                if (req?.DryRun == true)
                {
                    rows.Add(new FleetRestoreRowResult(s.SessionId, s.Tool, "would-open", "dry run"));
                    continue;
                }
                var result = await openService.OpenAsync(s.SessionId, new OpenRequest("terminal"), ct);
                rows.Add(new FleetRestoreRowResult(s.SessionId, s.Tool, result.Ok ? "opened" : "refused", result.Message));
            }

            var opened = rows.Count(r => r.Outcome == "opened");
            var refused = rows.Count(r => r.Outcome == "refused");
            return Results.Json(new { ok = refused == 0, opened, refused, rows });
        });
    }

    private static object Summarize(FleetState s, string currentBootStamp) => new
    {
        name = s.Name,
        kind = s.Kind,
        savedAtUtc = s.SavedAtUtc,
        bootStampUtc = s.BootStampUtc,
        thisBoot = FleetStore.SameBoot(s.BootStampUtc, currentBootStamp),
        sessionCount = s.Sessions.Count,
        sessions = s.Sessions.Select(x => new { x.Tool, x.SessionId, x.Pids, x.FirstSeenUtc, x.LastSeenUtc }).ToList(),
    };
}
