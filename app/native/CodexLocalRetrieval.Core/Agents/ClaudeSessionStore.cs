using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodexLocalRetrieval.Core.Agents;

// Reads Claude Code sessions (stored at ~/.claude/projects/<encoded-cwd>/<session-uuid>.jsonl) so they
// appear and render in the same browser as Codex sessions. Each line is an Anthropic-message-shaped
// record {type:user|assistant, message:{role, content:[...]}, cwd, uuid}. We normalize the same way the
// rollout parser does for codex, into AgentEvents, so one renderer shows both tools identically.
public sealed class ClaudeSessionStore
{
    private const int MaxHistoryLineChars = 4 * 1024 * 1024;
    private const int MaxMessageChars = 128 * 1024;
    private readonly string _root;
    // session id -> rollout path, populated on List() so Open can find the file by id.
    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeSessionStore(string? root = null) =>
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public bool Available => Directory.Exists(_root);

    // The most-recent sessions across all project folders (cap keeps the merged list fast).
    public List<ClaudeSessionInfo> List(int max = 100)
    {
        var infos = new List<ClaudeSessionInfo>();
        if (!Directory.Exists(_root)) return infos;

        var files = new List<FileInfo>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
                files.Add(new FileInfo(f));

        foreach (var fi in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(max))
        {
            var (cwd, title) = Peek(fi.FullName);
            var id = Path.GetFileNameWithoutExtension(fi.Name);
            _paths[id] = fi.FullName;
            infos.Add(new ClaudeSessionInfo(
                id, title, cwd,
                new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds()));
        }
        return infos;
    }

    public string? PathOf(string id)
    {
        if (_paths.TryGetValue(id, out var path))
        {
            if (File.Exists(path)) return path;
            _paths.TryRemove(new KeyValuePair<string, string>(id, path));
        }
        return ResolveById(id);
    }

    // Find a session file by id even if List() wasn't called this process (deep link / restart).
    private string? ResolveById(string id)
    {
        if (!Directory.Exists(_root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var p = Path.Combine(dir, id + ".jsonl");
            if (File.Exists(p)) return _paths.GetOrAdd(id, p);
        }
        return null;
    }

    public List<AgentEvent> ReadHistory(string id, int maxEvents = 600, int maxOutputChars = 6000)
    {
        if (maxEvents <= 0) return new List<AgentEvent>();
        var events = new Queue<AgentEvent>(maxEvents);
        void Add(AgentEvent ev)
        {
            events.Enqueue(ev);
            if (events.Count > maxEvents) events.Dequeue();
        }
        var path = PathOf(id);
        if (path is null || !File.Exists(path)) return new List<AgentEvent>();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var reader = new BoundedTextLineReader(
            sr,
            MaxHistoryLineChars,
            discardOversizedLine: true);
        string? line;
        while (true)
        {
            try { line = reader.ReadLine(); }
            catch (InvalidDataException) { continue; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement root;
            try { using var d = JsonDocument.Parse(line); root = d.RootElement.Clone(); } catch { continue; }
            var type = Str(root, "type");
            if (type != "user" && type != "assistant") continue;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) continue;

            if (content.ValueKind == JsonValueKind.String)
            {
                if (type == "user" && !IsBoilerplate(content.GetString())) Add(new AgentEvent { Kind = AgentEventKind.UserMessage, Text = Cap(content.GetString() ?? "", MaxMessageChars) });
                else if (type == "assistant") Add(new AgentEvent { Kind = AgentEventKind.AssistantText, Text = Cap(content.GetString() ?? "", MaxMessageChars) });
                continue;
            }
            if (content.ValueKind != JsonValueKind.Array) continue;

            foreach (var b in content.EnumerateArray())
            {
                switch (Str(b, "type"))
                {
                    case "text":
                        var t = Str(b, "text") ?? "";
                        if (type == "user") { if (!IsBoilerplate(t)) Add(new AgentEvent { Kind = AgentEventKind.UserMessage, Text = Cap(t, MaxMessageChars) }); }
                        else Add(new AgentEvent { Kind = AgentEventKind.AssistantText, Text = Cap(t, MaxMessageChars) });
                        break;
                    case "thinking":
                        var th = Str(b, "thinking") ?? "";
                        if (th.Length > 0) Add(new AgentEvent { Kind = AgentEventKind.Thinking, Text = Cap(th, MaxMessageChars) });
                        break;
                    case "tool_use":
                        Add(new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = Str(b, "id"), ToolName = Str(b, "name") ?? "tool", ToolInput = RawToolInput(b), State = "completed" });
                        break;
                    case "tool_result":
                        Add(new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = Str(b, "tool_use_id"), Output = Cap(FlattenResult(b), maxOutputChars), State = "completed" });
                        break;
                }
            }
        }
        return events.ToList();
    }

    // The session's real title, exactly as Claude Code resolves it: a user-set custom-title wins, else
    // Claude's generated ai-title, else the first real user prompt. Custom/ai titles are appended to the
    // .jsonl (so they live in the tail); the first prompt is in the head.
    private static (string cwd, string title) Peek(string path)
    {
        string cwd = "", firstPrompt = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var reader = new BoundedTextLineReader(
                sr,
                1024 * 1024,
                discardOversizedLine: true);
            string? line; int n = 0;
            while (n++ < 250)
            {
                try { line = reader.ReadLine(); }
                catch (InvalidDataException) { continue; }
                if (line is null) break;
                JsonElement root;
                try { using var d = JsonDocument.Parse(line); root = d.RootElement.Clone(); } catch { continue; }
                if (cwd.Length == 0 && Str(root, "cwd") is { Length: > 0 } c) cwd = c;
                if (firstPrompt.Length == 0 && Str(root, "type") == "user" && root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var ct))
                {
                    var u = FirstUserText(ct);
                    if (u is { Length: > 0 }) firstPrompt = u.Length > 90 ? u[..90] : u;
                }
                if (cwd.Length > 0 && firstPrompt.Length > 0) break;
            }
        }
        catch { }
        var (custom, ai) = TailTitle(path);
        var title = !string.IsNullOrWhiteSpace(custom) ? custom!
                  : !string.IsNullOrWhiteSpace(ai) ? ai!
                  : firstPrompt.Length > 0 ? firstPrompt : "(Claude session)";
        return (cwd, title);
    }

    // Scan the file tail (where renames/titles are appended) for the latest custom-title / ai-title.
    private static (string? custom, string? ai) TailTitle(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var n = (int)Math.Min(fs.Length, 96 * 1024);
            fs.Seek(-n, SeekOrigin.End);
            var buf = new byte[n];
            int read = fs.Read(buf, 0, n);
            string? custom = null, ai = null;
            foreach (var line in Encoding.UTF8.GetString(buf, 0, read).Split('\n'))
            {
                if (line.IndexOf("Title", StringComparison.Ordinal) < 0) continue;
                try { using var d = JsonDocument.Parse(line); var r = d.RootElement;
                    if (Str(r, "customTitle") is { Length: > 0 } cu) custom = cu;     // latest wins
                    else if (Str(r, "aiTitle") is { Length: > 0 } at) ai = at;
                } catch { }
            }
            return (custom, ai);
        }
        catch { return (null, null); }
    }

    // Rename a session the same way Claude Code does: append a custom-title record to its .jsonl. The
    // Claude Code sidebar reads the same record, so the name is shared. Returns false if the file is gone.
    public bool RenameSession(string id, string title)
    {
        var path = PathOf(id);
        if (path is null || !File.Exists(path)) return false;
        // Invariant: the app never writes into a transcript a LIVE agent owns. Defer the rename if the
        // session is currently running (its own writes take precedence; retry once it's idle).
        if (IsIdLive(id)) return false;
        if (!new CodexLocalRetrieval.Core.Remote.SessionLaunchGovernor().TryAcquire(
            new(id, null, "claude", "native-rename", "native title write", "", "", ""),
            out var claim, out _)) return false;
        using (claim)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                if (string.Equals(TailTitle(path).custom, title, StringComparison.Ordinal)) return true;
                fs.Seek(0, SeekOrigin.End);
                var rec = JsonSerializer.Serialize(new { type = "custom-title", sessionId = id, customTitle = title });
                using var sw = new StreamWriter(fs);
                sw.Write(rec + "\n");
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    // True if a live claude/codex agent is currently resuming this session id (so its transcript is owned).
    private static bool IsIdLive(string id)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (!CodexLocalRetrieval.Core.Remote.RunningSessions.TryAllLiveSessionIds(
                out var live, out var unverifiable, out _, bypassCache: true)) return true;
            return unverifiable.Count != 0 || live.Contains(id);
        }
        catch { return true; }
    }

    private static string? FirstUserText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return IsBoilerplate(content.GetString()) ? null : content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var b in content.EnumerateArray())
            if (Str(b, "type") == "text" && Str(b, "text") is { } t && !IsBoilerplate(t))
                return t.Replace('\n', ' ').Trim();
        return null;
    }

    // Injected context + bare "continue" stubs aren't real titles — Claude's own resume list skips them too.
    private static readonly string[] Stubs =
        { "continue", "continue.", "continue from where you left off.", "go on", "go ahead", "keep going",
          "keep working", "proceed", "resume", "next", "carry on" };

    private static bool IsBoilerplate(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return true;
        var s = t.TrimStart();
        if (s.StartsWith("<system-reminder") || s.StartsWith("<ide_") || s.StartsWith("<command-")
            || s.StartsWith("<local-command") || s.StartsWith("<user-prompt-submit-hook")
            || s.StartsWith("Caveat:") || s.StartsWith("This session is being continued"))
            return true;
        var trimmed = t.Trim();
        if (trimmed.Length < 3) return true;                       // "y", "ok"
        var lower = trimmed.ToLowerInvariant().TrimEnd('.', '!');
        return Array.IndexOf(Stubs, lower) >= 0 || Array.IndexOf(Stubs, lower + ".") >= 0;
    }

    // The full tool input as JSON, so the client can render each tool richly (a todo checklist, a file
    // path, a command, a diff…). Capped so a huge Write payload can't bloat the stream; if the cap trims
    // it past valid JSON the client falls back to showing the raw text.
    internal static string RawToolInput(JsonElement b)
    {
        if (!b.TryGetProperty("input", out var inp)) return "";
        var s = inp.ValueKind == JsonValueKind.String ? inp.GetString() ?? "" : inp.GetRawText();
        return s.Length <= 12000 ? s : s[..12000];
    }

    internal static string FlattenResult(JsonElement b)
    {
        if (!b.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var x in c.EnumerateArray())
                if (x.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
            return sb.ToString();
        }
        return c.GetRawText();
    }

    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n] + "\n…(truncated)";
}

public sealed record ClaudeSessionInfo(string Id, string Title, string Cwd, long UpdatedAt);
