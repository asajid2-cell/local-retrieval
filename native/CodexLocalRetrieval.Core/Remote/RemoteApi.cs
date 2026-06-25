using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

// HTTP-agnostic request bodies (minimal API binds JSON onto these).
public sealed record CopilotTurn(string? Role, string? Content);
public sealed record CopilotRequest(string Message, List<CopilotTurn>? History);
public sealed record ResumeRequest(bool Launch);
public sealed record FavoriteRequest(bool Favorite);

// The remote API surface. Every method is plain (no HttpContext) so it can be unit-tested directly
// against a real ArchiveService. It reuses the SAME co-pilot pieces as the desktop app. Read-only by
// default; the only state-changing operations are explicit, user-initiated taps (favorite / resume),
// which ARE the approval. Chat content sent to the model is redacted by ArchiveToolService; direct
// reads optionally redact when RedactReads is set (see SecretRedactor).
public sealed class RemoteApi
{
    private readonly ArchiveService _archive;
    private readonly Func<IChatBackend?> _backendFactory;
    private readonly bool _redactReads;
    private readonly bool _allowLaunch;

    public RemoteApi(ArchiveService archive, Func<IChatBackend?> backendFactory, bool redactReads = false, bool allowLaunch = false)
    {
        _archive = archive;
        _backendFactory = backendFactory;
        _redactReads = redactReads;
        _allowLaunch = allowLaunch;
    }

    public object Stats()
    {
        var sessions = _archive.Store.Sessions.Values.Where(s => !s.Archived).ToList();
        var byTool = sessions.GroupBy(s => string.IsNullOrWhiteSpace(s.Tool) ? "codex" : s.Tool.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Count());
        return new
        {
            totalChats = sessions.Count,
            byTool,
            favorites = sessions.Count(s => s.Pinned),
            projects = _archive.Store.Collections.Count
        };
    }

    public object Search(string? q, int limit)
    {
        limit = Math.Clamp(limit, 1, 50);
        var results = _archive.DeepSearch(q ?? "", limit).Select(h => new
        {
            id = h.Session.Id,
            title = SecretRedactor.Scrub(h.Session.DisplayTitle),
            workspace = SecretRedactor.Scrub(h.Session.WorkspaceName),
            tool = h.Session.Tool,
            pinned = h.Session.Pinned,
            updatedAt = h.Session.UpdatedAt,
            snippet = SecretRedactor.Scrub(Cap(h.Snippet, 240))
        }).ToList();
        return new { query = q ?? "", count = results.Count, results };
    }

    public object? Read(string id, int page, int pageSize)
    {
        var session = _archive.GetSession(id);
        if (session is null) return null;
        _archive.EnsureContent(session); // content lazy-loads from the source file
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var all = session.Messages;
        var slice = all.Skip(page * pageSize).Take(pageSize).Select(m => new
        {
            role = m.RoleLabel,
            text = Reveal(ArchiveService.ForReading(m.Text))
        }).ToList();
        return new
        {
            id = session.Id,
            title = SecretRedactor.Scrub(session.DisplayTitle),
            tool = session.Tool,
            workspace = SecretRedactor.Scrub(session.WorkspaceName),
            updatedAt = session.UpdatedAt,
            pinned = session.Pinned,
            totalMessages = all.Count,
            page,
            pageSize,
            hasMore = (page + 1) * pageSize < all.Count,
            messages = slice
        };
    }

    public object? Events(string id, int limit)
    {
        var session = _archive.GetSession(id);
        if (session is null) return null;
        limit = Math.Clamp(limit, 1, 1000);
        var events = _archive.ReadEvents(session, limit).Select(e => new
        {
            kind = e.Kind,
            timestamp = e.Timestamp,
            preview = Reveal(e.Preview)
        }).ToList();
        return new { id = session.Id, title = SecretRedactor.Scrub(session.DisplayTitle), count = events.Count, events };
    }

    public async Task<object> CopilotAsync(string message, List<CopilotTurn>? history, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return new { error = "Empty message." };
        var backend = _backendFactory();
        if (backend is null) return new { error = "No model key configured. Set DEEPSEEK_API_KEY on the server." };

        // Read-only tool surface for the remote co-pilot: no callbacks (so show_chat/resume_chat are
        // not registered) and mutation tools filtered out. The user does state changes via explicit taps.
        var tools = new ArchiveToolService(_archive).Tools().Where(t => !t.IsMutation).ToList();
        var orchestrator = new ChatOrchestrator(backend, tools);

        var messages = new List<ChatMessage> { ChatMessage.System(ArchiveToolService.SystemPrompt) };
        if (history is not null)
            foreach (var turn in history.TakeLast(24))
            {
                var role = turn.Role?.ToLowerInvariant();
                if ((role == "user" || role == "assistant") && !string.IsNullOrWhiteSpace(turn.Content))
                    messages.Add(new ChatMessage { Role = role, Content = turn.Content });
            }
        messages.Add(ChatMessage.User(message.Trim()));

        var result = await orchestrator.RunAsync(messages, ct);
        return new
        {
            answer = result.Answer,
            error = result.Error,
            stopped = result.Stopped,
            activity = result.Activity.Select(a => new { tool = a.Tool, ok = a.Ok, summary = a.ResultSummary }).ToList()
        };
    }

    // Build the resume command for a chat ("send the next task"). Only actually launches a terminal
    // when the server is configured to allow it AND the trusted-exe check passes; otherwise returns
    // the command for the user to run. Refusals from BuildResumeLaunch surface as launched=false.
    public object ResumeCommand(string id, bool launch)
    {
        var session = _archive.GetSession(id);
        if (session is null) return new { error = "No chat with that id." };
        var resume = _archive.BuildResumeLaunch(session);
        var refused = string.IsNullOrEmpty(resume.Exe);
        var launched = false;
        string? note = refused ? resume.DisplayCommand : null;

        if (!refused && launch && _allowLaunch)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = resume.Exe,
                    Arguments = resume.Arguments,
                    WorkingDirectory = resume.WorkingDirectory,
                    UseShellExecute = true // open in its own terminal window on the host
                };
                System.Diagnostics.Process.Start(psi);
                launched = true;
            }
            catch (Exception ex) { note = "Launch failed: " + ex.Message; }
        }
        else if (!refused && launch && !_allowLaunch)
        {
            note = "Launch is disabled on this server (set CLR_REMOTE_ALLOW_LAUNCH=1). Copy the command instead.";
        }

        return new
        {
            id = session.Id,
            tool = session.Tool,
            command = resume.DisplayCommand,
            workingDirectory = resume.WorkingDirectory,
            launched,
            note
        };
    }

    public async Task<object> FavoriteAsync(string id, bool favorite)
    {
        var ok = await _archive.SetFavoriteAsync(id, favorite);
        return new { ok, id, favorite, message = ok ? (favorite ? "Pinned." : "Unpinned.") : "No chat with that id." };
    }

    private string Reveal(string s) => _redactReads ? SecretRedactor.Scrub(s) : s;

    private static string Cap(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
