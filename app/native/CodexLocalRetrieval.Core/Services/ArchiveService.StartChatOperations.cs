using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Core.Services;

public sealed record StartChatPreparationRequest(
    string IntentId,
    string MuxName,
    string DeckId,
    string Tool = "",
    string WorkspaceId = "",
    string Subfolder = "",
    string CheckpointId = "",
    string CheckpointRevision = "",
    string CollectionId = "",
    string CollectionRevision = "",
    string CollectionName = "",
    string Title = "",
    string Phrase = "",
    string LaunchMode = ArchiveService.NativeLaunchMode,
    string HandoffFromId = "");

public sealed record StartChatPreparationResult(
    bool Ok,
    string State,
    string Detail,
    string ResultId,
    StartChatLaunchDescriptor? Launch,
    bool Replay = false,
    bool Uncertain = false);

public static class StartChatCoordinator
{
    public static async Task<StartChatPreparationResult> ExecuteAsync(
        StartChatPreparationRequest request,
        Func<Task<StartChatPreparationResult>> prepare,
        Func<bool, string, string, Task<StartChatPreparationResult>> finalize,
        Func<object, Task<string>> muxRequest)
    {
        var prepared = await prepare();
        if (!prepared.Ok || prepared.State != "ready" || prepared.Launch is null) return prepared;
        var launch = prepared.Launch;
        bool applied;
        string detail;
        var generation = "";
        try
        {
            var response = await muxRequest(new
            {
                t = "create", s = launch.MuxName, cmd = launch.Command,
                cols = 140, rows = 40, sessionId = launch.SessionId,
                aliases = launch.Aliases, identityPending = string.IsNullOrWhiteSpace(launch.SessionId),
                intentId = request.IntentId.Trim()
            });
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.TryGetProperty("t", out var type) && type.GetString() == "created"
                && root.TryGetProperty("s", out var name) && name.GetString() == launch.MuxName)
            {
                if (!root.TryGetProperty("generationId", out var generationValue)
                    || generationValue.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(generationValue.GetString()))
                    return prepared with { Ok = false, Uncertain = true, Detail = "mux launch generation is unverified" };
                generation = generationValue.GetString()!;
                applied = true;
                detail = "started PC-local mux session: " + launch.MuxName;
            }
            else if (root.TryGetProperty("t", out type) && type.GetString() == "err"
                     && root.TryGetProperty("retryable", out var retryable)
                     && retryable.ValueKind == JsonValueKind.False)
            {
                applied = false;
                detail = "muxd definitively refused startchat";
            }
            else return prepared with { Ok = false, Uncertain = true, Detail = "mux launch outcome uncertain; retry the same intent" };
        }
        catch
        {
            return prepared with { Ok = false, Uncertain = true, Detail = "mux launch response unavailable; retry the same intent" };
        }
        var terminal = await finalize(applied, detail, generation);
        return terminal.State is "applied" or "failed" ? terminal
            : terminal with { Ok = false, Uncertain = true };
    }
}

public sealed partial class ArchiveService
{
    private const string StartChatOperationType = "startchat";

    // Preparation only: this method never invokes muxd or any launch callback. Its success boundary is a
    // durable `ready` receipt containing the exact launch descriptor dispatch must replay verbatim.
    public async Task<StartChatPreparationResult> PrepareStartChatAsync(StartChatPreparationRequest request,
        Func<string, string, string, string>? startCommandFactory = null)
    {
        var normalized = NormalizeStartChatRequest(request);
        if (normalized.IntentId.Length == 0)
            return StartChatRefused("intentId required");
        var fingerprint = StartChatFingerprint(normalized);

        ManagementOperation? parent = null;
        if (Store.ManagementOperations.TryGetValue(normalized.IntentId, out var observed))
        {
            var authoritative = await ReloadStartChatReceiptAsync(normalized.IntentId, fingerprint, observed);
            if (authoritative.Result is not null) return authoritative.Result;
            parent = authoritative.Prepared;
        }

        StartChatValidation validation;
        if (parent is null)
        {
            // All semantic IDs, revisions, launch prerequisites, and the blank-start ownership slot are
            // checked before even the prepared receipt is written.
            validation = ValidateStartChatPreparation(normalized);
            if (!validation.Ok) return StartChatRefused(validation.Detail);

            parent = new ManagementOperation
            {
                Type = StartChatOperationType,
                PayloadFingerprint = fingerprint,
                State = "prepared",
                Detail = "startchat preparation accepted",
                StartChatPreparedTool = validation.Tool,
                StartChatPreparedWorkingDirectory = validation.WorkingDirectory,
                UpdatedAt = DateTime.UtcNow.ToString("O"),
            };
            var preparedSnapshot = CaptureStartChatState();
            Store.ManagementOperations[normalized.IntentId] = parent;
            try
            {
                await SaveAsync();
            }
            catch (StoreGenerationConflictException)
            {
                RestoreStartChatState(preparedSnapshot);
                await LoadStoreStateAsync();
                var reconciled = ReconcilePreparedStartChat(normalized.IntentId, fingerprint);
                if (reconciled.Result is not null) return reconciled.Result;
                parent = reconciled.Prepared;
            }
            catch (DurableWriteException error) when (!error.Committed && !error.VerificationUnknown)
            {
                RestoreStartChatState(preparedSnapshot);
                return StartChatRefused("startchat acceptance rolled back before commit");
            }
            catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
            {
                await LoadStoreStateAsync();
                var reconciled = ReconcilePreparedStartChat(normalized.IntentId, fingerprint);
                if (reconciled.Result is not null) return reconciled.Result;
                parent = reconciled.Prepared;
            }
            catch (IOException error)
            {
                RestoreStartChatState(preparedSnapshot);
                return StartChatRefused("startchat acceptance rolled back before commit: " + error.Message);
            }
            if (parent is null)
                return StartChatUncertain("startchat acceptance was not proven durable");
        }

        validation = StartChatValidation.FromPrepared(parent);
        if (!validation.Ok)
            return StartChatUncertain("prepared startchat receipt is missing its frozen preparation authority");

        string resultId;
        var targetCollectionId = normalized.CollectionId;
        ArchiveSession? branch = null;

        // Child operations are independently receipt-backed. A restart after either child commit resumes here
        // from the durable parent without re-validating a now-stale source object.
        if (normalized.CheckpointId.Length > 0)
        {
            (bool Ok, string Detail, string ResultId) spawned;
            try
            {
                spawned = await ExecuteBranchOperationAsync(
                    "checkpointspawn",
                    StartChatSubIntent(normalized.IntentId, "spawn"),
                    normalized.CheckpointId,
                    validation.Tool,
                    normalized.CheckpointRevision);
            }
            catch (DurableWriteException error) when (!error.Committed && !error.VerificationUnknown)
            {
                return StartChatUncertain("checkpoint preparation rolled back; retry the prepared intent");
            }
            catch (IOException error)
            {
                return StartChatUncertain("checkpoint preparation remains pending: " + error.Message);
            }
            if (!spawned.Ok) return StartChatUncertain("checkpoint preparation remains pending: " + spawned.Detail);
            resultId = spawned.ResultId;
            if (!Store.Sessions.TryGetValue(resultId, out branch))
            {
                await LoadStoreStateAsync();
                if (!Store.Sessions.TryGetValue(resultId, out branch))
                    return StartChatUncertain("spawn receipt exists but its branch is not authoritative");
            }
        }
        else
        {
            resultId = StartChatSubIntent(normalized.IntentId, "pending");
        }

        if (normalized.CollectionName.Length > 0)
        {
            var created = await ExecuteContainerOperationAsync(
                "collectioncreate",
                StartChatSubIntent(normalized.IntentId, "collection"),
                "",
                null,
                name: normalized.CollectionName,
                deckId: normalized.DeckId);
            if (!created.Ok) return StartChatUncertain("collection preparation remains pending: " + created.Detail);
            targetCollectionId = created.ResultId;
        }

        // Child reconciliation can reload the store. Another process may have completed the parent meanwhile.
        if (Store.ManagementOperations.TryGetValue(normalized.IntentId, out observed))
        {
            if (observed.Type != StartChatOperationType || observed.PayloadFingerprint != fingerprint)
                return StartChatRefused("intent collision");
            if (observed.State != "prepared") return ReceiptResult(observed, replay: true);
            parent = observed;
        }

        var snapshot = CaptureStartChatState();
        var createdDirectory = false;
        string? createdDirectoryPath = null;
        StartChatPreparationResult AbortReady(string detail)
        {
            RestoreStartChatState(snapshot);
            RemoveStartChatDirectoryIfEmpty(createdDirectory, createdDirectoryPath);
            return StartChatUncertain(detail);
        }
        try
        {
            StartChatLaunchDescriptor launch;
            if (branch is not null)
            {
                if (!Store.Sessions.TryGetValue(branch.Id, out branch))
                    return AbortReady("prepared branch disappeared before the ready commit");
                if (targetCollectionId.Length > 0 && !Store.Collections.ContainsKey(targetCollectionId))
                    return AbortReady("prepared target collection disappeared before the ready commit");
                ApplyPreparedStartChatMetadata(branch, normalized, targetCollectionId);
                var resolvedLaunch = BuildResumeLaunch(branch, launchModeOverride: normalized.LaunchMode);
                var command = BuildMultiplexCommand(branch, launchModeOverride: normalized.LaunchMode);
                if (command.Length == 0)
                    return AbortReady("prepared checkpoint no longer has its trusted launch command");
                launch = new StartChatLaunchDescriptor
                {
                    MuxName = normalized.MuxName,
                    Command = command,
                    WorkingDirectory = resolvedLaunch.WorkingDirectory,
                    Tool = branch.Tool.ToLowerInvariant(),
                    LaunchMode = normalized.LaunchMode,
                    SessionId = branch.Id,
                    Aliases = branch.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
            }
            else
            {
                var cwd = validation.WorkingDirectory;
                if (!Directory.Exists(cwd))
                {
                    createdDirectoryPath = cwd;
                    try
                    {
                        Directory.CreateDirectory(cwd);
                        createdDirectory = true;
                    }
                    catch (Exception error)
                    {
                        // A failed create does not establish ownership of any directory now at this path.
                        return AbortReady("could not create the prepared subfolder: " + error.Message);
                    }
                }
                var command = startCommandFactory is null
                    ? BuildMultiplexStartCommand(validation.Tool, cwd, launchModeOverride: normalized.LaunchMode)
                    : startCommandFactory(validation.Tool, cwd, normalized.LaunchMode);
                if (command.Length == 0)
                    return AbortReady($"the prepared {validation.Tool} launch is no longer available");
                if (Store.PendingNewChats.Any(p =>
                        !string.Equals(p.IntentId, resultId, StringComparison.Ordinal)
                        && string.Equals(p.Tool, validation.Tool, StringComparison.OrdinalIgnoreCase)
                        && StartChatPathEquals(p.Cwd, cwd)))
                    return AbortReady("the prepared tool and workspace slot became busy");
                var existingPending = Store.PendingNewChats.FirstOrDefault(p =>
                    string.Equals(p.IntentId, resultId, StringComparison.Ordinal));
                if (existingPending is null)
                {
                    Store.PendingNewChats.Add(new PendingNewChat
                    {
                        IntentId = resultId,
                        Cwd = cwd,
                        Tool = validation.Tool,
                        LaunchMode = normalized.LaunchMode,
                        HandoffFromId = normalized.HandoffFromId,
                        CollectionId = targetCollectionId,
                        CustomTitle = CleanTitle(normalized.Title),
                        SpecialPhrase = normalized.Phrase,
                        CreatedAt = DateTime.UtcNow.ToString("O"),
                        KnownIds = ClaudeFolderTranscripts(cwd).Select(t => t.id).ToList(),
                    });
                }
                launch = new StartChatLaunchDescriptor
                {
                    MuxName = normalized.MuxName,
                    Command = command,
                    WorkingDirectory = cwd,
                    Tool = validation.Tool,
                    LaunchMode = normalized.LaunchMode,
                    PendingIntentId = resultId,
                };
            }

            parent.ResultId = resultId;
            parent.State = "ready";
            parent.Detail = "startchat prepared";
            parent.StartChatLaunch = CloneStartChatLaunch(launch);
            parent.UpdatedAt = DateTime.UtcNow.ToString("O");
            Store.ManagementOperations[normalized.IntentId] = parent;
            await SaveAsync();
            return new(true, "ready", "startchat prepared", resultId, CloneStartChatLaunch(launch));
        }
        catch (StoreGenerationConflictException)
        {
            RestoreStartChatState(snapshot);
            RemoveStartChatDirectoryIfEmpty(createdDirectory, createdDirectoryPath);
            await LoadStoreStateAsync();
            return AuthoritativeStartChatResult(normalized.IntentId, fingerprint)
                   ?? StartChatUncertain("store changed; startchat preparation was not proven durable");
        }
        catch (DurableWriteException error) when (!error.Committed && !error.VerificationUnknown)
        {
            RestoreStartChatState(snapshot);
            RemoveStartChatDirectoryIfEmpty(createdDirectory, createdDirectoryPath);
            return StartChatRefused("startchat preparation rolled back before commit");
        }
        catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
        {
            await LoadStoreStateAsync();
            return AuthoritativeStartChatResult(normalized.IntentId, fingerprint)
                   ?? StartChatUncertain("startchat preparation outcome uncertain; reload and reconcile intent");
        }
        catch (IOException error)
        {
            RestoreStartChatState(snapshot);
            RemoveStartChatDirectoryIfEmpty(createdDirectory, createdDirectoryPath);
            return StartChatRefused("startchat preparation rolled back before commit: " + error.Message);
        }
        catch (Exception error)
        {
            // No durable write is in flight here: SaveAsync classifies its own failures above. Any other
            // exception is therefore a known local construction failure and must not leave mutable store
            // objects available for an unrelated later save.
            RestoreStartChatState(snapshot);
            RemoveStartChatDirectoryIfEmpty(createdDirectory, createdDirectoryPath);
            return StartChatRefused("startchat preparation rolled back before commit: " + error.Message);
        }
    }

    public Task<StartChatPreparationResult> MarkStartChatAppliedAsync(StartChatPreparationRequest request, string detail, string generation)
        => string.IsNullOrWhiteSpace(generation)
            ? Task.FromResult(StartChatUncertain("mux launch generation is unverified"))
            : FinalizeStartChatAsync(request, "applied", detail, removePending: false, generation: generation);

    public Task<StartChatPreparationResult> MarkStartChatFailedAsync(StartChatPreparationRequest request, string detail)
        => FinalizeStartChatAsync(request, "failed", detail, removePending: true);

    private async Task<StartChatPreparationResult> FinalizeStartChatAsync(
        StartChatPreparationRequest request, string state, string detail, bool removePending, string generation = "")
    {
        var normalized = NormalizeStartChatRequest(request);
        var fingerprint = StartChatFingerprint(normalized);
        if (!Store.ManagementOperations.TryGetValue(normalized.IntentId, out var receipt)
            || receipt.Type != StartChatOperationType
            || receipt.PayloadFingerprint != fingerprint)
            return StartChatRefused("startchat receipt missing or intent collision");
        if (receipt.State is "applied" or "failed")
            return ReceiptResult(receipt, replay: true);
        if (receipt.State != "ready" || receipt.StartChatLaunch is null)
            return StartChatRefused("startchat is not ready to finalize");

        var snapshot = CaptureStartChatState();
        try
        {
            if (removePending && receipt.StartChatLaunch.PendingIntentId.Length > 0)
            {
                var exact = receipt.StartChatLaunch.PendingIntentId;
                Store.PendingNewChats.RemoveAll(p => string.Equals(p.IntentId, exact, StringComparison.Ordinal));
            }
            receipt.State = state;
            receipt.StartChatMuxGeneration = generation;
            receipt.Detail = detail;
            receipt.UpdatedAt = DateTime.UtcNow.ToString("O");
            await SaveAsync();
            return ReceiptResult(receipt);
        }
        catch (StoreGenerationConflictException)
        {
            RestoreStartChatState(snapshot);
            await LoadStoreStateAsync();
            return AuthoritativeStartChatResult(normalized.IntentId, fingerprint)
                   ?? StartChatUncertain("store changed; startchat finalization was not proven durable");
        }
        catch (DurableWriteException error) when (!error.Committed && !error.VerificationUnknown)
        {
            RestoreStartChatState(snapshot);
            return StartChatRefused("startchat finalization rolled back before commit");
        }
        catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
        {
            await LoadStoreStateAsync();
            return AuthoritativeStartChatResult(normalized.IntentId, fingerprint)
                   ?? StartChatUncertain("startchat finalization outcome uncertain; reload and reconcile intent");
        }
        catch (IOException error)
        {
            RestoreStartChatState(snapshot);
            return StartChatRefused("startchat finalization rolled back before commit: " + error.Message);
        }
    }

    public async Task<bool> ReconcileStartChatBindingsAsync(string listing)
    {
        using var document = JsonDocument.Parse(listing);
        if (!document.RootElement.TryGetProperty("list", out var rows)
            || rows.ValueKind != JsonValueKind.Array) return false;
        var snapshot = CaptureStartChatState();
        var changed = false;
        try
        {
            foreach (var receipt in Store.ManagementOperations.Values)
            {
                if (receipt.Type != StartChatOperationType || receipt.State != "applied"
                    || string.IsNullOrWhiteSpace(receipt.StartChatMuxGeneration)
                    || receipt.StartChatLaunch is not { PendingIntentId.Length: > 0 } launch) continue;
                var pending = Store.PendingNewChats.SingleOrDefault(p => p.IntentId == launch.PendingIntentId);
                if (pending is null) continue;
                var matches = rows.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object
                    && row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && name.GetString() == launch.MuxName
                    && row.TryGetProperty("generationId", out var generation) && generation.ValueKind == JsonValueKind.String
                    && generation.GetString() == receipt.StartChatMuxGeneration).ToArray();
                if (matches.Length != 1) continue;
                var match = matches[0];
                if (!match.TryGetProperty("identityPending", out var identityPending)
                    || identityPending.ValueKind != JsonValueKind.False
                    || !match.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var session = ResolveSessionByIdOrAlias(id.GetString()!, launch.Tool);
                if (session is null) continue;
                ArchiveCollection? collection = null;
                if (!string.IsNullOrEmpty(pending.CollectionId)
                    && !Store.Collections.TryGetValue(pending.CollectionId, out collection)) continue;
                if (collection is not null && !collection.SessionIds.Contains(session.Id)) collection.SessionIds.Add(session.Id);
                if (!string.IsNullOrWhiteSpace(pending.CustomTitle)) session.CustomTitle = CleanTitle(pending.CustomTitle);
                AddSpecialPhrase(session, pending.SpecialPhrase);
                session.LaunchMode = NormalizeLaunchMode(pending.LaunchMode);
                session.HandoffFromId = pending.HandoffFromId;
                Store.PendingNewChats.Remove(pending);
                changed = true;
            }
            if (!changed) return false;
            await SaveAsync();
            return true;
        }
        catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
        {
            await LoadStoreStateAsync();
            throw;
        }
        catch
        {
            RestoreStartChatState(snapshot);
            throw;
        }
    }

    private async Task<(ManagementOperation? Prepared, StartChatPreparationResult? Result)> ReloadStartChatReceiptAsync(
        string intentId, string fingerprint, ManagementOperation observed)
    {
        if (observed.Type != StartChatOperationType || observed.PayloadFingerprint != fingerprint)
            return (null, StartChatRefused("intent collision"));
        await LoadStoreStateAsync();
        var authoritative = ReconcilePreparedStartChat(intentId, fingerprint);
        return authoritative.Prepared is not null || authoritative.Result is not null
            ? authoritative
            : (null, StartChatUncertain("startchat receipt is no longer authoritative"));
    }

    private (ManagementOperation? Prepared, StartChatPreparationResult? Result) ReconcilePreparedStartChat(
        string intentId, string fingerprint)
    {
        if (!Store.ManagementOperations.TryGetValue(intentId, out var receipt)) return (null, null);
        if (receipt.Type != StartChatOperationType || receipt.PayloadFingerprint != fingerprint)
            return (null, StartChatRefused("intent collision"));
        return receipt.State == "prepared"
            ? (receipt, null)
            : (null, ReceiptResult(receipt, replay: true));
    }

    private StartChatPreparationResult? AuthoritativeStartChatResult(string intentId, string fingerprint)
    {
        if (!Store.ManagementOperations.TryGetValue(intentId, out var receipt)) return null;
        if (receipt.Type != StartChatOperationType || receipt.PayloadFingerprint != fingerprint)
            return StartChatRefused("intent collision");
        return ReceiptResult(receipt, replay: true);
    }

    private static StartChatPreparationResult ReceiptResult(ManagementOperation receipt, bool replay = false)
    {
        var ok = receipt.State is "ready" or "applied";
        return new(ok, receipt.State, receipt.Detail, receipt.ResultId,
            CloneStartChatLaunch(receipt.StartChatLaunch), replay, Uncertain: receipt.State is not ("ready" or "applied" or "failed"));
    }

    private sealed record StartChatValidation(bool Ok, string Detail, string Tool, string WorkingDirectory)
    {
        public static StartChatValidation FromPrepared(ManagementOperation receipt)
        {
            var tool = (receipt.StartChatPreparedTool ?? "").Trim().ToLowerInvariant();
            var cwd = (receipt.StartChatPreparedWorkingDirectory ?? "").Trim();
            return tool is "claude" or "codex" && cwd.Length > 0
                ? new(true, "", tool, cwd)
                : new(false, "prepared authority missing", "", "");
        }
    }

    private StartChatValidation ValidateStartChatPreparation(StartChatPreparationRequest r)
    {
        static StartChatValidation Refuse(string detail) => new(false, detail, "", "");
        if (!RemoteCommandProtocol.IsWellFormedEnvelopeToken(r.IntentId)) return Refuse("invalid intentId");
        if (r.MuxName.Length is < 1 or > 48 || r.MuxName.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '.' and not '_' and not '-'))
            return Refuse("invalid mux session name");
        if (!Store.Decks.Any(d => d.Id.Equals(r.DeckId, StringComparison.OrdinalIgnoreCase)))
            return Refuse("selected deck is no longer available");
        if (r.CollectionId.Length > 0 && r.CollectionName.Length > 0)
            return Refuse("choose an existing collection or a new collection, not both");
        if (r.CollectionId.Length > 0)
        {
            if (!Store.Collections.TryGetValue(r.CollectionId, out var collection)
                || !CollectionDeck(collection).Equals(r.DeckId, StringComparison.OrdinalIgnoreCase)
                || !CollectionManagementRevision(r.CollectionId).Equals(r.CollectionRevision, StringComparison.Ordinal))
                return Refuse("collection not found in the selected deck or revision is stale");
        }
        if (r.HandoffFromId.Length > 0)
        {
            var source = ResolveSessionByIdOrAlias(r.HandoffFromId, "codex");
            if (source is null || source.Id != r.HandoffFromId || r.Tool != "claude"
                || r.LaunchMode != GatewayLaunchMode || r.CheckpointId.Length > 0)
                return Refuse("handoff requires an exact Codex source and a new Claude Gateway chat");
        }
        if (r.CheckpointId.Length > 0)
        {
            if (r.Tool.Length > 0 || r.WorkspaceId.Length > 0 || r.Subfolder.Length > 0)
                return Refuse("checkpoint start cannot include tool, workspaceId, or subfolder");
            if (!Store.TemplateSnapshots.TryGetValue(r.CheckpointId, out var checkpoint)
                || !TemplateSnapshotManagementRevision(r.CheckpointId).Equals(r.CheckpointRevision, StringComparison.Ordinal))
                return Refuse("checkpoint not found or revision is stale");
            var launch = BuildResumeLaunch(PreparedCheckpointLaunchSession(checkpoint, r.IntentId, r.LaunchMode), launchModeOverride: r.LaunchMode);
            if (launch.Exe.Length == 0 || launch.Arguments.Length == 0)
                return Refuse("prepared checkpoint has no trusted launch command");
            return new(true, "", checkpoint.Tool.ToLowerInvariant(), launch.WorkingDirectory);
        }
        if (r.Tool is not ("claude" or "codex")) return Refuse("tool must be claude or codex");
        if (!new DiscoveryApi(this).TryResolveWorkspace(r.WorkspaceId, out var cwd))
            return Refuse("selected workspace is no longer available");
        if (!TryResolvePreparedSubfolder(cwd, r.Subfolder, out cwd, out var error))
            return Refuse(error);
        var trusted = BuildStartLaunchForPreparation(r.Tool, cwd, r.LaunchMode);
        if (trusted.Exe.Length == 0)
            return Refuse($"the {r.Tool} CLI was not found at a trusted path");
        var resultId = StartChatSubIntent(r.IntentId, "pending");
        if (Store.PendingNewChats.Any(p =>
                !string.Equals(p.IntentId, resultId, StringComparison.Ordinal)
                && string.Equals(p.Tool, r.Tool, StringComparison.OrdinalIgnoreCase)
                && StartChatPathEquals(p.Cwd, cwd)))
            return Refuse("another unresolved start already owns this tool and workspace");
        return new(true, "", r.Tool, cwd);
    }

    private ArchiveSession PreparedCheckpointLaunchSession(TemplateSnapshot checkpoint, string intentId, string launchMode) => new()
    {
        Id = StartChatSubIntent(intentId, "spawn-preview"),
        Tool = checkpoint.Tool,
        SourcePath = checkpoint.SnapshotPath,
        Workspace = checkpoint.Workspace,
        WorkspaceName = checkpoint.WorkspaceName,
        LaunchMode = launchMode,
    };

    private ResumeLaunch BuildStartLaunchForPreparation(string tool, string cwd, string launchMode)
    {
        // BuildStartLaunch falls back to the profile when a not-yet-created subfolder is selected. Use its
        // trusted executable/argument validation, but retain the exact validated future cwd in the receipt.
        var existing = Directory.Exists(cwd) ? cwd : Path.GetDirectoryName(cwd) ?? cwd;
        return BuildStartLaunch(tool, existing, launchModeOverride: launchMode) with { WorkingDirectory = cwd };
    }

    private static bool TryResolvePreparedSubfolder(string root, string subfolder, out string cwd, out string detail)
    {
        cwd = root;
        detail = "";
        if (subfolder.Length == 0) return true;
        if (subfolder is "." or ".." || Path.IsPathRooted(subfolder)
            || subfolder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || subfolder.Contains(Path.DirectorySeparatorChar) || subfolder.Contains(Path.AltDirectorySeparatorChar))
        {
            detail = "new subfolder must be one valid folder name";
            return false;
        }
        cwd = Path.Combine(root, subfolder);
        return true;
    }

    private void ApplyPreparedStartChatMetadata(ArchiveSession branch, StartChatPreparationRequest r, string collectionId)
    {
        if (r.Title.Length > 0) branch.CustomTitle = CleanTitle(r.Title);
        AddSpecialPhrase(branch, r.Phrase);
        branch.LaunchMode = r.LaunchMode;
        if (collectionId.Length > 0 && Store.Collections.TryGetValue(collectionId, out var collection)
            && !collection.SessionIds.Contains(branch.Id))
            collection.SessionIds.Add(branch.Id);
    }

    private static StartChatPreparationRequest NormalizeStartChatRequest(StartChatPreparationRequest r) => r with
    {
        IntentId = (r.IntentId ?? "").Trim(),
        MuxName = (r.MuxName ?? "").Trim(),
        DeckId = (r.DeckId ?? "").Trim(),
        Tool = (r.Tool ?? "").Trim().ToLowerInvariant(),
        WorkspaceId = (r.WorkspaceId ?? "").Trim(),
        Subfolder = (r.Subfolder ?? "").Trim(),
        CheckpointId = (r.CheckpointId ?? "").Trim(),
        CheckpointRevision = (r.CheckpointRevision ?? "").Trim(),
        CollectionId = (r.CollectionId ?? "").Trim(),
        CollectionRevision = (r.CollectionRevision ?? "").Trim(),
        CollectionName = (r.CollectionName ?? "").Trim(),
        Title = CleanTitle(r.Title ?? ""),
        Phrase = (r.Phrase ?? "").Trim(),
        LaunchMode = NormalizeLaunchMode(r.LaunchMode),
        HandoffFromId = (r.HandoffFromId ?? "").Trim(),
    };

    private static string StartChatFingerprint(StartChatPreparationRequest r) => Revision(JsonSerializer.Serialize(new
    {
        type = StartChatOperationType,
        r.MuxName,
        r.DeckId,
        r.Tool,
        r.WorkspaceId,
        r.Subfolder,
        r.CheckpointId,
        r.CheckpointRevision,
        r.CollectionId,
        r.CollectionRevision,
        r.CollectionName,
        r.Title,
        r.Phrase,
        r.LaunchMode,
        r.HandoffFromId,
    }));

    private static string StartChatSubIntent(string parent, string purpose)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parent + "|" + purpose))).ToLowerInvariant();
        return "sc-" + hash;
    }

    private static bool StartChatPathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void RemoveStartChatDirectoryIfEmpty(bool created, string? path)
    {
        if (!created || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch { }
    }

    private sealed record StartChatStateSnapshot(
        Dictionary<string, ManagementOperation> Operations,
        Dictionary<string, ArchiveSession> Sessions,
        Dictionary<string, ArchiveCollection> Collections,
        List<PendingNewChat> Pending);

    private StartChatStateSnapshot CaptureStartChatState() => new(
        Store.ManagementOperations.ToDictionary(k => k.Key, v => CloneManagementOperation(v.Value), StringComparer.Ordinal),
        Store.Sessions.ToDictionary(k => k.Key, v => JsonSerializer.Deserialize<ArchiveSession>(JsonSerializer.Serialize(v.Value))!, StringComparer.OrdinalIgnoreCase),
        Store.Collections.ToDictionary(k => k.Key, v => CloneCollection(v.Value), StringComparer.OrdinalIgnoreCase),
        Store.PendingNewChats.Select(p => JsonSerializer.Deserialize<PendingNewChat>(JsonSerializer.Serialize(p))!).ToList());

    private void RestoreStartChatState(StartChatStateSnapshot snapshot)
    {
        Store.ManagementOperations = snapshot.Operations.ToDictionary(k => k.Key, v => CloneManagementOperation(v.Value), StringComparer.Ordinal);
        Store.Sessions = snapshot.Sessions.ToDictionary(k => k.Key, v => JsonSerializer.Deserialize<ArchiveSession>(JsonSerializer.Serialize(v.Value))!, StringComparer.OrdinalIgnoreCase);
        Store.Collections = snapshot.Collections.ToDictionary(k => k.Key, v => CloneCollection(v.Value), StringComparer.OrdinalIgnoreCase);
        Store.PendingNewChats = snapshot.Pending.Select(p => JsonSerializer.Deserialize<PendingNewChat>(JsonSerializer.Serialize(p))!).ToList();
    }

    private static ManagementOperation CloneManagementOperation(ManagementOperation value) => new()
    {
        Type = value.Type,
        PayloadFingerprint = value.PayloadFingerprint,
        ResultId = value.ResultId,
        State = value.State,
        Detail = value.Detail,
        StartChatPreparedTool = value.StartChatPreparedTool,
        StartChatPreparedWorkingDirectory = value.StartChatPreparedWorkingDirectory,
        StartChatLaunch = CloneStartChatLaunch(value.StartChatLaunch),
        StartChatMuxGeneration = value.StartChatMuxGeneration,
        UpdatedAt = value.UpdatedAt,
    };

    private static StartChatLaunchDescriptor? CloneStartChatLaunch(StartChatLaunchDescriptor? value) => value is null ? null : new()
    {
        MuxName = value.MuxName,
        Command = value.Command,
        WorkingDirectory = value.WorkingDirectory,
        Tool = value.Tool,
        LaunchMode = value.LaunchMode,
        SessionId = value.SessionId,
        Aliases = value.Aliases.ToList(),
        PendingIntentId = value.PendingIntentId,
    };

    private static StartChatPreparationResult StartChatRefused(string detail)
        => new(false, "refused", detail, "", null);

    private static StartChatPreparationResult StartChatUncertain(string detail)
        => new(false, "uncertain", detail, "", null, Uncertain: true);
}
