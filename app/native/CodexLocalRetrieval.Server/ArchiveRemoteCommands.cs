using System.Text.Json;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Server;

public static class ArchiveRemoteCommands
{
    public static async Task<(bool ok, string detail)> ExecuteAsync(ArchiveService archive, JsonElement command)
    {
        if (command.ValueKind != JsonValueKind.Object) return (false, "archive command object required");
        if (!command.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
            return (false, "command type required");
        var type = typeValue.GetString() ?? "";
        if (type == "rename")
        {
            var outcome = await archive.RenameNativeRemoteAsync(StringProperty(command, "tool") ?? "",
                StringProperty(command, "sessionId") ?? "", StringProperty(command, "title") ?? "",
                StringProperty(command, "intentId") ?? "", StringProperty(command, "expectedRevision") ?? "");
            if (outcome.Uncertain)
                throw new CodexLocalRetrieval.Core.Remote.RemoteCommandUnconfirmedException(outcome.Detail);
            return (outcome.Ok, outcome.Detail);
        }
        if (type is "checkpointcreate" or "checkpointrename" or "checkpointdelete" or "checkpointspawn" or "branchcreate")
        {
            var intent = StringProperty(command, "intentId");
            if (string.IsNullOrWhiteSpace(intent)) return (false, "intentId required");
            var expectedRevision = StringProperty(command, "expectedRevision");
            if (string.IsNullOrWhiteSpace(expectedRevision)) return (false, "expectedRevision required");
            var source = type is "checkpointcreate" or "branchcreate";
            var objectId = source ? StringProperty(command, "sessionId") : StringProperty(command, "snapshotId");
            if (string.IsNullOrWhiteSpace(objectId)) return (false, source ? "sessionId required" : "snapshotId required");
            var branchTool = OptionalString(command, "tool", out var branchToolError);
            if (branchToolError is not null) return (false, branchToolError);
            if (source && branchTool is not ("claude" or "codex")) return (false, "tool must be claude or codex");
            if (!source && branchTool is not null && branchTool.Length > 0 && branchTool is not ("claude" or "codex")) return (false, "tool must be claude or codex");
            var name = StringProperty(command, "name");
            if (type is "checkpointcreate" or "checkpointrename")
                if (string.IsNullOrWhiteSpace(name)) return (false, "name required");
            var outcome = await archive.ExecuteBranchOperationAsync(type, intent, objectId, branchTool ?? "", expectedRevision, name);
            return (outcome.Ok, outcome.Ok ? outcome.ResultId : outcome.Detail);
        }
        if (type is "deckcreate" or "collectioncreate" or "deckrename" or "collectionrename" or "collectionmove" or "deckdelete" or "collectiondelete" or "collectionrecover" or "collectionpurge" or "collectionempty" or "collectionsettag" or "collectionreorder" or "deckreorder")
        {
            var intent = StringProperty(command, "intentId");
            if (string.IsNullOrWhiteSpace(intent)) return (false, "intentId required");
            var objectId = type.Contains("deck", StringComparison.OrdinalIgnoreCase)
                ? StringProperty(command, "deckId")
                : StringProperty(command, "collectionId");
            if (type == "collectionempty") objectId = null;
            var containerExpected = type switch
            {
                "collectionpurge" => StringProperty(command, "expectedDeletedRevision"),
                "collectionempty" => StringProperty(command, "expectedRecentlyDeletedRevision"),
                _ => StringProperty(command, "expectedRevision") ?? StringProperty(command, "expectedDeletedRevision")
            };
            if (type is "collectionpurge" or "collectionempty" && string.IsNullOrWhiteSpace(containerExpected)) return (false, "semantic expected revision required");
            var name = StringProperty(command, "name");
            var deckId = StringProperty(command, "targetDeckId");
            if (type == "collectioncreate" && string.IsNullOrEmpty(deckId)) deckId = StringProperty(command, "deckId");
            string? tag = null; bool? enabled = null; IReadOnlyList<string>? sessionIds = null;
            if (type == "collectionsettag") {
                if (!StringProperty(command, "tag", out var rawTag) || string.IsNullOrWhiteSpace(rawTag)) return (false, "tag must be a non-empty string");
                if (!BooleanProperty(command, "enabled", out var rawEnabled)) return (false, "enabled must be an explicit boolean");
                tag = ArchiveService.NormalizeTag(rawTag); enabled = rawEnabled;
                if (tag.Length == 0 || ArchiveService.IsReservedTag(tag)) return (false, "tag is empty or reserved");
            }
            if (type is "collectionreorder" or "deckreorder") {
                var idsProperty = type == "deckreorder" ? "deckIds" : "sessionIds";
                if (!command.TryGetProperty(idsProperty, out var ids) || ids.ValueKind != JsonValueKind.Array || ids.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) return (false, idsProperty + " must be an array of strings");
                sessionIds = ids.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            }
            var outcome = await archive.ExecuteContainerOperationAsync(type, intent, command.GetRawText(), containerExpected, objectId, name, deckId, tag, enabled, sessionIds);
            return (outcome.Ok, outcome.Ok ? outcome.ResultId : outcome.Detail);
        }
        if (type is not ("setapptitle" or "setfavorite" or "archive" or "setphrases" or "settag" or "addtocollection" or "removefromcollection" or "deckcreate" or "collectioncreate" or "deckrename" or "collectionrename" or "collectionmove" or "deckdelete" or "collectiondelete" or "collectionrecover" or "collectionpurge" or "collectionempty" or "collectionsettag" or "collectionreorder" or "deckreorder"))
            return (false, "unsupported archive command");
        if (type is "addtocollection" or "removefromcollection")
        {
            var collectionId = StringProperty(command, "collectionId");
            var expectedCollectionRevision = StringProperty(command, "expectedCollectionRevision");
            if (string.IsNullOrEmpty(collectionId)) return (false, "collectionId required");
            if (string.IsNullOrEmpty(expectedCollectionRevision)) return (false, "expectedCollectionRevision required");
            var memberId = StringProperty(command, "sessionId");
            if (string.IsNullOrEmpty(memberId)) return (false, "sessionId required");
            var member = archive.ResolveSessionByIdOrAlias(memberId, OptionalString(command, "tool", out var memberToolError));
            if (memberToolError is not null) return (false, memberToolError);
            if (member is null) return (false, "chat not found in the authoritative archive");
            var okMembership = await archive.ExecuteRemoteCollectionMembershipAsync(collectionId, member.Id, type == "addtocollection", expectedCollectionRevision);
            return (okMembership, okMembership ? "collection membership updated" : "collection changed; refresh and retry");
        }
        var id = StringProperty(command, "sessionId");
        if (id is null || id.Length == 0) return (false, "sessionId required");
        var tool = OptionalString(command, "tool", out var toolError);
        if (toolError is not null) return (false, toolError);
        var session = archive.ResolveSessionByIdOrAlias(id, tool);
        if (session is null) return (false, "chat not found in the authoritative archive");
        var expected = OptionalString(command, "expectedRevision", out var revisionError);
        if (revisionError is not null) return (false, revisionError);
        if (type != "setfavorite" && string.IsNullOrEmpty(expected)) return (false, "expectedRevision required");
        bool ok;
        switch (type)
        {
            case "setfavorite": if (!BooleanProperty(command, "favorite", out var favorite)) return (false, "favorite must be an explicit boolean"); ok = await archive.SetFavoriteRemoteAsync(session.Id, favorite, expected ?? ""); break;
            case "archive": if (!BooleanProperty(command, "archived", out var archived)) return (false, "archived must be an explicit boolean"); ok = await archive.SetArchivedRemoteAsync(session.Id, tool, archived, expected!); break;
            case "setapptitle": if (!StringProperty(command, "title", out var title)) return (false, "title must be a string"); ok = await archive.SetAppTitleRemoteAsync(session.Id, tool, title, expected!); break;
            case "setphrases": if (!command.TryGetProperty("phrases", out var phrases) || phrases.ValueKind != JsonValueKind.Array || phrases.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String)) return (false, "phrases must be an array of strings"); ok = await archive.SetPhrasesRemoteAsync(session.Id, tool, phrases.EnumerateArray().Select(p => p.GetString() ?? ""), expected!); break;
            default: if (!StringProperty(command, "tag", out var tag) || string.IsNullOrWhiteSpace(tag)) return (false, "tag must be a non-empty string"); if (!BooleanProperty(command, "enabled", out var enabled)) return (false, "enabled must be an explicit boolean"); var normalized = ArchiveService.NormalizeTag(tag); if (normalized.Length == 0 || ArchiveService.IsReservedTag(normalized)) return (false, "tag is empty or reserved"); ok = await archive.SetTagRemoteAsync(session.Id, tool, normalized, enabled, expected!); break;
        }
        return (ok, ok ? type == "setfavorite" && string.IsNullOrEmpty(expected) ? "favorite already set" : "metadata updated" : "chat metadata changed; refresh and retry");
    }
    public static async Task<(bool ok, string detail)> CaptureWorkspaceAsync(ArchiveService archive, JsonElement command, string listing)
    {
        try
        {
            if (!command.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array) return (false, "workspace tabs required");
            using var live = JsonDocument.Parse(listing);
            if (!live.RootElement.TryGetProperty("list", out var rows) || rows.ValueKind != JsonValueKind.Array) return (false, "authoritative tab listing unavailable");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var requested = tabs.Deserialize<List<CodexLocalRetrieval.Core.Models.WorkspaceCaptureTab>>(options);
            var authoritative = rows.Deserialize<List<CodexLocalRetrieval.Core.Models.WorkspaceCaptureTab>>(options);
            if (requested is null || authoritative is null || requested.Any(x => x is null) || authoritative.Any(x => x is null)) return (false, "invalid tab listing");
            var outcome = await archive.ExecuteWorkspaceCaptureAsync(StringProperty(command, "intentId") ?? "", StringProperty(command, "collectionId") ?? "", StringProperty(command, "expectedCollectionRevision") ?? "", requested, authoritative);
            return (outcome.Ok, outcome.Detail);
        }
        catch (JsonException) { return (false, "invalid workspace capture payload"); }
    }

    private static string? StringProperty(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool StringProperty(JsonElement o, string name, out string value) { value = StringProperty(o, name) ?? ""; return o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String; }
    private static string? OptionalString(JsonElement o, string name, out string? error) { error = null; if (!o.TryGetProperty(name, out var v)) return null; if (v.ValueKind != JsonValueKind.String) { error = name + " must be a string"; return null; } return v.GetString() ?? ""; }
    private static bool BooleanProperty(JsonElement o, string name, out bool value) { value = false; if (!o.TryGetProperty(name, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; value = v.GetBoolean(); return true; }
}
