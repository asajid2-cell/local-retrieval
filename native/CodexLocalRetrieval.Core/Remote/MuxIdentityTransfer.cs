using System.Text.Json;

namespace CodexLocalRetrieval.Core.Remote;

public static class MuxIdentityTransfer
{
    public sealed record Result(bool Ok, string Detail, bool AlreadyOwned);

    private sealed record MuxRow(string Name, bool Alive, string SessionId, string[] Aliases);

    public static async Task<Result> ExecuteAsync(
        string desiredName,
        string sessionId,
        IEnumerable<string>? aliases,
        Func<object, Task<string>> muxdRequest,
        Func<(bool Ok, Dictionary<string, HashSet<int>> Live, string Detail)>? liveScan = null,
        Func<int, (bool Ok, string Detail)>? killOwner = null,
        Func<string, IEnumerable<int>, (bool Ok, HashSet<int> Owned, string Detail)>? muxCustody = null)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(sessionId)) ids.Add(sessionId.Trim());
        foreach (var alias in aliases ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(alias)) ids.Add(alias.Trim());
        if (ids.Count == 0) return new(false, "takeover has no canonical session identity", false);

        List<MuxRow> rows;
        try
        {
            var text = await muxdRequest(new { t = "ls" });
            using var doc = JsonDocument.Parse(text);
            rows = doc.RootElement.GetProperty("list").EnumerateArray()
                .Select(row => new MuxRow(
                    row.GetProperty("name").GetString() ?? "",
                    row.TryGetProperty("alive", out var alive) && alive.ValueKind == JsonValueKind.True,
                    row.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "",
                    row.TryGetProperty("aliases", out var aa) && aa.ValueKind == JsonValueKind.Array
                        ? aa.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToArray()
                        : Array.Empty<string>()))
                .Where(row => ids.Contains(row.SessionId) || row.Aliases.Any(ids.Contains))
                .ToList();
        }
        catch (Exception ex)
        {
            return new(false, "could not inspect mux ownership before takeover: " + ex.Message, false);
        }

        var desiredPresent = rows.Any(row =>
            string.Equals(row.Name, desiredName, StringComparison.OrdinalIgnoreCase));
        var desiredAlive = rows.Any(row =>
            row.Alive && string.Equals(row.Name, desiredName, StringComparison.OrdinalIgnoreCase));
        foreach (var row in rows.Where(row =>
                     !string.Equals(row.Name, desiredName, StringComparison.OrdinalIgnoreCase)))
        {
            var removed = await DeleteMuxRowAsync(row.Name, muxdRequest);
            if (!removed.Ok)
                return new(false, $"could not remove existing mux owner '{row.Name}': {removed.Detail}", false);
        }

        var scanned = liveScan?.Invoke() ?? ScanLiveOwners();
        if (!scanned.Ok)
            return new(false, "could not verify local ownership before takeover: " + scanned.Detail, false);
        var live = scanned.Live;
        var liveDetail = scanned.Detail;

        var ownerPids = ids
            .Where(live.ContainsKey)
            .SelectMany(id => live[id])
            .Where(pid => pid > 0)
            .Distinct()
            .ToArray();
        var muxOwnedPids = new HashSet<int>();
        if (desiredPresent)
        {
            var custody = muxCustody?.Invoke(desiredName, ownerPids) ?? FindMuxCustody(desiredName, ownerPids);
            if (!custody.Ok) return new(false, custody.Detail, false);
            muxOwnedPids = custody.Owned;
        }

        var pidsToStop = ownerPids.Where(pid => !muxOwnedPids.Contains(pid)).ToArray();
        foreach (var ownerPid in pidsToStop)
        {
            var killed = killOwner?.Invoke(ownerPid) ?? KillOwner(ownerPid);
            if (!killed.Ok)
                return new(false, $"could not stop matching local owner pid {ownerPid}: {killed.Detail}", false);
        }

        if (pidsToStop.Length == 0)
            return desiredAlive
                ? new(true, "desired mux owner will be relaunched", true)
                : new(true, "ownership available", false);

        var deadline = DateTime.UtcNow.AddSeconds(12);
        do
        {
            scanned = liveScan?.Invoke() ?? ScanLiveOwners();
            if (!scanned.Ok)
                return new(false, "could not verify local owner exit: " + scanned.Detail, false);
            live = scanned.Live;
            liveDetail = scanned.Detail;
            var remainingExternal = ids
                .Where(live.ContainsKey)
                .SelectMany(id => live[id])
                .Any(pid => !muxOwnedPids.Contains(pid));
            if (!remainingExternal)
                return desiredAlive
                    ? new(true, "external ownership cleared; desired mux owner will be relaunched", true)
                    : new(true, "ownership transferred", false);
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);

        return new(false, "matching local owner did not exit after takeover", false);
    }

    private static (bool Ok, Dictionary<string, HashSet<int>> Live, string Detail) ScanLiveOwners()
    {
        var ok = RunningSessions.TryLiveSessionPids(out var live, out var detail);
        return (ok, live, detail);
    }

    private static (bool Ok, string Detail) KillOwner(int pid)
    {
        // No candidate ids: this pid IS the target (the handoff already identified it), so the id set is empty
        // rather than null - `null` is ambiguous between the two Kill overloads.
        var result = RunningSessions.Kill(Array.Empty<string>(), pid);
        return (result.ok, result.detail);
    }

    private static (bool Ok, HashSet<int> Owned, string Detail) FindMuxCustody(
        string muxName,
        IEnumerable<int> candidates)
    {
        var ok = RunningSessions.TryMuxOwnedAgentPids(muxName, candidates, out var owned, out var detail);
        return (ok, owned, detail);
    }

    private static async Task<(bool Ok, string Detail)> DeleteMuxRowAsync(
        string name,
        Func<object, Task<string>> muxdRequest)
    {
        try
        {
            var text = await muxdRequest(new { t = "kill", s = name });
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("t", out var type) && type.GetString() == "killed")
                return (true, "removed");
            var detail = doc.RootElement.TryGetProperty("m", out var message)
                ? message.GetString() ?? "muxd error"
                : "unexpected muxd response: " + text;
            return (false, detail);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
