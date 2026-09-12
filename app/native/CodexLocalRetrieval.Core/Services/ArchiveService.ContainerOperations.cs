using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Services;

public sealed partial class ArchiveService
{
    private static readonly HashSet<string> ContainerOperationTypes = new(StringComparer.Ordinal)
    {
        "deckcreate", "collectioncreate", "deckrename", "collectionrename", "collectionmove",
        "deckdelete", "collectiondelete", "collectionrecover", "collectionpurge", "collectionempty",
        "collectionsettag", "collectionreorder", "deckreorder"
    };

    public string DeckOrderRevision()
        => Revision(JsonSerializer.Serialize(Store.Decks.Select(d => d.Id).ToList()));

    public string DeckManagementRevision(string deckId)
    {
        var d = Store.Decks.FirstOrDefault(x => x.Id.Equals(deckId, StringComparison.OrdinalIgnoreCase));
        return d is null ? "" : Revision(JsonSerializer.Serialize(new { d.Id, d.Name, d.CreatedAt, active = ActiveDeckId }));
    }

    public string CollectionManagementRevision(string collectionId)
    {
        if (!Store.Collections.TryGetValue(collectionId, out var c)) return "";
        return Revision(JsonSerializer.Serialize(new { c.Id, c.Name, c.DeckId, c.Color, c.Tags, c.SessionIds }));
    }

    public string DeletedCollectionManagementRevision(string collectionId)
    {
        var d = Store.DeletedCollections.FirstOrDefault(x => x.Collection.Id.Equals(collectionId, StringComparison.OrdinalIgnoreCase));
        return d is null ? "" : Revision(JsonSerializer.Serialize(new { d.Collection.Id, d.Collection.Name, d.Collection.DeckId, d.Collection.Color, d.Collection.Tags, d.Collection.SessionIds, d.DeletedAt }));
    }

    public string RecentlyDeletedManagementRevision()
        => Revision(JsonSerializer.Serialize(Store.DeletedCollections.Select(d => new { d.Collection.Id, d.Collection.Name, d.Collection.DeckId, d.Collection.Color, d.Collection.Tags, d.Collection.SessionIds, d.DeletedAt }).ToList()));

    public bool HasDeletedCollection(string id) => Store.DeletedCollections.Any(d => d.Collection.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> DeletedCollectionIds() => Store.DeletedCollections.Select(d => d.Collection.Id).ToList();

    public void RemoveDeletedCollection(string id) => Store.DeletedCollections.RemoveAll(d => d.Collection.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void ClearDeletedCollections() => Store.DeletedCollections.Clear();

    public string ManagementOperationRevision(string type, string? id, string? expected)
        => Revision(JsonSerializer.Serialize(new { type, id, expected }));

    private string RequireRecentlyDeleted(string? expected)
        => !string.IsNullOrWhiteSpace(expected) && RecentlyDeletedManagementRevision().Equals(expected, StringComparison.Ordinal) ? expected! : "";

    private string RequireDeletedExact(string? id, string? expected)
        => id is not null && HasDeletedCollection(id) && DeletedCollectionManagementRevision(id).Equals(expected ?? "", StringComparison.Ordinal) ? id : "";

    public async Task<(bool Ok, string Detail, string ResultId)> ExecuteContainerOperationAsync(
        string type, string intentId, string payload, string? expectedRevision,
        string? objectId = null, string? name = null, string? deckId = null,
        string? tag = null, bool? enabled = null, IReadOnlyList<string>? sessionIds = null)
    {
        type = (type ?? "").Trim().ToLowerInvariant();
        intentId = (intentId ?? "").Trim();
        if (intentId.Length == 0 || !ContainerOperationTypes.Contains(type))
            return (false, "unsupported container operation or intentId required", "");

        // Only semantic inputs participate. Transport metadata in the command JSON is deliberately ignored.
        var normalizedTag = NormalizeTag(tag);
        var orderedSessionIds = sessionIds?.ToList();
        var fingerprint = Revision(JsonSerializer.Serialize(new { type, objectId, name = NormalizeContainerName(name), deckId, expectedRevision, semanticExpectedRevision = expectedRevision, tag = normalizedTag, enabled, sessionIds = orderedSessionIds }));
        if (Store.ManagementOperations.TryGetValue(intentId, out var prior))
        {
            if (!StringEquals(prior.Type, type) || prior.PayloadFingerprint != fingerprint)
                return (false, "intent collision", "");
            if (prior.State == "applied")
            {
                // A receipt is durable even when the object was subsequently deleted. Reload first so a
                // stale in-memory receipt cannot claim success for a different authoritative generation.
                await LoadStoreStateAsync();
                if (Store.ManagementOperations.TryGetValue(intentId, out var authoritative)
                    && authoritative.State == "applied"
                    && authoritative.PayloadFingerprint == fingerprint)
                    return (true, "already applied", authoritative.ResultId);
                return (false, "intent receipt is no longer authoritative", "");
            }
        }

        // All validation precedes the first mutation, including strict destination resolution.
        var validation = Validate(type, objectId, name, deckId, expectedRevision, normalizedTag, enabled, orderedSessionIds);
        if (!validation.Ok) return (false, validation.Detail, "");

        var snapshot = CaptureContainerState();
        try
        {
            var ledger = Store.ManagementOperations.TryGetValue(intentId, out var existing)
                ? existing
                : new ManagementOperation { Type = type, PayloadFingerprint = fingerprint, State = "prepared" };
            ledger.Type = type;
            ledger.PayloadFingerprint = fingerprint;
            Store.ManagementOperations[intentId] = ledger;

            var result = Apply(type, objectId, name, validation.DeckId, validation.ResultId, ledger, normalizedTag, enabled, orderedSessionIds);
            ledger.ResultId = result;
            ledger.State = "applied";
            ledger.UpdatedAt = DateTime.UtcNow.ToString("O");
            await SaveAsync();
            return (true, "container operation applied", result);
        }
        catch (StoreGenerationConflictException)
        {
            RestoreContainerState(snapshot);
            await LoadStoreStateAsync();
            return (false, "store changed; reload and reconcile intent", "");
        }
        catch (DurableWriteException error) when (!error.Committed && !error.VerificationUnknown)
        {
            RestoreContainerState(snapshot);
            return (false, "container operation rolled back before commit", "");
        }
        catch (DurableWriteException error) when (error.Committed || error.VerificationUnknown)
        {
            // The bytes may have committed; never report a false rollback. Reconcile from disk and inspect
            // the durable receipt, which is the only truthful answer after an ambiguous write.
            await LoadStoreStateAsync();
            if (Store.ManagementOperations.TryGetValue(intentId, out var receipt)
                && receipt.State == "applied" && receipt.PayloadFingerprint == fingerprint)
                return (true, "container operation committed; receipt verified", receipt.ResultId);
            return (false, "container operation outcome uncertain; reload and reconcile intent", "");
        }
        catch (IOException error)
        {
            RestoreContainerState(snapshot);
            return (false, "container operation rolled back before commit: " + error.Message, "");
        }
        catch
        {
            RestoreContainerState(snapshot);
            throw;
        }
    }

    private (bool Ok, string Detail, string? DeckId, string ResultId) Validate(
        string type, string? objectId, string? name, string? deckRef, string? expected,
        string normalizedTag, bool? enabled, IReadOnlyList<string>? sessionIds)
    {
        var normalizedName = NormalizeContainerName(name);
        switch (type)
        {
            case "deckreorder":
                if (string.IsNullOrWhiteSpace(expected) || DeckOrderRevision() != expected)
                    return (false, "deck order revision is stale", null, "");
                var deckIds = Store.Decks.Select(d => d.Id).ToList();
                if (sessionIds is null || sessionIds.Count != deckIds.Count
                    || sessionIds.Distinct(StringComparer.Ordinal).Count() != sessionIds.Count
                    || !new HashSet<string>(deckIds, StringComparer.Ordinal).SetEquals(sessionIds))
                    return (false, "deckIds must be an exact permutation of decks", null, "");
                return (true, "", null, "decks");
            case "deckcreate":
                return normalizedName.Length == 0 ? (false, "name required", null, "") : (true, "", null, "");
            case "collectioncreate":
                if (normalizedName.Length == 0) return (false, "name required", null, "");
                var createDeck = StrictDeckId(deckRef);
                return createDeck is null ? (false, "destination deck not found", null, "") : (true, "", createDeck, "");
            case "deckrename":
            case "deckdelete":
                var deck = RequireDeck(objectId, expected);
                if (deck.Length == 0) return (false, "deck not found or revision is stale", null, "");
                if (type == "deckdelete" && deck.Equals(MainDeckId, StringComparison.OrdinalIgnoreCase)) return (false, "Main cannot be deleted", null, "");
                if (type == "deckrename" && normalizedName.Length == 0) return (false, "name required", null, "");
                return (true, "", null, deck);
            case "collectionrename":
            case "collectiondelete":
                var collection = RequireCollection(objectId, expected);
                if (collection.Length == 0) return (false, "collection not found or revision is stale", null, "");
                if (type == "collectionrename" && normalizedName.Length == 0) return (false, "name required", null, "");
                return (true, "", null, collection);
            case "collectionmove":
                var moving = RequireCollection(objectId, expected);
                var destination = StrictDeckId(deckRef);
                return moving.Length == 0 ? (false, "collection not found or revision is stale", null, "")
                    : destination is null ? (false, "destination deck not found", null, "")
                    : (true, "", destination, moving);
            case "collectionrecover":
                var deleted = RequireDeleted(objectId, expected);
                if (deleted.Length == 0) return (false, "deleted collection not found or revision is stale", null, "");
                if (Store.Collections.ContainsKey(deleted)) return (false, "restore refused: active collection id already exists", null, "");
                return (true, "", null, deleted);
            case "collectionpurge":
                var purge = RequireDeletedExact(objectId, expected);
                return purge.Length == 0 ? (false, "deleted collection not found or revision is stale", null, "") : (true, "", null, purge);
            case "collectionempty":
                return RequireRecentlyDeleted(expected).Length == 0
                    ? (false, "recently deleted set is stale", null, "")
                    : (true, "", null, "cleared");
            case "collectionsettag":
                var tagged = RequireCollection(objectId, expected);
                if (tagged.Length == 0) return (false, "collection not found or revision is stale", null, "");
                if (normalizedTag.Length == 0 || IsReservedTag(normalizedTag)) return (false, "tag is empty or reserved", null, "");
                if (enabled is null) return (false, "enabled must be explicit", null, "");
                return (true, "", null, tagged);
            case "collectionreorder":
                var reordered = RequireCollection(objectId, expected);
                if (reordered.Length == 0) return (false, "collection not found or revision is stale", null, "");
                if (sessionIds is null) return (false, "sessionIds must be an array", null, "");
                var current = Store.Collections[reordered].SessionIds;
                if (sessionIds.Count != current.Count || sessionIds.Count != sessionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    || !new HashSet<string>(current, StringComparer.OrdinalIgnoreCase).SetEquals(sessionIds))
                    return (false, "sessionIds must be an exact permutation of collection membership", null, "");
                return (true, "", null, reordered);
            default: return (false, "unsupported container operation", null, "");
        }
    }

    private string Apply(string type, string? objectId, string? name, string? deckId, string validatedId, ManagementOperation ledger, string normalizedTag, bool? enabled, IReadOnlyList<string>? sessionIds)
    {
        var normalizedName = NormalizeContainerName(name);
        switch (type)
        {
            case "deckreorder":
                var decksById = Store.Decks.ToDictionary(d => d.Id, StringComparer.Ordinal);
                Store.Decks = sessionIds!.Select(id => decksById[id]).ToList();
                return "decks";
            case "deckcreate":
                var deckIdNew = ledger.ResultId.Length == 0 ? UniqueDeckId(normalizedName) : ledger.ResultId;
                if (!Store.Decks.Any(d => d.Id.Equals(deckIdNew, StringComparison.OrdinalIgnoreCase)))
                    Store.Decks.Add(new Deck { Id = deckIdNew, Name = normalizedName, CreatedAt = DateTime.UtcNow.ToString("O") });
                return deckIdNew;
            case "collectioncreate":
                var collectionId = ledger.ResultId.Length == 0 ? DeckCollectionId(normalizedName, deckId!) : ledger.ResultId;
                if (!Store.Collections.ContainsKey(collectionId))
                    Store.Collections[collectionId] = new ArchiveCollection { Id = collectionId, Name = normalizedName, DeckId = deckId!, Color = Store.Settings.AccentHex };
                return collectionId;
            case "deckrename": Store.Decks.First(d => d.Id.Equals(validatedId, StringComparison.OrdinalIgnoreCase)).Name = normalizedName; return validatedId;
            case "collectionrename": Store.Collections[validatedId].Name = normalizedName; return validatedId;
            case "collectionmove": Store.Collections[validatedId].DeckId = deckId!; return validatedId;
            case "deckdelete":
                foreach (var c in Store.Collections.Values.Where(c => CollectionDeck(c).Equals(validatedId, StringComparison.OrdinalIgnoreCase))) c.DeckId = MainDeckId;
                Store.Decks.RemoveAll(d => d.Id.Equals(validatedId, StringComparison.OrdinalIgnoreCase));
                if (ActiveDeckId.Equals(validatedId, StringComparison.OrdinalIgnoreCase)) Store.Settings.ActiveDeckId = MainDeckId;
                return validatedId;
            case "collectiondelete":
                var deleted = Store.Collections[validatedId];
                Store.Collections.Remove(validatedId);
                Store.DeletedCollections.Insert(0, new DeletedCollection { Collection = CloneCollection(deleted), DeletedAt = DateTime.UtcNow.ToString("O") });
                return validatedId;
            case "collectionrecover":
                var entry = Store.DeletedCollections.First(d => d.Collection.Id.Equals(validatedId, StringComparison.OrdinalIgnoreCase));
                Store.DeletedCollections.Remove(entry);
                Store.Collections[validatedId] = CloneCollection(entry.Collection);
                return validatedId;
            case "collectionpurge":
                Store.DeletedCollections.RemoveAll(d => d.Collection.Id.Equals(validatedId, StringComparison.OrdinalIgnoreCase));
                return validatedId;
            case "collectionempty":
                Store.DeletedCollections.Clear();
                return "cleared";
            case "collectionsettag":
                var tagged = Store.Collections[validatedId];
                if (enabled == true) AddTagTo(tagged.Tags, normalizedTag, reserved: false);
                else RemoveTagFrom(tagged.Tags, normalizedTag);
                return validatedId;
            case "collectionreorder":
                Store.Collections[validatedId].SessionIds = sessionIds!.ToList();
                return validatedId;
            default: throw new InvalidOperationException("unsupported container operation");
        }
    }

    private string? StrictDeckId(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return MainDeckId;
        return Store.Decks.FirstOrDefault(d => d.Id.Equals(reference, StringComparison.OrdinalIgnoreCase) || d.Name.Equals(reference, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private string RequireDeck(string? id, string? expected) => id is not null && Store.Decks.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) && DeckManagementRevision(id).Equals(expected ?? "", StringComparison.Ordinal) ? id : "";
    private string RequireCollection(string? id, string? expected) => id is not null && Store.Collections.ContainsKey(id) && CollectionManagementRevision(id).Equals(expected ?? "", StringComparison.Ordinal) ? id : "";
    private string RequireDeleted(string? id, string? expected) => id is not null && Store.DeletedCollections.Any(d => d.Collection.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) && DeletedCollectionManagementRevision(id).Equals(expected ?? "", StringComparison.Ordinal) ? id : "";
    private static bool StringEquals(string a, string b) => a.Equals(b, StringComparison.Ordinal);
    private static string NormalizeContainerName(string? value) => (value ?? "").Trim();
    private static string Revision(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static ArchiveCollection CloneCollection(ArchiveCollection c) => new() { Id = c.Id, Name = c.Name, DeckId = c.DeckId, Color = c.Color, Tags = c.Tags.ToList(), SessionIds = c.SessionIds.ToList() };
    private static DeletedCollection CloneDeleted(DeletedCollection d) => new() { DeletedAt = d.DeletedAt, Collection = CloneCollection(d.Collection) };
    private static Deck CloneDeck(Deck d) => new() { Id = d.Id, Name = d.Name, CreatedAt = d.CreatedAt };
    private static ArchiveSettings CloneSettings(ArchiveSettings s) => JsonSerializer.Deserialize<ArchiveSettings>(JsonSerializer.Serialize(s))!;

    private sealed record ContainerSnapshot(
        List<Deck> Decks,
        Dictionary<string, ArchiveCollection> Collections,
        List<DeletedCollection> DeletedCollections,
        ArchiveSettings Settings,
        Dictionary<string, ManagementOperation> ManagementOperations);

    private ContainerSnapshot CaptureContainerState() => new(
        Store.Decks.Select(CloneDeck).ToList(),
        Store.Collections.ToDictionary(k => k.Key, v => CloneCollection(v.Value), StringComparer.OrdinalIgnoreCase),
        Store.DeletedCollections.Select(CloneDeleted).ToList(),
        CloneSettings(Store.Settings),
        Store.ManagementOperations.ToDictionary(k => k.Key, v => CloneManagementOperation(v.Value), StringComparer.Ordinal));

    private void RestoreContainerState(ContainerSnapshot snapshot)
    {
        Store.Decks = snapshot.Decks.Select(CloneDeck).ToList();
        Store.Collections = snapshot.Collections.ToDictionary(k => k.Key, v => CloneCollection(v.Value), StringComparer.OrdinalIgnoreCase);
        Store.DeletedCollections = snapshot.DeletedCollections.Select(CloneDeleted).ToList();
        Store.Settings = CloneSettings(snapshot.Settings);
        Store.ManagementOperations = snapshot.ManagementOperations.ToDictionary(k => k.Key, v => CloneManagementOperation(v.Value), StringComparer.Ordinal);
    }
}
