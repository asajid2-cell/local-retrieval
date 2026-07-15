using System.Text.Json;
using System.Text.Json.Nodes;
using CodexLocalRetrieval.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Core.Services;

// Branching = clone a chat's exact history into a NEW, independently-resumable session, tagged as a
// branch and linked to its parent. It is the app's own version of the tool's fork/branch, plus a durable
// snapshot: the clone is frozen at branch time and reopenable at any point via normal Resume.
public sealed partial class ArchiveService
{
    public readonly record struct BranchResult(bool Ok, string Message, ArchiveSession? Branch);

    // Clone `parent` into a new branch session. Per-tool the transcript is copied to a fresh resumable id
    // (Claude: rewrite sessionId throughout + a forkedFrom marker; Codex: rewrite the session_meta id +
    // register a threads row so `codex resume` can find it). The branch is registered, linked to the
    // parent, and persisted. Returns the new session or a human-readable failure reason.
    public async Task<BranchResult> BranchSessionAsync(ArchiveSession parent)
    {
        if (parent is null) return new BranchResult(false, "No chat to branch.", null);

        var srcPath = ResolveSessionSourcePath(parent);
        if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
            return new BranchResult(false, "This chat's transcript file isn't on disk, so it can't be branched.", null);

        var tool = (parent.Tool ?? "").Trim().ToLowerInvariant();
        var newId = Guid.NewGuid().ToString();
        string newPath;
        try
        {
            newPath = tool switch
            {
                "claude" => BranchClaudeTranscript(srcPath, parent.Id, newId),
                "codex" => BranchCodexTranscript(srcPath, parent, newId),
                _ => throw new InvalidOperationException($"branching isn't supported for tool '{parent.Tool}'.")
            };
        }
        catch (Exception ex)
        {
            return new BranchResult(false, $"Branch failed: {ex.Message}", null);
        }

        var now = DateTime.UtcNow.ToString("o");
        var branch = new ArchiveSession
        {
            Id = newId,
            Tool = tool,
            Title = parent.Title,
            CustomTitle = string.IsNullOrWhiteSpace(parent.DisplayTitle) ? "" : parent.DisplayTitle + " (branch)",
            SourcePath = newPath,
            Workspace = parent.Workspace,
            WorkspaceName = parent.WorkspaceName,
            Model = parent.Model,
            CreatedAt = string.IsNullOrWhiteSpace(parent.CreatedAt) ? now : parent.CreatedAt,
            UpdatedAt = now,
            MessageCount = parent.MessageCount,
            UserMessageCount = parent.UserMessageCount,
            Text = parent.Text,
            BranchOfId = parent.Id,
            BranchedAt = now
        };
        AddAlias(branch.Aliases, parent.Id);   // the parent id resolves against the branch too
        Store.Sessions[newId] = branch;
        await SaveAsync();
        ReapplyList();
        return new BranchResult(true, $"Branched \"{parent.DisplayTitle}\".", branch);
    }

    // Mark / unmark a chat as a reusable template (a curated starting point spawned via branch).
    public async Task SetTemplateAsync(ArchiveSession session, bool isTemplate)
    {
        if (session is null || session.IsTemplate == isTemplate) return;
        session.IsTemplate = isTemplate;
        await SaveAsync();
        ReapplyList();
    }

    // Human-readable one-liner of how a chat is filed — used by the agent "info" op so a chat can check
    // its own name, collections, phrases, template/branch status from the /stashme skill.
    internal string DescribeSession(ArchiveSession s)
    {
        var cols = Store.Collections.Values
            .Where(c => c.SessionIds.Contains(s.Id))
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var phrases = s.SpecialPhrases.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var parent = s.IsBranch
            ? (Store.Sessions.TryGetValue(s.BranchOfId, out var p) ? $"\"{p.DisplayTitle}\"" : s.BranchOfId)
            : "no";
        var branchCount = Store.Sessions.Values.Count(x =>
            !x.Archived && string.Equals(x.BranchOfId, s.Id, StringComparison.OrdinalIgnoreCase));
        return $"Chat \"{s.DisplayTitle}\" [{s.Tool}]"
             + $" · native name: {(string.IsNullOrWhiteSpace(s.Title) ? "(none)" : $"\"{s.Title}\"")}"
             + $" · collections: {(cols.Count == 0 ? "none" : string.Join(", ", cols))}"
             + $" · phrases: {(phrases.Count == 0 ? "none" : string.Join(", ", phrases))}"
             + $" · template: {(s.IsTemplate ? "yes" : "no")}"
             + $" · branch of: {parent}"
             + (branchCount > 0 ? $" · branches: {branchCount}" : "")
             + $" · id: {s.Id}";
    }

    // The chats the user curated as templates, newest-updated first.
    public IReadOnlyList<ArchiveSession> Templates() =>
        Store.Sessions.Values
            .Where(s => s.IsTemplate && !s.Archived)
            .OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal)
            .ToList();

    private string ResolveSessionSourcePath(ArchiveSession session)
    {
        if (string.IsNullOrEmpty(session.SourcePath)) return "";
        return Path.IsPathRooted(session.SourcePath) ? session.SourcePath : Path.Combine(_rootPath, session.SourcePath);
    }

    // Claude: a branch is a new <uuid>.jsonl in the SAME project folder, with sessionId rewritten to the
    // new id on every line and a forkedFrom marker on the first line — exactly the shape `/branch` writes,
    // so `claude --resume <newId>` reopens it with the full history.
    private static string BranchClaudeTranscript(string srcPath, string parentId, string newId)
    {
        var dir = Path.GetDirectoryName(srcPath) ?? throw new InvalidOperationException("transcript has no folder.");
        var destPath = Path.Combine(dir, newId + ".jsonl");
        var lines = File.ReadAllLines(srcPath);

        // The branch point is the tip of the parent's history (a full clone).
        string? tipUuid = null;
        for (int i = lines.Length - 1; i >= 0 && tipUuid is null; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (JsonNode.Parse(lines[i]) is JsonObject o && o["uuid"] is JsonValue uv) tipUuid = uv.ToString();
        }

        var outLines = new List<string>(lines.Length);
        var stampedFork = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) { outLines.Add(line); continue; }
            if (JsonNode.Parse(line) is not JsonObject obj) { outLines.Add(line); continue; }
            if (obj.ContainsKey("sessionId")) obj["sessionId"] = newId;
            if (!stampedFork)
            {
                obj["forkedFrom"] = new JsonObject { ["sessionId"] = parentId, ["messageUuid"] = tipUuid ?? "" };
                stampedFork = true;
            }
            outLines.Add(obj.ToJsonString());
        }
        File.WriteAllText(destPath, string.Join("\n", outLines) + "\n");
        return destPath;
    }

    // Codex: a branch is a copied rollout under ~/.codex/sessions/<Y>/<M>/<D> with a fresh id, and a
    // matching row in state_5.sqlite so `codex resume <newId>` resolves it. Only the first line
    // (session_meta) carries the id, so the (potentially huge) rest of the rollout is streamed verbatim.
    private string BranchCodexTranscript(string srcPath, ArchiveSession parent, string newId)
    {
        var now = DateTime.Now;
        var root = DefaultCodexSessionsRoot;
        var dir = Path.Combine(root, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
        Directory.CreateDirectory(dir);
        var fileName = $"rollout-{now:yyyy-MM-ddTHH-mm-ss}-{newId}.jsonl";
        var destPath = Path.Combine(dir, fileName);

        using (var reader = new StreamReader(srcPath))
        using (var writer = new StreamWriter(destPath, append: false) { NewLine = "\n" })
        {
            var first = reader.ReadLine();
            if (first is null) throw new InvalidOperationException("the rollout is empty.");
            writer.WriteLine(RewriteCodexSessionMeta(first, parent.Id, newId));
            var buffer = new char[1 << 16];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) writer.Write(buffer, 0, read);
        }

        RegisterCodexBranchThread(parent, newId, destPath);
        return destPath;
    }

    internal static string RewriteCodexSessionMeta(string line, string parentId, string newId)
    {
        if (JsonNode.Parse(line) is not JsonObject obj) return line;
        if (obj["payload"] is JsonObject payload)
        {
            if (payload.ContainsKey("id")) payload["id"] = newId;
            if (payload.ContainsKey("session_id")) payload["session_id"] = newId;
            payload["forked_from_id"] = parentId;
        }
        if (obj.ContainsKey("id")) obj["id"] = newId;
        return obj.ToJsonString();
    }

    // Give the branch its own row in Codex's thread index. We CLONE the parent's row (or, if it's missing,
    // the most recent row) so every NOT NULL column is satisfied, then override the identity + timestamps.
    // Best-effort: a resume can still fall back to a rollout file scan, so a DB hiccup never aborts a branch.
    private static void RegisterCodexBranchThread(ArchiveSession parent, string newId, string rolloutPath)
    {
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");
        if (!File.Exists(dbPath)) return;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            conn.Open();

            var cols = new List<string>();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "pragma table_info(threads)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) cols.Add(r.GetString(1));
            }
            if (cols.Count == 0) return;

            var row = ReadThreadRow(conn, cols, "select * from threads where id = $id", parent.Id)
                      ?? ReadThreadRow(conn, cols, "select * from threads order by updated_at desc limit 1", null);
            if (row is null) return;   // no template to satisfy NOT NULL columns; leave to file-scan resume

            long nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            void Set(string col, object? v) { if (row.ContainsKey(col)) row[col] = v; }
            row["id"] = newId;
            row["rollout_path"] = rolloutPath;
            Set("created_at", nowS); Set("updated_at", nowS); Set("recency_at", nowS);
            Set("created_at_ms", nowMs); Set("updated_at_ms", nowMs); Set("recency_at_ms", nowMs);
            Set("archived", 0L); Set("archived_at", null);
            if (row.TryGetValue("title", out var t) && t is string ts && !string.IsNullOrWhiteSpace(ts))
                row["title"] = ts + " (branch)";

            var colList = string.Join(",", cols);
            var paramList = string.Join(",", cols.Select((_, i) => "$p" + i));
            using var ins = conn.CreateCommand();
            ins.CommandText = $"insert or replace into threads ({colList}) values ({paramList})";
            for (int i = 0; i < cols.Count; i++)
                ins.Parameters.AddWithValue("$p" + i, row[cols[i]] ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }
        catch { /* best-effort: the rollout file itself is enough for a mtime-scan resume */ }
    }

    private static Dictionary<string, object?>? ReadThreadRow(SqliteConnection conn, List<string> cols, string sql, string? id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (id is not null) cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cols.Count; i++) map[cols[i]] = r.IsDBNull(i) ? null : r.GetValue(i);
        return map;
    }
}
