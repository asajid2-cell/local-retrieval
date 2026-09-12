using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval_Native;

// Give each running session its REAL claude/codex name (+ a first-prompt preview), and let the web open
// a transcript tail — so a session that isn't in our archive (no app title) still shows what it is.
public sealed partial class MainPage
{
    private static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string CodexDbPath => Path.Combine(HomeDir, ".codex", "state_5.sqlite");

    private static string Trunc(string? s, int n)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= n ? s : s.Substring(0, n).TrimEnd() + "…";
    }

    // Fill RealTitle + Preview on each running session (codex from its state DB, claude from its transcript).
    private List<ArchiveService.RunningSessionInfo> EnrichRunningSessionTitles(List<ArchiveService.RunningSessionInfo> list)
    {
        var outList = new List<ArchiveService.RunningSessionInfo>(list.Count);
        foreach (var s in list)
        {
            string title = "", preview = "";
            try
            {
                if (string.Equals(s.Tool, "codex", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(s.SessionId))
                {
                    var (t, fum) = ArchiveService.ReadCodexThreadTitle(CodexDbPath, s.SessionId);
                    title = !string.IsNullOrWhiteSpace(t) ? Trunc(t, 90) : Trunc(fum, 90);
                    preview = Trunc(fum, 160);
                }
                else if (!string.IsNullOrEmpty(s.SessionId))
                {
                    var (t, p) = ClaudeTitlePreview(s.SessionId);
                    title = t; preview = p;
                }
            }
            catch { }
            outList.Add(s with { RealTitle = title ?? "", Preview = preview ?? "" });
        }
        return outList;
    }

    private static string? FindClaudeTranscript(string sessionId)
    {
        try
        {
            var root = Path.Combine(HomeDir, ".claude", "projects");
            if (!Directory.Exists(root)) return null;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var f = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(f)) return f;
            }
        }
        catch { }
        return null;
    }

    // Claude's OWN title is the `custom-title` line it writes (what the resume picker shows); fall back
    // to a `summary`, then the first user prompt. Preview = first prompt. Scans only the head (cheap).
    private (string title, string preview) ClaudeTitlePreview(string sessionId)
    {
        var f = FindClaudeTranscript(sessionId);
        if (f is null) return ("", "");
        string customTitle = "", summary = "", firstUser = "";
        try
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rd = new StreamReader(fs);
            string? line; int n = 0;
            while ((line = rd.ReadLine()) != null)
            {
                n++; if (n > 250) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (type == "custom-title" && root.TryGetProperty("customTitle", out var ct)) customTitle = ct.GetString() ?? customTitle;   // last wins
                    else if (type == "summary" && string.IsNullOrEmpty(summary) && root.TryGetProperty("summary", out var sm)) summary = sm.GetString() ?? "";
                    else if (type == "user" && string.IsNullOrEmpty(firstUser)) { var t = StripLeadingBoilerplate(ExtractClaudeText(root)); if (!string.IsNullOrWhiteSpace(t)) firstUser = t; }
                }
                catch { }
            }
        }
        catch { }
        var title = !string.IsNullOrWhiteSpace(customTitle) ? customTitle
                  : !string.IsNullOrWhiteSpace(summary) ? summary : firstUser;
        return (Trunc(title, 90), Trunc(firstUser, 160));
    }

    // Pull (role,text) messages off a transcript. maxLines>0 reads only the head (for a title); 0 = all.
    private static List<(string role, string text)> ExtractMessages(string file, string tool, int maxLines)
    {
        var outl = new List<(string, string)>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rd = new StreamReader(fs);
            string? line; int n = 0;
            while ((line = rd.ReadLine()) != null)
            {
                n++; if (maxLines > 0 && n > maxLines) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase))
                    {
                        if (type == "event_msg" && root.TryGetProperty("payload", out var pl))
                        {
                            var pt = pl.TryGetProperty("type", out var ptp) ? ptp.GetString() : null;
                            if (pt == "user_message" || pt == "agent_message")
                            {
                                var msg = pl.TryGetProperty("message", out var mp) ? mp.GetString() ?? "" : "";
                                outl.Add((pt == "agent_message" ? "assistant" : "user", msg));
                            }
                        }
                    }
                    else
                    {
                        if (type == "summary") { var sm = root.TryGetProperty("summary", out var sp) ? sp.GetString() ?? "" : ""; outl.Add(("summary", sm)); }
                        else if (type == "user" || type == "assistant")
                        {
                            var text = ExtractClaudeText(root);
                            if (!string.IsNullOrWhiteSpace(text)) outl.Add((type, text));
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return outl;
    }

    // Claude Code injects boilerplate into the FIRST user message (<ide_opened_file>…</ide_opened_file>,
    // <system-reminder>…, <command-name>…, <local-command-…>). Peel any such leading tag blocks so the
    // running-session "real name" preview shows the actual prompt, not the IDE wrapper. Bounded loop.
    private static string StripLeadingBoilerplate(string t)
    {
        if (string.IsNullOrEmpty(t)) return t;
        var s = t.TrimStart();
        for (var guard = 0; guard < 6 && s.StartsWith("<", StringComparison.Ordinal); guard++)
        {
            var close = s.IndexOf('>');
            if (close < 1) break;
            var tag = s.Substring(1, close - 1).Split(' ', '\t', '\n', '\r', '/')[0].Trim();
            if (tag.Length == 0) break;
            var endTag = "</" + tag + ">";
            var endIdx = s.IndexOf(endTag, StringComparison.Ordinal);
            if (endIdx >= 0) s = s.Substring(endIdx + endTag.Length).TrimStart();
            else s = s.Substring(close + 1).TrimStart();   // unclosed wrapper: drop just the opening tag
        }
        return s.Trim();
    }

    private static string ExtractClaudeText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var m)) return "";
        if (m.TryGetProperty("content", out var c))
        {
            if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
            if (c.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var b in c.EnumerateArray())
                    if (b.TryGetProperty("type", out var bt) && bt.GetString() == "text" && b.TryGetProperty("text", out var txt))
                        sb.Append(txt.GetString());
                return sb.ToString();
            }
        }
        return "";
    }

    // On-demand transcript tail for the web's "Open" — the last ~20 turns, newest kept under maxChars.
    private string ReadTranscriptTail(string tool, string sessionId, int maxChars)
    {
        try
        {
            string? file = string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase)
                ? ArchiveService.ReadCodexRolloutPath(CodexDbPath, sessionId)
                : FindClaudeTranscript(sessionId);
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return "(transcript not found on this PC)";
            var msgs = ExtractMessages(file, tool, 0).Where(m => m.role != "summary").ToList();
            if (msgs.Count == 0) return "(no messages yet)";
            var sb = new StringBuilder();
            foreach (var (role, text) in msgs.TakeLast(20))
            {
                sb.Append(role == "user" ? "› you:\n" : "• agent:\n");
                sb.Append(Trunc(text, 800)); sb.Append("\n\n");
            }
            var s = sb.ToString().Trim();
            return s.Length <= maxChars ? s : "…\n" + s.Substring(s.Length - maxChars);
        }
        catch (Exception ex) { return "(error reading transcript: " + ex.Message + ")"; }
    }
}
