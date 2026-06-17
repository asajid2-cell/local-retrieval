using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

// ---- config (env only; nothing secret is ever read from disk or args) ----
var token = Environment.GetEnvironmentVariable("CLR_REMOTE_TOKEN");
if (!RemoteAuth.IsValidConfiguredToken(token))
{
    Console.Error.WriteLine($"refusing to start: set CLR_REMOTE_TOKEN to a secret of at least {RemoteAuth.MinTokenLength} chars.");
    return 1;
}
token = token!.Trim();

var port = int.TryParse(Environment.GetEnvironmentVariable("CLR_REMOTE_PORT"), out var p) ? p : 8765;
var bind = Environment.GetEnvironmentVariable("CLR_REMOTE_BIND") ?? "127.0.0.1"; // localhost only; nginx is the edge
var redactReads = Environment.GetEnvironmentVariable("CLR_REMOTE_REDACT_READS") == "1";
var allowLaunch = Environment.GetEnvironmentVariable("CLR_REMOTE_ALLOW_LAUNCH") == "1";

// ---- archive: load the store and index the live session folders so remote browsing is current ----
// CLR_REMOTE_STORE: point at a specific app-store.json (e.g. a synced copy). CLR_REMOTE_BUNDLED=1
// uses the repo's sanitized demo store (handy for a smoke test without touching real chats).
var storePath = Environment.GetEnvironmentVariable("CLR_REMOTE_STORE");
var useBundled = Environment.GetEnvironmentVariable("CLR_REMOTE_BUNDLED") == "1";
var archive = new ArchiveService(storePath: string.IsNullOrWhiteSpace(storePath) ? null : storePath, useBundledStore: useBundled);
await archive.LoadAsync();
if (Environment.GetEnvironmentVariable("CLR_REMOTE_SYNC") != "0")
{
    try { await archive.SyncFromDiskAsync(); } catch (Exception ex) { Console.Error.WriteLine("sync warning: " + ex.Message); }
}

IChatBackend? BackendFactory()
{
    var provider = archive.ActiveAiProvider()
                   ?? archive.EnsureAiProvider("DeepSeek", "https://api.deepseek.com", "deepseek-v4-flash");
    var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
              ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    return string.IsNullOrWhiteSpace(key) ? null : new DeepSeekBackend(provider.BaseUrl, provider.Model, key);
}

var api = new RemoteApi(archive, BackendFactory, redactReads, allowLaunch);

// Resolve wwwroot robustly: next to the binary when published, else the project source when running
// from bin during development. Content root = binary dir so this works regardless of launch cwd.
var contentRoot = AppContext.BaseDirectory;
var webRoot = Path.Combine(contentRoot, "wwwroot");
if (!Directory.Exists(webRoot))
{
    var srcWwwroot = Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "..", "wwwroot"));
    if (Directory.Exists(srcWwwroot)) webRoot = srcWwwroot;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot, WebRootPath = webRoot });
builder.WebHost.UseUrls($"http://{bind}:{port}");
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
var app = builder.Build();

// Token gate on everything under /api. The static SPA shell (/, /index.html) is open — it carries no
// data and asks for the token itself, then sends it as a Bearer header on API calls.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var presented = RemoteAuth.Extract(ctx.Request.Headers.Authorization, ctx.Request.Headers["X-Auth-Token"]);
        if (!RemoteAuth.Matches(presented, token))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { ok = true, service = "codex-local-retrieval", chats = archive.Store.Sessions.Count }));
app.MapGet("/api/stats", () => Results.Json(api.Stats()));
app.MapGet("/api/chats", (string? q, int? limit) => Results.Json(api.Search(q, limit ?? 20)));
app.MapGet("/api/chats/{id}", (string id, int? page, int? pageSize) =>
    api.Read(id, page ?? 0, pageSize ?? 20) is { } r ? Results.Json(r) : Results.NotFound(new { error = "no chat with that id" }));
app.MapGet("/api/chats/{id}/events", (string id, int? limit) =>
    api.Events(id, limit ?? 400) is { } r ? Results.Json(r) : Results.NotFound(new { error = "no chat with that id" }));
app.MapPost("/api/copilot", async (CopilotRequest req, CancellationToken ct) => Results.Json(await api.CopilotAsync(req.Message, req.History, ct)));
app.MapPost("/api/chats/{id}/resume", (string id, ResumeRequest? req) => Results.Json(api.ResumeCommand(id, req?.Launch ?? false)));
app.MapPost("/api/chats/{id}/favorite", async (string id, FavoriteRequest? req) => Results.Json(await api.FavoriteAsync(id, req?.Favorite ?? true)));

Console.WriteLine($"codex-local-retrieval remote server on http://{bind}:{port}  (chats: {archive.Store.Sessions.Count}, launch: {(allowLaunch ? "on" : "off")}, redact-reads: {(redactReads ? "on" : "off")})");
app.Run();
return 0;
