using CodexLocalRetrieval.Core.Chat;
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
    public static void MapDiscovery(this WebApplication app, ArchiveRuntime runtime, bool redactReads = false)
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

        // The full-transcript scan the desktop app runs on Enter. Kept on its own route because it is the
        // expensive one: the browser only asks when the user actually submits a phrase, so typing can never
        // start a file scan. Read-only, like everything else under /api/discovery.
        app.MapGet("/api/discovery/search", async (HttpRequest req, CancellationToken ct) =>
        {
            var q = req.Query.TryGetValue("q", out var rawQuery) ? rawQuery.ToString() : null;
            var limit = int.TryParse(req.Query["limit"], out var parsed) ? parsed : (int?)null;
            var showHidden = ReadQuery(req).ShowHidden == true;
            var page = await runtime.UseAsync(
                (archive, _) => new DiscoveryApi(archive).DeepSearchAsync(q, limit, showHidden), ct);
            return Results.Json(page);
        });

        // Pickers for the web's Start-chat dialog. Read-only like the rest of discovery: they hand
        // out ids and labels, never a path or a command line, because the choice travels back
        // through the relay's command queue where the browser can read it.
        app.MapGet("/api/discovery/start/decks", async (CancellationToken ct) =>
            Results.Json(await runtime.UseAsync((a, _) => Task.FromResult(new DiscoveryApi(a).StartDecks()), ct)));

        app.MapGet("/api/discovery/start/collections", async (string? deckId, CancellationToken ct) =>
            Results.Json(await runtime.UseAsync((a, _) => Task.FromResult(new DiscoveryApi(a).StartCollections(deckId ?? "")), ct)));

        app.MapGet("/api/discovery/admin/containers", async (CancellationToken ct) =>
            Results.Json(await runtime.UseAsync((a, _) => Task.FromResult(new DiscoveryApi(a).ContainerAdmin()), ct)));

        app.MapGet("/api/discovery/start/checkpoints", async (CancellationToken ct) =>
            Results.Json(await runtime.UseAsync((a, _) => Task.FromResult(new DiscoveryApi(a).StartCheckpoints()), ct)));

        app.MapPost("/api/discovery/copy", async (HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var request = await context.Request.ReadFromJsonAsync<CopyRequest>(ct);
            if (request is null || string.IsNullOrWhiteSpace(request.SessionId)
                || request.Tool is not ("claude" or "codex")
                || request.Mode is not ("resume" or "command" or "path" or "paths" or "code")
                || request.LaunchMode is not (null or "native" or "gateway"))
                return Results.BadRequest(new { error = "exact chat, tool and supported copy mode required" });
            return await runtime.UseAsync<IResult>(async (archive, cancellation) =>
            {
                var session = archive.ResolveSessionByIdOrAlias(request.SessionId, request.Tool);
                if (session is null || session.Id != request.SessionId)
                    return Results.NotFound(new { error = "exact chat not found" });
                if (request.LaunchMode == "gateway" && (request.Mode != "command" || !ArchiveService.CanResumeThroughGateway(session.Tool)))
                    return Results.BadRequest(new { error = "Gateway resume command unavailable for this chat" });
                if (request.Mode == "command")
                {
                    var launch = archive.BuildResumeLaunch(session, launchModeOverride: request.LaunchMode);
                    if (string.IsNullOrWhiteSpace(launch.Exe) || string.IsNullOrWhiteSpace(launch.Arguments))
                        return Results.BadRequest(new { error = "resume command unavailable for this chat" });
                }
                var payload = await archive.CopyPayloadAsync(session, request.Mode, cancellation, request.LaunchMode);
                if (redactReads) payload = SecretRedactor.Scrub(payload);
                if (System.Text.Encoding.UTF8.GetByteCount(payload) > 1024 * 1024)
                    return Results.BadRequest(new { error = "copy payload exceeds 1 MiB; use the desktop export" });
                return Results.Json(new { sessionId = session.Id, tool = session.Tool, mode = request.Mode, payload });
            }, ct);
        });

        app.MapGet("/api/discovery/start/workspaces", async (CancellationToken ct) =>
            Results.Json(await runtime.UseAsync((a, _) => Task.FromResult(new DiscoveryApi(a).StartWorkspaces()), ct)));
    }

    private sealed record CopyRequest(string SessionId, string Tool, string Mode, string? LaunchMode);

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
            Archived: S("archived"),
            Project: S("project"),
            Sort: S("sort"),
            Offset: I("offset"),
            Limit: I("limit"));
    }
}
