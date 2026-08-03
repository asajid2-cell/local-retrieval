using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Core.Services;

internal sealed partial class TranscriptSearchIndex : IDisposable
{
    internal const int SchemaVersion = 1;
    private const int TailHashBytes = 64 * 1024;
    private const int CandidateRowLimit = 2_000;
    private static readonly TimeSpan LiveFileWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FallbackBudget = TimeSpan.FromSeconds(32);

    private readonly string _dbPath;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _statusGate = new();
    private TranscriptSearchIndexStatus _status = new(
        false,
        false,
        0,
        0,
        0,
        0,
        "",
        "",
        DateTimeOffset.MinValue);

    public TranscriptSearchIndex(string dbPath)
    {
        _dbPath = dbPath;
    }

    public string DatabasePath => _dbPath;

    public TranscriptSearchIndexStatus Status
    {
        get
        {
            lock (_statusGate) return _status;
        }
    }

    public async Task<TranscriptSearchSyncResult> SyncAsync(
        IReadOnlyCollection<ArchiveSession> sessions,
        IReadOnlyList<SessionSource> sources,
        IProgress<TranscriptSearchIndexStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            using var connection = OpenConnection();
            Initialize(connection);

            var known = sessions
                .Where(session => !string.IsNullOrWhiteSpace(session.SourcePath))
                .GroupBy(
                    session => FullPath(session.SourcePath),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            var files = EnumerateTranscriptFiles(sources, known)
                .OrderByDescending(file => file.LastWriteTicks)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var totalBytes = files.Sum(file => file.Length);
            SetStatus(new TranscriptSearchIndexStatus(
                true,
                false,
                0,
                files.Count,
                0,
                totalBytes,
                "",
                "",
                DateTimeOffset.UtcNow), progress);

            var states = LoadFileStates(connection);
            RegisterPendingFiles(connection, files, states);
            RemoveMissingFiles(connection, files, states);

            var indexedFiles = 0;
            var indexedBytes = 0L;
            var rebuilt = 0;
            var appended = 0;
            var unchanged = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetStatus(Status with
                {
                    CurrentFile = file.Path,
                    IndexedFiles = indexedFiles,
                    IndexedBytes = indexedBytes,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, progress);

                states.TryGetValue(file.Path, out var state);
                if (IsUnchanged(file, state))
                {
                    unchanged++;
                    indexedFiles++;
                    indexedBytes += file.Length;
                    continue;
                }

                var append = CanAppend(file, state);
                var startOffset = append ? state!.IndexedLength : 0;
                var outcome = IndexFile(
                    connection,
                    file,
                    startOffset,
                    rebuild: !append,
                    cancellationToken);
                if (append) appended++;
                else rebuilt++;
                indexedFiles++;
                indexedBytes += outcome.IndexedLength;
            }

            var complete = indexedFiles == files.Count;
            var finalStatus = new TranscriptSearchIndexStatus(
                false,
                complete,
                indexedFiles,
                files.Count,
                indexedBytes,
                totalBytes,
                "",
                "",
                DateTimeOffset.UtcNow);
            SetStatus(finalStatus, progress);
            stopwatch.Stop();
            return new TranscriptSearchSyncResult(
                indexedFiles,
                rebuilt,
                appended,
                unchanged,
                indexedBytes,
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            SetStatus(Status with
            {
                Building = false,
                Complete = false,
                LastError = ex.Message,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, progress);
            throw;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task<UnifiedSearchResult> SearchAsync(
        string query,
        int limit,
        Func<string, ArchiveSession?> sessionResolver,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var tokens = QueryTokens(query);
        if (tokens.Count == 0)
            return new UnifiedSearchResult(
                Array.Empty<ArchiveSearchHit>(),
                Coverage(Status, exhaustiveFallbackComplete: Status.Complete));

        var bySession = new Dictionary<string, CandidateSession>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_dbPath))
        {
            await Task.Run(
                () => SearchIndexed(query, tokens, bySession, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        var status = Status;
        var fallbackComplete = status.Complete;
        if (!status.Complete)
        {
            fallbackComplete = await Task.Run(
                () => SearchUnindexed(
                    query,
                    tokens,
                    bySession,
                    DateTime.UtcNow + FallbackBudget,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        var hits = bySession.Values
            .Where(candidate => candidate.MatchedTokens.Count == tokens.Count)
            .Select(candidate => (
                Candidate: candidate,
                Session: sessionResolver(candidate.SessionId)
                    ?? TryGetSession(candidate.SessionId)))
            .Where(item => item.Session is not null)
            .Select(item => item.Candidate.ToHit(item.Session!))
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Session.Pinned)
            .ThenByDescending(hit => hit.Session.UpdatedAt, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        return new UnifiedSearchResult(
            hits,
            Coverage(Status, fallbackComplete));
    }

    public ArchiveSession? TryGetSession(string sessionId)
    {
        if (!File.Exists(_dbPath) || string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            using var connection = OpenConnection(readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT session_id, title, tool, source_path, updated_at
                FROM files
                WHERE session_id = $id
                ORDER BY observed_last_write_ticks DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var path = reader.GetString(3);
            return new ArchiveSession
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Tool = reader.GetString(2),
                SourcePath = path,
                UpdatedAt = reader.GetString(4),
                CreatedAt = File.Exists(path)
                    ? File.GetCreationTimeUtc(path).ToString("O")
                    : "",
                Workspace = "Unknown workspace",
                WorkspaceName = "Unknown",
                Model = reader.GetString(2),
                ContentLoaded = false,
                Tags = new System.Collections.ObjectModel.ObservableCollection<string>(
                    new[] { "archive" }),
            };
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _syncGate.Dispose();
        try { SqliteConnection.ClearAllPools(); } catch { }
    }

    private void SearchIndexed(
        string query,
        IReadOnlyList<string> tokens,
        Dictionary<string, CandidateSession> bySession,
        CancellationToken cancellationToken)
    {
        using var connection = OpenConnection(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                t.session_id,
                t.source_path,
                t.byte_start,
                t.byte_length,
                t.block_ordinal,
                t.role,
                f.tool
            FROM turn_fts
            JOIN turns t ON t.id = turn_fts.rowid
            JOIN files f ON f.source_path = t.source_path
            WHERE turn_fts MATCH $query
            ORDER BY
                CASE t.role
                    WHEN 'user' THEN 0
                    WHEN 'assistant' THEN 1
                    ELSE 2
                END,
                f.observed_last_write_ticks DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", BuildFtsQuery(tokens));
        command.Parameters.AddWithValue("$limit", CandidateRowLimit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = reader.GetString(0);
            var path = reader.GetString(1);
            var byteStart = reader.GetInt64(2);
            var byteLength = reader.GetInt64(3);
            var blockOrdinal = reader.GetInt32(4);
            var role = reader.GetString(5);
            var tool = reader.GetString(6);
            var text = ReadTurnText(
                path,
                byteStart,
                byteLength,
                blockOrdinal,
                role,
                tool);
            if (string.IsNullOrWhiteSpace(text)) continue;
            AddCandidate(
                bySession,
                sessionId,
                path,
                byteStart,
                byteLength,
                role,
                text,
                query,
                tokens);
        }
    }

    private bool SearchUnindexed(
        string query,
        IReadOnlyList<string> tokens,
        Dictionary<string, CandidateSession> bySession,
        DateTime deadlineUtc,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_dbPath)) return false;
        using var connection = OpenConnection(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, source_path, tool
            FROM files
            WHERE complete = 0
            ORDER BY observed_last_write_ticks DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadlineUtc) return false;
            var sessionId = reader.GetString(0);
            var path = reader.GetString(1);
            var tool = reader.GetString(2);
            if (!File.Exists(path)) continue;
            if (!ScanFile(
                    path,
                    tool,
                    query,
                    tokens,
                    deadlineUtc,
                    cancellationToken,
                    out var match))
                continue;
            AddCandidate(
                bySession,
                sessionId,
                path,
                match.ByteStart,
                match.ByteLength,
                match.Role,
                match.Text,
                query,
                tokens);
        }
        return true;
    }

    private static bool ScanFile(
        string path,
        string tool,
        string query,
        IReadOnlyList<string> tokens,
        DateTime deadlineUtc,
        CancellationToken cancellationToken,
        out ScannedMatch match)
    {
        match = default;
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bestScore = int.MinValue;
        try
        {
            using var stream = OpenTranscript(path);
            foreach (var line in ReadLines(stream, 0, stream.Length, includeFinalLine: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadlineUtc) return false;
                var turns = ExtractTurns(line.Bytes, tool);
                for (var ordinal = 0; ordinal < turns.Count; ordinal++)
                {
                    var turn = turns[ordinal];
                    var lower = turn.Text.ToLowerInvariant();
                    var local = tokens.Where(token => lower.Contains(token)).ToList();
                    if (local.Count == 0) continue;
                    foreach (var token in local) matched.Add(token);
                    var score = local.Count * 1000 + RoleScore(turn.Role);
                    if (Normalize(lower).Contains(Normalize(query.ToLowerInvariant())))
                        score += 500;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    match = new ScannedMatch(
                        line.Start,
                        line.ContentLength,
                        turn.Role,
                        turn.Text);
                }
                if (matched.Count == tokens.Count && bestScore > int.MinValue) return true;
            }
        }
        catch
        {
            return false;
        }
        return matched.Count == tokens.Count && bestScore > int.MinValue;
    }

    private static void AddCandidate(
        Dictionary<string, CandidateSession> bySession,
        string sessionId,
        string path,
        long byteStart,
        long byteLength,
        string role,
        string text,
        string query,
        IReadOnlyList<string> tokens)
    {
        var lower = text.ToLowerInvariant();
        var localTokens = tokens.Where(token => lower.Contains(token)).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (localTokens.Count == 0) return;
        if (!bySession.TryGetValue(sessionId, out var candidate))
        {
            candidate = new CandidateSession(sessionId);
            bySession[sessionId] = candidate;
        }
        candidate.MatchedTokens.UnionWith(localTokens);
        var exact = Normalize(lower).Contains(Normalize(query.ToLowerInvariant()));
        var rowScore = localTokens.Count * 1000 + RoleScore(role) + (exact ? 500 : 0);
        if (rowScore <= candidate.BestRowScore) return;
        candidate.BestRowScore = rowScore;
        candidate.Path = path;
        candidate.ByteStart = byteStart;
        candidate.ByteLength = byteLength;
        candidate.Role = role;
        candidate.Snippet = BuildSnippet(text, tokens);
        candidate.ExactPhrase = exact;
    }

    private static int RoleScore(string role) => role switch
    {
        "user" => 300,
        "assistant" => 100,
        _ => 0,
    };

    private static string BuildSnippet(string text, IReadOnlyList<string> tokens)
    {
        var index = tokens
            .Select(token => text.IndexOf(token, StringComparison.OrdinalIgnoreCase))
            .Where(value => value >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, index - 80);
        var length = Math.Min(320, text.Length - start);
        var snippet = Normalize(text.Substring(start, length));
        return (start > 0 ? "..." : "") + snippet + (start + length < text.Length ? "..." : "");
    }

    private FileIndexOutcome IndexFile(
        SqliteConnection connection,
        TranscriptFile file,
        long startOffset,
        bool rebuild,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        if (rebuild) DeleteFileTurns(connection, transaction, file.Path);
        var nextOrdinal = rebuild ? 0 : NextTurnOrdinal(connection, transaction, file.Path);
        var sessionId = file.SessionId;
        var title = file.Title;
        var lastCompleteOffset = startOffset;
        var inserted = 0;
        using var stream = OpenTranscript(file.Path);
        var snapshotLength = stream.Length;
        var includeFinalLine = DateTime.UtcNow - new DateTime(file.LastWriteTicks, DateTimeKind.Utc)
            > LiveFileWindow;
        foreach (var line in ReadLines(
                     stream,
                     startOffset,
                     snapshotLength,
                     includeFinalLine))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = ExtractMetadata(line.Bytes, file.Tool);
            if (!string.IsNullOrWhiteSpace(metadata.SessionId)) sessionId = metadata.SessionId;
            if (title == file.DefaultTitle
                && !string.IsNullOrWhiteSpace(metadata.Title))
                title = metadata.Title;
            var turns = ExtractTurns(line.Bytes, file.Tool);
            for (var blockOrdinal = 0; blockOrdinal < turns.Count; blockOrdinal++)
            {
                var turn = turns[blockOrdinal];
                if (string.IsNullOrWhiteSpace(turn.Text)) continue;
                InsertTurn(
                    connection,
                    transaction,
                    sessionId,
                    file.Path,
                    line.Start,
                    line.ContentLength,
                    blockOrdinal,
                    nextOrdinal++,
                    turn);
                inserted++;
                if (title == file.DefaultTitle && turn.Role == "user")
                    title = TitleFromText(turn.Text, file.DefaultTitle);
            }
            if (line.Complete) lastCompleteOffset = line.End;
        }

        var tailHash = ComputeTailHash(file.Path, lastCompleteOffset);
        UpsertFileState(
            connection,
            transaction,
            file,
            sessionId,
            title,
            lastCompleteOffset,
            snapshotLength,
            tailHash,
            complete: lastCompleteOffset >= snapshotLength);
        transaction.Commit();
        return new FileIndexOutcome(lastCompleteOffset, inserted);
    }

    private static void InsertTurn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string path,
        long byteStart,
        long byteLength,
        int blockOrdinal,
        int turnOrdinal,
        IndexedTurn turn)
    {
        using var row = connection.CreateCommand();
        row.Transaction = transaction;
        row.CommandText = """
            INSERT INTO turns(
                session_id,
                source_path,
                byte_start,
                byte_length,
                block_ordinal,
                turn_ordinal,
                role,
                timestamp)
            VALUES(
                $session,
                $path,
                $start,
                $length,
                $block,
                $turn,
                $role,
                $timestamp);
            SELECT last_insert_rowid();
            """;
        row.Parameters.AddWithValue("$session", sessionId);
        row.Parameters.AddWithValue("$path", path);
        row.Parameters.AddWithValue("$start", byteStart);
        row.Parameters.AddWithValue("$length", byteLength);
        row.Parameters.AddWithValue("$block", blockOrdinal);
        row.Parameters.AddWithValue("$turn", turnOrdinal);
        row.Parameters.AddWithValue("$role", turn.Role);
        row.Parameters.AddWithValue("$timestamp", turn.Timestamp);
        var rowId = (long)(row.ExecuteScalar()
            ?? throw new InvalidOperationException("No turn row id."));

        using var fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = """
            INSERT INTO turn_fts(rowid, user_text, assistant_text, tool_text)
            VALUES($rowid, $user, $assistant, $tool);
            """;
        fts.Parameters.AddWithValue("$rowid", rowId);
        fts.Parameters.AddWithValue("$user", turn.Role == "user" ? turn.Text : "");
        fts.Parameters.AddWithValue("$assistant", turn.Role == "assistant" ? turn.Text : "");
        fts.Parameters.AddWithValue("$tool", turn.Role == "tool" ? turn.Text : "");
        fts.ExecuteNonQuery();
    }

    private static string ReadTurnText(
        string path,
        long byteStart,
        long byteLength,
        int blockOrdinal,
        string role,
        string tool)
    {
        try
        {
            using var stream = OpenTranscript(path);
            stream.Position = byteStart;
            var length = checked((int)Math.Min(byteLength, 256L * 1024 * 1024));
            var bytes = new byte[length];
            var read = 0;
            while (read < length)
            {
                var count = stream.Read(bytes, read, length - read);
                if (count == 0) break;
                read += count;
            }
            var turns = ExtractTurns(bytes.AsMemory(0, read), tool);
            if (blockOrdinal >= 0
                && blockOrdinal < turns.Count
                && turns[blockOrdinal].Role == role)
                return turns[blockOrdinal].Text;
            return turns.FirstOrDefault(turn => turn.Role == role)?.Text ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static IReadOnlyList<IndexedTurn> ExtractTurns(
        ReadOnlyMemory<byte> json,
        string tool)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase)
                ? ExtractClaudeTurns(document.RootElement)
                : ExtractCodexTurns(document.RootElement);
        }
        catch
        {
            var decoded = Encoding.UTF8.GetString(json.Span);
            return string.IsNullOrWhiteSpace(decoded)
                ? Array.Empty<IndexedTurn>()
                : ChunkFallback(decoded);
        }
    }

    private static IReadOnlyList<IndexedTurn> ExtractCodexTurns(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)) return Array.Empty<IndexedTurn>();
        var rootType = StringProperty(root, "type");
        var type = StringProperty(payload, "type");
        var timestamp = StringProperty(root, "timestamp");
        if (rootType == "event_msg" && type is "user_message" or "agent_message")
        {
            var text = ElementText(payload, "message");
            if (text.Length == 0) return Array.Empty<IndexedTurn>();
            return new[]
            {
                new IndexedTurn(
                    type == "user_message" ? "user" : "assistant",
                    text,
                    timestamp),
            };
        }
        if (rootType != "response_item") return Array.Empty<IndexedTurn>();
        if (type == "function_call")
        {
            var text = string.Join(
                "\n",
                new[]
                {
                    StringProperty(payload, "name"),
                    ElementText(payload, "arguments"),
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return text.Length == 0
                ? Array.Empty<IndexedTurn>()
                : new[] { new IndexedTurn("tool", text, timestamp) };
        }
        if (type == "function_call_output")
        {
            var text = ElementText(payload, "output");
            return text.Length == 0
                ? Array.Empty<IndexedTurn>()
                : new[] { new IndexedTurn("tool", text, timestamp) };
        }
        if (type == "message")
        {
            var role = NormalizeRole(StringProperty(payload, "role"));
            return ContentTexts(payload, "content")
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => new IndexedTurn(role, text, timestamp))
                .ToList();
        }
        return Array.Empty<IndexedTurn>();
    }

    private static IReadOnlyList<IndexedTurn> ExtractClaudeTurns(JsonElement root)
    {
        var rootType = StringProperty(root, "type");
        if (rootType is not "user" and not "assistant") return Array.Empty<IndexedTurn>();
        if (!root.TryGetProperty("message", out var message)) return Array.Empty<IndexedTurn>();
        var role = NormalizeRole(StringProperty(message, "role"));
        if (role == "tool") role = NormalizeRole(rootType);
        var timestamp = StringProperty(root, "timestamp");
        if (!message.TryGetProperty("content", out var content)) return Array.Empty<IndexedTurn>();
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? "";
            return text.Length == 0
                ? Array.Empty<IndexedTurn>()
                : new[] { new IndexedTurn(role, text, timestamp) };
        }
        if (content.ValueKind != JsonValueKind.Array) return Array.Empty<IndexedTurn>();
        var turns = new List<IndexedTurn>();
        var textBuilder = new StringBuilder();
        void Flush()
        {
            if (textBuilder.Length == 0) return;
            turns.Add(new IndexedTurn(role, textBuilder.ToString(), timestamp));
            textBuilder.Clear();
        }
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                textBuilder.AppendLine(block.GetString());
                continue;
            }
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = StringProperty(block, "type");
            if (type == "text")
            {
                textBuilder.AppendLine(StringProperty(block, "text"));
            }
            else if (type == "tool_use")
            {
                Flush();
                var toolText = string.Join(
                    "\n",
                    new[]
                    {
                        StringProperty(block, "name"),
                        block.TryGetProperty("input", out var input) ? input.ToString() : "",
                    }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (toolText.Length > 0)
                    turns.Add(new IndexedTurn("tool", toolText, timestamp));
            }
            else if (type == "tool_result")
            {
                Flush();
                foreach (var output in ContentTexts(block, "content"))
                    if (!string.IsNullOrWhiteSpace(output))
                        turns.Add(new IndexedTurn("tool", output, timestamp));
            }
        }
        Flush();
        return turns;
    }

    private static IReadOnlyList<IndexedTurn> ChunkFallback(string decoded)
    {
        const int chunkChars = 64 * 1024;
        var turns = new List<IndexedTurn>();
        for (var offset = 0; offset < decoded.Length; offset += chunkChars)
        {
            var length = Math.Min(chunkChars, decoded.Length - offset);
            turns.Add(new IndexedTurn("tool", decoded.Substring(offset, length), ""));
        }
        return turns;
    }

    private static FileMetadata ExtractMetadata(ReadOnlyMemory<byte> json, string tool)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("message", out var message))
                    return default;
                var role = NormalizeRole(StringProperty(message, "role"));
                if (role != "user") return default;
                var title = ContentTexts(message, "content").FirstOrDefault() ?? "";
                return new FileMetadata("", TitleFromText(title, ""));
            }
            if (StringProperty(root, "type") == "session_meta"
                && root.TryGetProperty("payload", out var payload))
            {
                var id = StringProperty(payload, "id");
                if (id.Length == 0) id = StringProperty(payload, "session_id");
                return new FileMetadata(id, "");
            }
            if (root.TryGetProperty("payload", out var eventPayload)
                && StringProperty(root, "type") == "event_msg"
                && StringProperty(eventPayload, "type") == "user_message")
            {
                return new FileMetadata(
                    "",
                    TitleFromText(ElementText(eventPayload, "message"), ""));
            }
        }
        catch
        {
        }
        return default;
    }

    private static IEnumerable<string> ContentTexts(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var content)) yield break;
        if (content.ValueKind == JsonValueKind.String)
        {
            yield return content.GetString() ?? "";
            yield break;
        }
        if (content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                yield return block.GetString() ?? "";
                continue;
            }
            if (block.ValueKind != JsonValueKind.Object) continue;
            var text = StringProperty(block, "text");
            if (text.Length == 0) text = StringProperty(block, "input_text");
            if (text.Length == 0) text = StringProperty(block, "output_text");
            if (text.Length > 0) yield return text;
            else if (block.TryGetProperty("content", out var nested))
            {
                if (nested.ValueKind == JsonValueKind.String)
                    yield return nested.GetString() ?? "";
                else if (nested.ValueKind == JsonValueKind.Array)
                    foreach (var child in nested.EnumerateArray())
                        if (child.ValueKind == JsonValueKind.Object
                            && StringProperty(child, "text") is { Length: > 0 } childText)
                            yield return childText;
            }
        }
    }

    private static string ElementText(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            JsonValueKind.Object or JsonValueKind.Array => value.ToString(),
            _ => "",
        };
    }

    private static string StringProperty(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string NormalizeRole(string role) =>
        role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "user"
        : role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant"
        : "tool";

    private static IReadOnlyList<string> QueryTokens(string query) =>
        TokenRegex().Matches((query ?? "").ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();

    private static string BuildFtsQuery(IEnumerable<string> tokens) =>
        string.Join(
            " AND ",
            tokens.Select(token => "\"" + token.Replace("\"", "\"\"") + "\""));

    private static string Normalize(string text) =>
        WhitespaceRegex().Replace(text ?? "", " ").Trim();

    private static string TitleFromText(string text, string fallback)
    {
        var title = Normalize(text);
        if (title.Length == 0) return fallback;
        return title.Length <= 160 ? title : title[..160];
    }

    private static FileStream OpenTranscript(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        1024 * 1024,
        FileOptions.SequentialScan);

    private static IEnumerable<RawJsonLine> ReadLines(
        FileStream stream,
        long startOffset,
        long snapshotLength,
        bool includeFinalLine)
    {
        stream.Position = Math.Clamp(startOffset, 0, snapshotLength);
        var buffer = new byte[1024 * 1024];
        using var line = new MemoryStream();
        var lineStart = stream.Position;
        while (stream.Position < snapshotLength)
        {
            var requested = (int)Math.Min(buffer.Length, snapshotLength - stream.Position);
            var read = stream.Read(buffer, 0, requested);
            if (read <= 0) break;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == '\n')
                {
                    var bytes = line.ToArray();
                    var contentLength = bytes.Length;
                    if (contentLength > 0 && bytes[^1] == '\r') contentLength--;
                    yield return new RawJsonLine(
                        lineStart,
                        lineStart + bytes.Length + 1,
                        bytes.AsMemory(0, contentLength),
                        contentLength,
                        true);
                    line.SetLength(0);
                    lineStart = stream.Position - read + index + 1;
                }
                else
                {
                    line.WriteByte(value);
                }
            }
        }
        if (includeFinalLine && line.Length > 0)
        {
            var bytes = line.ToArray();
            var contentLength = bytes.Length;
            if (contentLength > 0 && bytes[^1] == '\r') contentLength--;
            yield return new RawJsonLine(
                lineStart,
                snapshotLength,
                bytes.AsMemory(0, contentLength),
                contentLength,
                false);
        }
    }

    private static string ComputeTailHash(string path, long length)
    {
        if (length <= 0) return "";
        using var stream = OpenTranscript(path);
        var count = (int)Math.Min(TailHashBytes, length);
        stream.Position = length - count;
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(bytes, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, read)));
    }

    private static bool IsUnchanged(TranscriptFile file, FileState? state) =>
        state is not null
        && state.IndexVersion == SchemaVersion
        && state.LastWriteTicks == file.LastWriteTicks
        && state.ObservedLength == file.Length
        && state.IndexedLength == file.Length
        && state.Complete;

    private static bool CanAppend(TranscriptFile file, FileState? state)
    {
        if (state is null
            || state.IndexVersion != SchemaVersion
            || file.Length <= state.IndexedLength
            || state.IndexedLength < 0)
            return false;
        try
        {
            return string.Equals(
                ComputeTailHash(file.Path, state.IndexedLength),
                state.TailHash,
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<TranscriptFile> EnumerateTranscriptFiles(
        IReadOnlyList<SessionSource> sources,
        IReadOnlyDictionary<string, ArchiveSession> known)
    {
        var files = new Dictionary<string, TranscriptFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (!source.Enabled
                || string.IsNullOrWhiteSpace(source.Root)
                || !Directory.Exists(source.Root))
                continue;
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(
                    source.Root,
                    "*.jsonl",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip =
                            FileAttributes.Hidden
                            | FileAttributes.System
                            | FileAttributes.ReparsePoint,
                    });
            }
            catch
            {
                continue;
            }
            foreach (var path in paths)
            {
                try
                {
                    var full = FullPath(path);
                    if (full.Contains(
                            $"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            Path.GetFileName(full),
                            "journal.jsonl",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    var info = new FileInfo(full);
                    known.TryGetValue(full, out var session);
                    var tool = session?.Tool;
                    if (string.IsNullOrWhiteSpace(tool)) tool = source.Tool;
                    if (string.IsNullOrWhiteSpace(tool) || tool == "auto")
                        tool = ToolFromPath(full);
                    var defaultTitle = Path.GetFileNameWithoutExtension(full);
                    files[full] = new TranscriptFile(
                        full,
                        session?.Id ?? IdFromPath(full, tool),
                        session?.DisplayTitle ?? defaultTitle,
                        defaultTitle,
                        tool.ToLowerInvariant(),
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        info.LastWriteTimeUtc.ToString("O"));
                }
                catch
                {
                }
            }
        }
        return files.Values.ToList();
    }

    private static string IdFromPath(string path, string tool)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase))
            return name;
        if (name.Length >= 36)
        {
            var suffix = name[^36..];
            if (Guid.TryParse(suffix, out _)) return suffix;
        }
        return name;
    }

    private static string ToolFromPath(string path) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "claude"
            : "codex";

    private static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private SqliteConnection OpenConnection(bool readOnly = false)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
            }.ToString());
        connection.Open();
        if (!readOnly)
        {
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Execute(connection, "PRAGMA temp_store=MEMORY;");
            Execute(connection, "PRAGMA cache_size=-65536;");
            Execute(connection, "PRAGMA mmap_size=268435456;");
        }
        return connection;
    }

    private static void Initialize(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS files(
                source_path TEXT PRIMARY KEY COLLATE NOCASE,
                session_id TEXT NOT NULL,
                title TEXT NOT NULL,
                tool TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_write_ticks INTEGER NOT NULL,
                observed_last_write_ticks INTEGER NOT NULL,
                indexed_length INTEGER NOT NULL,
                observed_length INTEGER NOT NULL,
                tail_hash TEXT NOT NULL,
                index_version INTEGER NOT NULL,
                complete INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS files_session_id ON files(session_id);
            CREATE TABLE IF NOT EXISTS turns(
                id INTEGER PRIMARY KEY,
                session_id TEXT NOT NULL,
                source_path TEXT NOT NULL COLLATE NOCASE,
                byte_start INTEGER NOT NULL,
                byte_length INTEGER NOT NULL,
                block_ordinal INTEGER NOT NULL,
                turn_ordinal INTEGER NOT NULL,
                role TEXT NOT NULL,
                timestamp TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS turns_source_path ON turns(source_path);
            CREATE INDEX IF NOT EXISTS turns_session_id ON turns(session_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS turn_fts USING fts5(
                user_text,
                assistant_text,
                tool_text,
                content='',
                contentless_delete=1,
                tokenize='unicode61 remove_diacritics 2'
            );
            """);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta(key, value)
            VALUES('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, FileState> LoadFileStates(SqliteConnection connection)
    {
        var states = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                source_path,
                last_write_ticks,
                indexed_length,
                observed_length,
                tail_hash,
                index_version,
                complete
            FROM files;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            states[reader.GetString(0)] = new FileState(
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6) != 0);
        }
        return states;
    }

    private static void RegisterPendingFiles(
        SqliteConnection connection,
        IReadOnlyList<TranscriptFile> files,
        IReadOnlyDictionary<string, FileState> states)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var file in files)
        {
            if (states.ContainsKey(file.Path))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE files
                    SET
                        session_id = $session,
                        title = $title,
                        tool = $tool,
                        updated_at = $updated,
                        observed_last_write_ticks = $ticks,
                        observed_length = $length
                    WHERE source_path = $path;
                    """;
                BindFile(update, file);
                update.ExecuteNonQuery();
                continue;
            }
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO files(
                    source_path,
                    session_id,
                    title,
                    tool,
                    updated_at,
                    last_write_ticks,
                    observed_last_write_ticks,
                    indexed_length,
                    observed_length,
                    tail_hash,
                    index_version,
                    complete)
                VALUES(
                    $path,
                    $session,
                    $title,
                    $tool,
                    $updated,
                    0,
                    $ticks,
                    0,
                    $length,
                    '',
                    $version,
                    0);
                """;
            BindFile(insert, file);
            insert.Parameters.AddWithValue("$version", SchemaVersion);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void RemoveMissingFiles(
        SqliteConnection connection,
        IReadOnlyList<TranscriptFile> files,
        IReadOnlyDictionary<string, FileState> states)
    {
        var current = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in states.Keys.Where(path => !current.Contains(path)).ToList())
        {
            using var transaction = connection.BeginTransaction();
            DeleteFileTurns(connection, transaction, path);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM files WHERE source_path = $path;";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static void DeleteFileTurns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path)
    {
        using var fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = """
            DELETE FROM turn_fts
            WHERE rowid IN (
                SELECT id FROM turns WHERE source_path = $path
            );
            """;
        fts.Parameters.AddWithValue("$path", path);
        fts.ExecuteNonQuery();

        using var rows = connection.CreateCommand();
        rows.Transaction = transaction;
        rows.CommandText = "DELETE FROM turns WHERE source_path = $path;";
        rows.Parameters.AddWithValue("$path", path);
        rows.ExecuteNonQuery();
    }

    private static int NextTurnOrdinal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(turn_ordinal) + 1, 0)
            FROM turns
            WHERE source_path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void UpsertFileState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TranscriptFile file,
        string sessionId,
        string title,
        long indexedLength,
        long observedLength,
        string tailHash,
        bool complete)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE files
            SET
                session_id = $session,
                title = $title,
                tool = $tool,
                updated_at = $updated,
                last_write_ticks = $ticks,
                observed_last_write_ticks = $ticks,
                indexed_length = $indexed,
                observed_length = $observed,
                tail_hash = $hash,
                index_version = $version,
                complete = $complete
            WHERE source_path = $path;
            """;
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$tool", file.Tool);
        command.Parameters.AddWithValue("$updated", file.UpdatedAt);
        command.Parameters.AddWithValue("$ticks", file.LastWriteTicks);
        command.Parameters.AddWithValue("$indexed", indexedLength);
        command.Parameters.AddWithValue("$observed", observedLength);
        command.Parameters.AddWithValue("$hash", tailHash);
        command.Parameters.AddWithValue("$version", SchemaVersion);
        command.Parameters.AddWithValue("$complete", complete ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void BindFile(SqliteCommand command, TranscriptFile file)
    {
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$session", file.SessionId);
        command.Parameters.AddWithValue("$title", file.Title);
        command.Parameters.AddWithValue("$tool", file.Tool);
        command.Parameters.AddWithValue("$updated", file.UpdatedAt);
        command.Parameters.AddWithValue("$ticks", file.LastWriteTicks);
        command.Parameters.AddWithValue("$length", file.Length);
    }

    private static SearchCoverage Coverage(
        TranscriptSearchIndexStatus status,
        bool exhaustiveFallbackComplete)
    {
        var complete = status.Complete || exhaustiveFallbackComplete;
        var message = complete
            ? $"Searched all {status.TotalFiles:N0} indexed transcript files."
            : $"Search is partial: {status.IndexedFiles:N0} of {status.TotalFiles:N0} transcript files indexed.";
        return new SearchCoverage(
            complete,
            status.IndexedFiles,
            status.TotalFiles,
            status.IndexedBytes,
            status.TotalBytes,
            exhaustiveFallbackComplete,
            message);
    }

    private void SetStatus(
        TranscriptSearchIndexStatus status,
        IProgress<TranscriptSearchIndexStatus>? progress)
    {
        lock (_statusGate) _status = status;
        progress?.Report(status);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record TranscriptFile(
        string Path,
        string SessionId,
        string Title,
        string DefaultTitle,
        string Tool,
        long Length,
        long LastWriteTicks,
        string UpdatedAt);

    private sealed record FileState(
        long LastWriteTicks,
        long IndexedLength,
        long ObservedLength,
        string TailHash,
        int IndexVersion,
        bool Complete);

    private sealed record IndexedTurn(string Role, string Text, string Timestamp);
    private sealed record FileIndexOutcome(long IndexedLength, int InsertedTurns);
    private readonly record struct FileMetadata(string SessionId, string Title);
    private readonly record struct ScannedMatch(
        long ByteStart,
        long ByteLength,
        string Role,
        string Text);

    private readonly record struct RawJsonLine(
        long Start,
        long End,
        ReadOnlyMemory<byte> Bytes,
        int ContentLength,
        bool Complete);

    private sealed class CandidateSession(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public HashSet<string> MatchedTokens { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public int BestRowScore { get; set; } = int.MinValue;
        public string Path { get; set; } = "";
        public long ByteStart { get; set; }
        public long ByteLength { get; set; }
        public string Role { get; set; } = "";
        public string Snippet { get; set; } = "";
        public bool ExactPhrase { get; set; }

        public ArchiveSearchHit ToHit(ArchiveSession? session)
        {
            if (session is null) return new ArchiveSearchHit();
            var score = MatchedTokens.Count * 1000
                + RoleScore(Role)
                + (ExactPhrase ? 500 : 0);
            return new ArchiveSearchHit
            {
                Session = session,
                Snippet = Snippet,
                SourceLabel = Role switch
                {
                    "user" => "your message",
                    "assistant" => "assistant message",
                    _ => "tool output",
                },
                MatchedTerms = string.Join(", ", MatchedTokens.OrderBy(value => value)),
                Score = score,
                ByteOffset = ByteStart,
                ByteLength = ByteLength,
                Provenance = Role,
                Navigable = File.Exists(Path),
            };
        }
    }
}
