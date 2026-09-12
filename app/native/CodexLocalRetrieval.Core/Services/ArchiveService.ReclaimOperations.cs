using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Core.Services;

public sealed partial class ArchiveService
{
    private const string ReclaimOperationType = "reclaim";

    public async Task<ReclaimOperationResult> ExecuteReclaimOperationAsync(
        string intentId,
        string sessionId,
        string tool,
        string expectedRevision,
        Func<Task<string>> list,
        Func<string, string, string, Task<(bool ok, string detail)>> fencedRemove,
        Func<IReadOnlyList<string>, RunningSessions.KillResult>? processKill = null,
        SessionLaunchClaims.Options? claimOptions = null,
        SessionOwnerRecords.Options? recordOptions = null,
        CancellationToken cancellationToken = default)
    {
        intentId = (intentId ?? "").Trim();
        sessionId = (sessionId ?? "").Trim();
        tool = (tool ?? "").Trim().ToLowerInvariant();
        expectedRevision = (expectedRevision ?? "").Trim();
        if (intentId.Length == 0 || sessionId.Length == 0 || expectedRevision.Length == 0 || tool is not ("claude" or "codex"))
            return Refused("intentId, sessionId, tool, and expectedRevision are required");
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(fencedRemove);

        var fingerprint = ReclaimFingerprint(sessionId, tool, expectedRevision);
        if (Store.ManagementOperations.TryGetValue(intentId, out var prior))
        {
            if (prior.Type != ReclaimOperationType || prior.PayloadFingerprint != fingerprint)
                return Refused("intent collision");
            await LoadStoreStateAsync();
            if (!Store.ManagementOperations.TryGetValue(intentId, out prior)
                || prior.Type != ReclaimOperationType || prior.PayloadFingerprint != fingerprint)
                return Refused("intent receipt is no longer authoritative");
            var payload = ReadReclaimReceipt(prior);
            if (payload is null) return Uncertain("reclaim receipt is malformed");
            if (prior.State == "applied" && payload.Report is not null)
                return new(ReclaimOperationStatus.Applied, "already applied", payload.Report, Replay: true);
            if (prior.State == "started" && payload.Report is { } verifiedReport)
            {
                if (!verifiedReport.KillOk || !verifiedReport.MuxOk || verifiedReport.AnyClaimBlocking)
                    return Uncertain("persisted cleanup report does not verify completion");
                return await CommitReclaimAsync(intentId, fingerprint, prior, payload, verifiedReport, list, cancellationToken);
            }
            if (prior.State == "started")
                return await ResumeStartedReclaimAsync(prior, payload, list, fencedRemove, cancellationToken);
            return Uncertain("reclaim receipt has an unknown state");
        }

        var session = ResolveSessionByIdOrAlias(sessionId, tool);
        if (session is null) return Refused("chat not found in the authoritative archive");
        if (!string.Equals(RemoteManagementRevision(session), expectedRevision, StringComparison.Ordinal))
            return Refused("chat revision is stale; refresh and confirm again");

        var candidates = new[] { session.Id }.Concat(session.Aliases)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var receipt = new ManagementOperation { Type = ReclaimOperationType, PayloadFingerprint = fingerprint, State = "started" };
        ReclaimOperationReceiptPayload? frozen = null;
        var mux = await SessionReclaim.ProbeAndFencedRemoveMuxOwnerAsync(
            candidates, MultiplexSessionName(session), list, fencedRemove,
            async resolution =>
            {
                frozen = new ReclaimOperationReceiptPayload
                {
                    CanonicalSessionId = session.Id,
                    Aliases = candidates.Skip(1).ToList(),
                    MuxFence = resolution.Owner is null ? null : new(resolution.Owner.Name, resolution.Owner.SessionId, resolution.Owner.GenerationId)
                };
                receipt.ResultId = JsonSerializer.Serialize(frozen);
                receipt.UpdatedAt = DateTime.UtcNow.ToString("O");
                Store.ManagementOperations[intentId] = receipt;
                return await PersistStartedReceiptAsync(intentId, fingerprint, receipt.ResultId, cancellationToken);
            });

        if (frozen is null) return Refused(mux.Detail);
        if (!mux.ProbeOk || (mux.OwnerFound && !mux.TeardownVerified))
            return Uncertain(mux.Detail);

        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = candidates,
            KillMux = () => Task.FromResult(mux),
            Kill = processKill,
            ClaimOptions = claimOptions,
            RecordOptions = recordOptions,
            Cancellation = cancellationToken,
        });
        if (!report.KillOk || !report.MuxOk || report.AnyClaimBlocking)
            return Uncertain("reclaim cleanup is incomplete; " + report.Headline);
        frozen.Report = report;
        receipt.ResultId = JsonSerializer.Serialize(frozen);
        var verifiedCleanup = await PersistStartedReceiptAsync(intentId, fingerprint, receipt.ResultId, cancellationToken);
        if (!verifiedCleanup.ok) return Uncertain("cleanup completed but its report was not durably verified");
        return await CommitReclaimAsync(intentId, fingerprint, receipt, frozen, report, list, cancellationToken);
    }

    private async Task<ReclaimOperationResult> ResumeStartedReclaimAsync(
        ManagementOperation receipt,
        ReclaimOperationReceiptPayload payload,
        Func<Task<string>> list,
        Func<string, string, string, Task<(bool ok, string detail)>> fencedRemove,
        CancellationToken cancellationToken)
    {
        if (payload.MuxFence is null)
            return Uncertain("started reclaim has no frozen mux owner; cleanup completion is uncertain");
        var candidates = new[] { payload.CanonicalSessionId }.Concat(payload.Aliases).ToArray();
        var mux = await SessionReclaim.ProbeAndFencedRemoveMuxOwnerAsync(
            candidates, payload.MuxFence.Name, list, fencedRemove, requiredFence: payload.MuxFence);
        return Uncertain(mux.TeardownVerified
            ? "frozen mux owner was removed, but prior process/claim cleanup completion is uncertain"
            : mux.Detail);
    }

    private async Task<(bool ok, string detail)> PersistStartedReceiptAsync(string intentId, string fingerprint, string frozenPayload, CancellationToken ct)
    {
        try { await SaveAsync(ct); return (true, "started receipt persisted"); }
        catch (StoreGenerationConflictException)
        {
            await LoadStoreStateAsync();
            return (false, "store changed before the frozen owner receipt committed");
        }
        catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
        {
            await LoadStoreStateAsync();
            return Store.ManagementOperations.TryGetValue(intentId, out var durable)
                   && durable.Type == ReclaimOperationType && durable.State == "started"
                   && durable.PayloadFingerprint == fingerprint && durable.ResultId == frozenPayload
                ? (true, "started receipt verified")
                : (false, "started receipt outcome is uncertain");
        }
        catch (Exception ex) when (ex is DurableWriteException or IOException or OperationCanceledException)
        {
            await LoadStoreStateAsync();
            return (false, "started receipt did not commit: " + ex.Message);
        }
    }

    private async Task<ReclaimOperationResult> CommitReclaimAsync(
        string intentId, string fingerprint, ManagementOperation receipt, ReclaimOperationReceiptPayload payload,
        ReclaimReport report, Func<Task<string>> list, CancellationToken ct)
    {
        var historySnapshot = JsonSerializer.Deserialize<Dictionary<string, MuxTabRecord>>(
            JsonSerializer.Serialize(Store.MuxTabHistory))!;
        try
        {
            var listing = await list();
            var rows = SessionReclaim.ParseMuxRowsStrict(listing, out _);
            if (rows is null)
                return Uncertain("cleanup completed but final mux listing could not be verified");
            var candidates = new[] { payload.CanonicalSessionId }.Concat(payload.Aliases).ToArray();
            if (SessionReclaim.ResolveMuxOwner(rows, candidates, payload.MuxFence?.Name).Status != ReclaimMuxOwnerStatus.Absent)
                return Uncertain("cleanup completed but an owner or replacement is present in the final mux listing");
            var pruned = SessionReclaim.PruneStaleMuxCurrentFromListing(Store.MuxTabHistory,
                new[] { payload.CanonicalSessionId }.Concat(payload.Aliases).ToArray(), listing);
            report = report with { PrunedMuxTabs = pruned };
            payload.Report = report;
            receipt.ResultId = JsonSerializer.Serialize(payload);
            receipt.State = "applied";
            receipt.Detail = report.Headline;
            receipt.UpdatedAt = DateTime.UtcNow.ToString("O");
            Store.ManagementOperations[intentId] = receipt;
            await SaveAsync(ct);
            return new(ReclaimOperationStatus.Applied, report.Headline, report);
        }
        catch (StoreGenerationConflictException)
        {
            Store.MuxTabHistory = historySnapshot;
            await LoadStoreStateAsync();
            return Uncertain("cleanup completed but app-store changed before prune/receipt commit");
        }
        catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
        {
            await LoadStoreStateAsync();
            if (Store.ManagementOperations.TryGetValue(intentId, out var durable)
                && durable.State == "applied" && durable.PayloadFingerprint == fingerprint
                && ReadReclaimReceipt(durable)?.Report is { } durableReport)
                return new(ReclaimOperationStatus.Applied, "reclaim committed; receipt verified", durableReport);
            return Uncertain("cleanup completed; prune/receipt commit outcome is uncertain");
        }
        catch (Exception ex) when (ex is DurableWriteException or IOException or JsonException or OperationCanceledException)
        {
            Store.MuxTabHistory = historySnapshot;
            await LoadStoreStateAsync();
            return Uncertain("cleanup completed but prune/receipt did not commit: " + ex.Message);
        }
    }

    private static ReclaimOperationReceiptPayload? ReadReclaimReceipt(ManagementOperation receipt)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ReclaimOperationReceiptPayload>(receipt.ResultId);
            return payload?.Version == 1 && payload.CanonicalSessionId.Length > 0 ? payload : null;
        }
        catch (JsonException) { return null; }
    }

    private static string ReclaimFingerprint(string sessionId, string tool, string expectedRevision)
    {
        var semantic = JsonSerializer.Serialize(new { type = ReclaimOperationType, sessionId, tool, expectedRevision });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semantic))).ToLowerInvariant();
    }

    private static ReclaimOperationResult Refused(string detail) => new(ReclaimOperationStatus.Refused, detail);
    private static ReclaimOperationResult Uncertain(string detail) => new(ReclaimOperationStatus.Uncertain, detail);
}
