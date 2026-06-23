using System.Text;
using System.Text.Json;

namespace CodexLocalRetrieval.Core.Agents;

// Reads Claude Code sessions (stored at ~/.claude/projects/<encoded-cwd>/<session-uuid>.jsonl) so they
// appear and render in the same browser as Codex sessions. Each line is an Anthropic-message-shaped
// record {type:user|assistant, message:{role, content:[...]}, cwd, uuid}. We normalize the same way the
// rollout parser does for codex, into AgentEvents, so one renderer shows both tools identically.
public sealed class ClaudeSessionStore
{
    private readonly string _root;
    // session id -> rollout path, populated on List() so Open can find the file by id.
    private readonly Dictionary<string, string> _paths = new();

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

    public string? PathOf(string id) => _paths.TryGetValue(id, out var p) ? p : ResolveById(id);

    // Find a session file by id even if List() wasn't called this process (deep link / restart).
    private string? ResolveById(string id)
    {
        if (!Directory.Exists(_root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var p = Path.Combine(dir, id + ".jsonl");
            if (File.Exists(p)) { _paths[id] = p; return p; }
        }
        return null;
    }

    public List<AgentEvent> ReadHistory(string id, int maxEvents = 600, int maxOutputChars = 6000)
    {
        var events = new List<AgentEvent>();
        var path = PathOf(id);
        if (path is null || !File.Exists(path)) return events;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement root;
            try { using var d = JsonDocument.Parse(line); root = d.RootElement.Clone(); } catch { continue; }
            var type = Str(root, "type");
            if (type != "user" && type != "assistant") continue;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) continue;

            if (content.ValueKind == JsonValueKind.String)
            {
                if (type == "user" && !IsBoilerplate(content.GetString())) events.Add(new AgentEvent { Kind = AgentEventKind.UserMessage, Text = content.GetString() });
                else if (type == "assistant") events.Add(new AgentEvent { Kind = AgentEventKind.AssistantText, Text = content.GetString() });
                continue;
            }
            if (content.ValueKind != JsonValueKind.Array) continue;

            foreach (var b in content.EnumerateArray())
            {
                switch (Str(b, "type"))
                {
                    case "text":
                        var t = Str(b, "text") ?? "";
                        if (type == "user") { if (!IsBoilerplate(t)) events.Add(new AgentEvent { Kind = AgentEventKind.UserMessage, Text = t }); }
                        else events.Add(new AgentEvent { Kind = AgentEventKind.AssistantText, Text = t });
                        break;
                    case "thinking":
                        var th = Str(b, "thinking") ?? "";
                        if (th.Length > 0) events.Add(new AgentEvent { Kind = AgentEventKind.Thinking, Text = th });
                        break;
                    case "tool_use":
                        events.Add(new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = Str(b, "id"), ToolName = Str(b, "name") ?? "tool", ToolInput = RawToolInput(b), State = "completed" });
                        break;
                    case "tool_result":
                        events.Add(new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = Str(b, "tool_use_id"), Output = Cap(FlattenResult(b), maxOutputChars), State = "completed" });
                        break;
                }
            }
        }
        if (events.Count > maxEvents) events.RemoveRange(0, events.Count - maxEvents);
        return events;
    }

    // Read the first lines to get cwd + the first real (non-injected) user prompt as the title.
    private static (string cwd, string title) Peek(string path)
    {
        string cwd = "", title = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            string? line; int n = 0;
            while ((line = sr.ReadLine()) is not null && n++ < 80)
            {
                JsonElement root;
                try { using var d = JsonDocument.Parse(line); root = d.RootElement.Clone(); } catch { continue; }
                if (cwd.Length == 0 && Str(root, "cwd") is { Length: > 0 } c) cwd = c;
                if (title.Length == 0 && Str(root, "type") == "user" && root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var ct))
                {
                    var u = FirstUserText(ct);
                    if (u is { Length: > 0 }) title = u.Length > 90 ? u[..90] : u;
                }
                if (cwd.Length > 0 && title.Length > 0) break;
            }
        }
        catch { }
        return (cwd, title.Length > 0 ? title : "(Claude session)");
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

    private static bool IsBoilerplate(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return true;
        var s = t.TrimStart();
        return s.StartsWith("<system-reminder") || s.StartsWith("<ide_") || s.StartsWith("<command-")
            || s.StartsWith("<local-command") || s.StartsWith("<user-prompt-submit-hook")
            || s.StartsWith("Caveat:") || s.StartsWith("This session is being continued");
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
