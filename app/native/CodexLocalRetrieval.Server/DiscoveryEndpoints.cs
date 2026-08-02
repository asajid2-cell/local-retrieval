using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Server;

// The web's archive browser talks to THIS server, not to the relay: the PC owns the chats, so the
// filtering runs where the data already lives (the same ChatFilter/FilterChats the desktop app uses)
// and only one page of rows crosses the tunnel. There is deliberately no second index on the VPS.
//
// The query names are a frozen contract shared with relay/public/chats.js — renaming one silently
// breaks the page, so they are read explicitly here instead of by model binding.
public static class DiscoveryEndpoints
{
    public static void MapDiscovery(this WebApplication app, ArchiveRuntime runtime)
    {
        // The archive loads lazily and is evicted when idle, so the api is built per request around
        // whichever ArchiveService instance the runtime hands over.
        app.MapGet("/api/discovery/chats", async (HttpRequest req, CancellationToken ct) =>
        {
            var query = ReadQuery(req);
            var page = await runtime.UseAsync(
                (archive, _) => Task.FromResult(new DiscoveryApi(archive).Chats(query)), ct);
            return Results.Json(page);
        });

        app.MapGet("/api/discovery/facets", async (HttpRequest req, CancellationToken ct) =>
        {
            var query = ReadQuery(req);
            var facets = await runtime.UseAsync(
                (archive, _) => Task.FromResult(new DiscoveryApi(archive).Facets(query)), ct);
            return Results.Json(facets);
        });
    }

    private static DiscoveryQuery ReadQuery(HttpRequest req)
    {
        string? S(string name) => req.Query.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : null;
        int? I(string name) => int.TryParse(S(name), out var n) ? n : null;
        // Absent stays null so the api applies its own default (hidden chats stay hidden); only an
        // explicit value overrides it.
        bool? B(string name) => S(name) switch
        {
            "1" or "true" or "True" => true,
            "0" or "false" or "False" => false,
            _ => null,
        };
        return new DiscoveryQuery(
            Q: S("q"),
            Include: S("include"),
            Exclude: S("exclude"),
            Match: S("match"),
            Agent: S("agent"),
            Date: S("date"),
            MinUserMessages: I("minUserMessages"),
            ShowHidden: B("showHidden"),
            Project: S("project"),
            Sort: S("sort"),
            Offset: I("offset"),
            Limit: I("limit"));
    }
}
