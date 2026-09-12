using System.Text.Json;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Services;

public sealed partial class ArchiveService
{
    private static readonly HashSet<string> BranchOperationTypes = new(StringComparer.Ordinal)
    {
        "checkpointcreate", "checkpointrename", "checkpointdelete", "checkpointspawn", "branchcreate"
    };

    public async Task<(bool Ok, string Detail, string ResultId)> ExecuteBranchOperationAsync(
        string type, string intentId, string objectId, string tool, string expectedRevision, string? name = null)
    {
        type = (type ?? "").Trim().ToLowerInvariant();
        intentId = (intentId ?? "").Trim();
        objectId = (objectId ?? "").Trim();
        tool = (tool ?? "").Trim().ToLowerInvariant();
        expectedRevision ??= "";
        name = (name ?? "").Trim();
        if (intentId.Length == 0 || !BranchOperationTypes.Contains(type))
            return (false, "unsupported branch operation or intentId required", "");

        var fingerprint = Revision(JsonSerializer.Serialize(new { type, objectId, tool, expectedRevision, name }));
        if (Store.ManagementOperations.TryGetValue(intentId, out var prior))
        {
            if (!prior.Type.Equals(type, StringComparison.Ordinal) || !prior.PayloadFingerprint.Equals(fingerprint, StringComparison.Ordinal))
                return (false, "intent collision", "");
            if (prior.State == "applied")
            {
                await LoadStoreStateAsync();
                if (Store.ManagementOperations.TryGetValue(intentId, out var authoritative)
                    && authoritative.State == "applied"
                    && authoritative.PayloadFingerprint == fingerprint)
                    return (true, "already applied", authoritative.ResultId);
                return (false, "intent receipt is no longer authoritative", "");
            }
        }

        ArchiveSession? source = null;
        TemplateSnapshot? snapshot = null;
        switch (type)
        {
            case "checkpointcreate":
            case "branchcreate":
                source = ResolveSessionByIdOrAlias(objectId, tool);
                if (source is null || !string.Equals(expectedRevision, RemoteManagementRevision(source), StringComparison.Ordinal))
                    return (false, "source chat not found or revision is stale", "");
                break;
            case "checkpointrename":
            case "checkpointdelete":
            case "checkpointspawn":
                if (!Store.TemplateSnapshots.TryGetValue(objectId, out snapshot))
                    return (false, "checkpoint not found", "");
                if (!string.Equals(expectedRevision, TemplateSnapshotManagementRevision(objectId), StringComparison.Ordinal))
                    return (false, "checkpoint not found or revision is stale", "");
                break;
        }
        if ((type == "checkpointrename" || type == "checkpointcreate") && name.Length == 0)
            return (false, "name required", "");

        // Do not publish a prepared receipt before the operation has done its file work. A failed
        // operation must not leave a receipt that a later unrelated SaveAsync can persist.
        var priorReceipt = Store.ManagementOperations.TryGetValue(intentId, out var existingReceipt)
            ? existingReceipt
            : null;
        var receipt = new ManagementOperation { Type = type, PayloadFingerprint = fingerprint, State = "prepared" };
        Action<string> applyReceipt = resultId =>
        {
            receipt.ResultId = resultId;
            receipt.State = "applied";
            receipt.UpdatedAt = DateTime.UtcNow.ToString("O");
            Store.ManagementOperations[intentId] = receipt;
        };
        void RestoreReceipt()
        {
            if (priorReceipt is null) Store.ManagementOperations.Remove(intentId);
            else Store.ManagementOperations[intentId] = priorReceipt;
        }
        bool HasAuthoritativeReceipt(out string resultId)
        {
            if (Store.ManagementOperations.TryGetValue(intentId, out var authoritative)
                && authoritative.State == "applied"
                && authoritative.PayloadFingerprint == fingerprint)
            {
                resultId = authoritative.ResultId;
                return true;
            }
            resultId = "";
            return false;
        }
        (bool Ok, string Detail, string ResultId) FinishSuccess(string detail, string resultId)
        {
            return HasAuthoritativeReceipt(out var authoritativeId)
                ? (true, detail, authoritativeId)
                : (false, "branch operation outcome uncertain; reload and reconcile intent", "");
        }
        try
        {
            switch (type)
            {
                case "checkpointcreate":
                    var created = await CreateTemplateSnapshotCoreAsync(source!, name, null, applyReceipt);
                    if (!created.Ok) { RestoreReceipt(); return (false, created.Message, ""); }
                    return FinishSuccess(created.Message, created.Snapshot!.Id);
                case "checkpointrename":
                    var renamed = await RenameTemplateSnapshotCoreAsync(objectId, name, applyReceipt);
                    if (!renamed) { RestoreReceipt(); return (false, "checkpoint rename failed", ""); }
                    return FinishSuccess("checkpoint renamed", objectId);
                case "checkpointdelete":
                    var deleted = await DeleteTemplateSnapshotCoreAsync(objectId, applyReceipt);
                    if (!deleted) { RestoreReceipt(); return (false, "checkpoint delete failed", ""); }
                    return FinishSuccess("checkpoint deleted", objectId);
                case "checkpointspawn":
                    var spawned = await SpawnTemplateCoreAsync(snapshot, applyReceipt);
                    if (!spawned.Ok) { RestoreReceipt(); return (false, spawned.Message, ""); }
                    return FinishSuccess(spawned.Message, spawned.Branch!.Id);
                case "branchcreate":
                    var branch = await CreateNativeBranchAsync(source!, ResolveSessionSourcePath(source!), source!.DisplayTitle, source.Id, appliedReceipt: applyReceipt);
                    if (!branch.Ok) { RestoreReceipt(); return (false, branch.Message, ""); }
                    return FinishSuccess(branch.Message, branch.Branch!.Id);
                default:
                    RestoreReceipt();
                    return (false, "unsupported branch operation", "");
            }
        }
        catch (StoreGenerationConflictException)
        {
            RestoreReceipt();
            await LoadStoreStateAsync();
            return (false, "store changed; reload and reconcile intent", "");
        }
        catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
        {
            await LoadStoreStateAsync();
            if (HasAuthoritativeReceipt(out var resultId))
                return (true, "branch operation committed; receipt verified", resultId);
            // The file may have committed even when the receipt did not. Never remove it or
            // manufacture a negative receipt in this uncertain state.
            return (false, "branch operation outcome uncertain; reload and reconcile intent", "");
        }
        catch (DurableWriteException error) when (!error.Committed && !error.VerificationUnknown)
        {
            RestoreReceipt();
            throw;
        }
        catch
        {
            RestoreReceipt();
            throw;
        }
    }
}
