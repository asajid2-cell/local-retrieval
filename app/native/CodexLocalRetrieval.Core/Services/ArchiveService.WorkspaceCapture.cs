using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Services;

public sealed partial class ArchiveService
{
    private const string WorkspaceCaptureOperation = "workspacecapture";

    public async Task<(bool Ok, string Detail, string ResultId)> ExecuteWorkspaceCaptureAsync(
        string intentId,
        string collectionId,
        string expectedCollectionRevision,
        IReadOnlyList<WorkspaceCaptureTab> requestedTabs,
        IReadOnlyList<WorkspaceCaptureTab> authoritativeTabs)
    {
        intentId = (intentId ?? "").Trim();
        collectionId = (collectionId ?? "").Trim();
        expectedCollectionRevision = (expectedCollectionRevision ?? "").Trim();
        requestedTabs ??= Array.Empty<WorkspaceCaptureTab>();
        authoritativeTabs ??= Array.Empty<WorkspaceCaptureTab>();

        if (intentId.Length == 0 || collectionId.Length == 0 || expectedCollectionRevision.Length == 0)
            return (false, "intentId, collectionId, and expectedCollectionRevision are required", "");

        var fingerprint = WorkspaceCaptureFingerprint(collectionId, expectedCollectionRevision, requestedTabs);
        if (Store.ManagementOperations.TryGetValue(intentId, out var prior))
        {
            if (!string.Equals(prior.Type, WorkspaceCaptureOperation, StringComparison.Ordinal)
                || !string.Equals(prior.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                return (false, "intent collision", "");
            if (prior.State == "applied")
            {
                await LoadStoreStateAsync();
                if (Store.ManagementOperations.TryGetValue(intentId, out var receipt)
                    && receipt.State == "applied"
                    && receipt.PayloadFingerprint == fingerprint)
                    return (true, "already applied", receipt.ResultId);
                return (false, "intent receipt is no longer authoritative", "");
            }
        }

        var validation = ValidateWorkspaceCapture(collectionId, expectedCollectionRevision, requestedTabs, authoritativeTabs);
        if (!validation.Ok) return (false, validation.Detail, "");

        var containerSnapshot = CaptureContainerState();
        var addedPlaceholderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var collection = Store.Collections[collectionId];
            foreach (var tab in validation.Tabs)
            {
                string sessionId;
                if (tab.ShellOnly && string.IsNullOrWhiteSpace(tab.SessionId))
                {
                    var placeholderId = "muxtab:" + tab.Name.ToLowerInvariant();
                    var existed = Store.Sessions.ContainsKey(placeholderId);
                    var placeholder = EnsureMuxTabPlaceholder(tab.Name, tab.Tool);
                    sessionId = placeholder.Id;
                    if (!existed) addedPlaceholderIds.Add(sessionId);
                }
                else
                {
                    var session = ResolveSessionByIdOrAlias(tab.SessionId, tab.Tool);
                    if (session is null) throw new InvalidOperationException("authoritative session disappeared");
                    sessionId = session.Id;
                }
                if (!collection.SessionIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
                    collection.SessionIds.Add(sessionId);
            }

            var ledger = Store.ManagementOperations.TryGetValue(intentId, out var existing)
                ? existing
                : new ManagementOperation { Type = WorkspaceCaptureOperation, PayloadFingerprint = fingerprint, State = "prepared" };
            ledger.Type = WorkspaceCaptureOperation;
            ledger.PayloadFingerprint = fingerprint;
            ledger.ResultId = collectionId;
            ledger.State = "applied";
            ledger.UpdatedAt = DateTime.UtcNow.ToString("O");
            Store.ManagementOperations[intentId] = ledger;

            await SaveAsync();
            return (true, "workspace capture applied", collectionId);
        }
        catch (StoreGenerationConflictException)
        {
            RestoreContainerState(containerSnapshot);
            foreach (var id in addedPlaceholderIds) Store.Sessions.Remove(id);
            await LoadStoreStateAsync();
            return (false, "store changed; reload and reconcile intent", "");
        }
        catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
        {
            await LoadStoreStateAsync();
            if (Store.ManagementOperations.TryGetValue(intentId, out var receipt)
                && receipt.State == "applied" && receipt.PayloadFingerprint == fingerprint)
                return (true, "workspace capture committed; receipt verified", receipt.ResultId);
            return (false, "workspace capture outcome uncertain; reload and reconcile intent", "");
        }
        catch (DurableWriteException)
        {
            RestoreContainerState(containerSnapshot);
            foreach (var id in addedPlaceholderIds) Store.Sessions.Remove(id);
            return (false, "workspace capture rolled back before commit", "");
        }
        catch (IOException error)
        {
            RestoreContainerState(containerSnapshot);
            foreach (var id in addedPlaceholderIds) Store.Sessions.Remove(id);
            return (false, "workspace capture rolled back before commit: " + error.Message, "");
        }
        catch
        {
            RestoreContainerState(containerSnapshot);
            foreach (var id in addedPlaceholderIds) Store.Sessions.Remove(id);
            throw;
        }
    }

    private (bool Ok, string Detail, List<WorkspaceCaptureTab> Tabs) ValidateWorkspaceCapture(
        string collectionId, string expectedRevision,
        IReadOnlyList<WorkspaceCaptureTab> requested, IReadOnlyList<WorkspaceCaptureTab> authoritative)
    {
        if (!Store.Collections.ContainsKey(collectionId)
            || !string.Equals(CollectionMembershipRevision(collectionId), expectedRevision, StringComparison.Ordinal))
            return (false, "collection not found or revision is stale", new());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in requested)
        {
            var name = (tab?.Name ?? "").Trim();
            if (!IsSafeWorkspaceTabName(name) || !seen.Add(name))
                return (false, "requested tabs must have unique nonempty safe names", new());
            if (string.IsNullOrWhiteSpace(tab.GenerationId))
                return (false, "generation is required", new());
            if (tab.IdentityPending)
                return (false, "identity-pending tabs are not capturable", new());

            var matches = authoritative.Where(a => string.Equals(a.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.GenerationId, tab.GenerationId, StringComparison.Ordinal)
                && string.Equals(a.SessionId ?? "", tab.SessionId ?? "", StringComparison.Ordinal)
                && string.Equals(a.Tool ?? "", tab.Tool ?? "", StringComparison.OrdinalIgnoreCase)
                && a.Alive && !a.IdentityPending).ToList();
            if (matches.Count != 1)
                return (false, "requested tab is not an exact authoritative live tab", new());
            if (string.IsNullOrWhiteSpace(tab.SessionId) && (!tab.ShellOnly || !matches[0].ShellOnly))
                return (false, "blank session id is allowed only for an authoritative shell-only tab", new());
            if (!tab.ShellOnly && string.IsNullOrWhiteSpace(tab.SessionId))
                return (false, "session id is required for real chats", new());
            if (!tab.ShellOnly && ResolveSessionByIdOrAlias(tab.SessionId, tab.Tool) is null)
                return (false, "session id is unknown", new());
        }
        return (true, "", requested.Select(t => t with { Name = t.Name.Trim() }).ToList());
    }

    private static bool IsSafeWorkspaceTabName(string name) => name.Length > 0
        && name.Length <= 200
        && name.All(c => !char.IsControl(c) && c is not '/' and not '\\' and not ':' and not '"');

    private static string WorkspaceCaptureFingerprint(string collectionId, string revision, IReadOnlyList<WorkspaceCaptureTab> tabs)
    {
        var tuples = tabs.Select(t => new { name = (t?.Name ?? "").Trim(), t.GenerationId, t.SessionId, t.Tool, t.Alive, t.IdentityPending, t.ShellOnly }).ToList();
        var payload = JsonSerializer.Serialize(new { collectionId, revision, tabs = tuples });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
