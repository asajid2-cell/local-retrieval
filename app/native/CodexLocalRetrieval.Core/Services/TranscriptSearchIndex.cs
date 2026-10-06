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

    // The `files` table is the index's memory of what it has already read: ~9.4k rows and ~3 MB of
    // payload live. Reading it whole on every sync was a per-cycle read burst, so the states are held
    // here instead -- materialised once per process and then kept in step by every write below (register,
    // remove, upsert). _totalFiles/_totalBytes are the coverage totals that a per-pass COUNT/SUM used to
    // scan for; they move with the same writes and are reconciled from the map on a full pass. Both are
    // guarded by _syncGate, which SyncAsync holds for the whole body.
    private Dictionary<string, FileState>? _fileStates;
    private int _totalFiles;
    private long _totalBytes;

    // Test seams: the rows (and payload bytes) a sync materialised from `files`. Only the one-time load
    // reads; an incremental pass in steady state must leave both untouched.
    internal int FileRowsRead;
    internal long FileStateBytesRead;
    // Test seam: how many times the `turns` table was asked for a file's next ordinal. The ordinal is
    // cached on the file state, so repeated appends to one file seed it once and then read the cache.
    internal int OrdinalQueries;
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
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? changedPaths = null,
        bool forceFullScan = false)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // A null path set means "walk everything" (startup, overflow, the reconciliation beat). A
            // non-null set is the watcher's dirty list: only those files are looked at, so the full
            // EnumerateTranscriptFiles walk -- every source tree -- runs on the full passes alone.
            var full = forceFullScan || changedPaths is null;
            if (!full && changedPaths!.Count == 0)
            {
                // No change reported: no enumeration, no transaction, no write at all.
                return new TranscriptSearchSyncResult(0, 0, 0, 0, 0, stopwatch.Elapsed);
            }

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

            var enumWatch = Stopwatch.StartNew();
            var files = (full
                    ? EnumerateTranscriptFiles(sources, known)
                    : BuildTranscriptFiles(changedPaths!, sources, known))
                .OrderByDescending(file => file.LastWriteTicks)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var enumMs = enumWatch.ElapsedMilliseconds;

            var statesWatch = Stopwatch.StartNew();
            EnsureFileStates(connection);
            var states = _fileStates!;
            var statesMs = statesWatch.ElapsedMilliseconds;

            // A full pass reports its walk; an incremental pass reports the running totals the register/
            // remove/upsert writes below keep current.
            int totalFiles;
            long totalBytes;
            if (full)
            {
                totalFiles = files.Count;
                totalBytes = files.Sum(file => file.Length);
            }
            else
            {
                totalFiles = _totalFiles;
                totalBytes = _totalBytes;
            }
            SetStatus(new TranscriptSearchIndexStatus(
                true,
                false,
                0,
                totalFiles,
                0,
                totalBytes,
                "",
                "",
                DateTimeOffset.UtcNow), progress);

            // ONE transaction for a dirty-set pass: the metadata rows and every appended turn commit
            // together, so a cycle costs one WAL flush instead of one per file. A full pass leaves it null
            // so each file commits on its own (see IndexFile) and no single transaction spans the whole index.
            using var batch = full ? null : connection.BeginTransaction();

            var registerWatch = Stopwatch.StartNew();
            RegisterPendingFiles(connection, batch, files, states);
            var registerMs = registerWatch.ElapsedMilliseconds;

            var removeMs = 0L;
            if (full)
            {
                var removeWatch = Stopwatch.StartNew();
                RemoveMissingFiles(connection, files, states);
                removeMs = removeWatch.ElapsedMilliseconds;
            }

            var indexedFiles = 0;
            var indexedBytes = 0L;
            var rebuilt = 0;
            var appended = 0;
            var unchanged = 0;
            var indexWatch = Stopwatch.StartNew();
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
                    batch,
                    file,
                    states,
                    startOffset,
                    rebuild: !append,
                    cancellationToken);
                if (append) appended++;
                else rebuilt++;
                indexedFiles++;
                indexedBytes += outcome.IndexedLength;
            }
            batch?.Commit();
            var indexMs = indexWatch.ElapsedMilliseconds;

            // WAL housekeeping after the write, never inside it: a PASSIVE checkpoint folds what it can
            // into the main DB and returns at once, and journal_size_limit truncates the log file back to
            // its bound, so a burst of writes cannot leave a multi-hundred-MB WAL behind.
            TryCheckpoint(connection);

            // A full pass is the reconciliation beat: it saw every file, so the totals are recomputed from
            // the (now complete) map rather than accumulated -- the same count/sum the old per-pass scan
            // produced, with no read of the table.
            if (full)
            {
                _totalFiles = states.Count;
                _totalBytes = states.Values.Sum(state => state.ObservedLength);
            }

            var complete = indexedFiles == files.Count;
            var finalStatus = new TranscriptSearchIndexStatus(
                false,
                complete,
                indexedFiles,
                _totalFiles,
                indexedBytes,
                _totalBytes,
                "",
                "",
                DateTimeOffset.UtcNow);
            SetStatus(finalStatus, progress);
            stopwatch.Stop();
            PerfCounters.Trace?.Invoke(
                $"index sync full={full} files={files.Count} appended={appended} rebuilt={rebuilt}"
                + $" unchanged={unchanged} enumMs={enumMs} statesMs={statesMs} registerMs={registerMs}"
                + $" removeMs={removeMs} indexMs={indexMs} totalMs={stopwatch.ElapsedMilliseconds}"
                + $" wal={WalBytes():F0}MB");
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
        SqliteTransaction? batch,
        TranscriptFile file,
        Dictionary<string, FileState> states,
        long startOffset,
        bool rebuild,
        CancellationToken cancellationToken)
    {
        // The append path passes one transaction for the whole cycle (one commit, one WAL flush). A full
        // rebuild -- which can touch every transcript -- passes null so each file commits on its own and a
        // single transaction never grows to the size of the entire index.
        var owns = batch is null;
        var transaction = batch ?? connection.BeginTransaction();
        try
        {
            if (rebuild) DeleteFileTurns(connection, transaction, file.Path);
            // The next ordinal is stable between appends to one file, so it is cached on the file state
            // (see FileState.NextTurnOrdinal): the first append in a process pays one query, later appends
            // read the cache. A rebuild starts over at 0 and stores the fresh count below.
            var nextOrdinal = rebuild
                ? 0
                : states[file.Path].NextTurnOrdinal ?? NextTurnOrdinal(connection, transaction, file.Path);
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
                states,
                sessionId,
                title,
                lastCompleteOffset,
                snapshotLength,
                tailHash,
                nextTurnOrdinal: nextOrdinal,
                complete: lastCompleteOffset >= snapshotLength);
            if (owns) transaction.Commit();
            return new FileIndexOutcome(lastCompleteOffset, inserted);
        }
        finally
        {
            if (owns) transaction.Dispose();
        }
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
        // An append needs an indexed prefix to extend. A row that has never been indexed carries
        // indexed_length 0 and tail_hash ''; ComputeTailHash returns "" for offset 0, which would falsely
        // "match" that empty hash. Zero is therefore a rebuild, not an append.
        if (state is null
            || state.IndexVersion != SchemaVersion
            || state.IndexedLength <= 0
            || file.Length <= state.IndexedLength)
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
                    var file = BuildTranscriptFile(full, source, known);
                    if (file is not null) files[full] = file;
                }
                catch
                {
                }
            }
        }
        return files.Values.ToList();
    }

    // The same file construction the full walk uses, for one explicit path. Returns null for the Claude
    // workflow/subagent journals that collide on the filename "journal" and are not chats.
    private static TranscriptFile? BuildTranscriptFile(
        string full,
        SessionSource source,
        IReadOnlyDictionary<string, ArchiveSession> known)
    {
        if (full.Contains(
                $"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Path.GetFileName(full),
                "journal.jsonl",
                StringComparison.OrdinalIgnoreCase))
            return null;
        var info = new FileInfo(full);
        known.TryGetValue(full, out var session);
        var tool = session?.Tool;
        if (string.IsNullOrWhiteSpace(tool)) tool = source.Tool;
        if (string.IsNullOrWhiteSpace(tool) || tool == "auto")
            tool = ToolFromPath(full);
        var defaultTitle = Path.GetFileNameWithoutExtension(full);
        return new TranscriptFile(
            full,
            session?.Id ?? IdFromPath(full, tool),
            session?.DisplayTitle ?? defaultTitle,
            defaultTitle,
            tool.ToLowerInvariant(),
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            info.LastWriteTimeUtc.ToString("O"));
    }

    // The dirty-set entry point: build the file list from the paths the watcher reported instead of a
    // directory walk. The tool is resolved from the owning source (longest-root match), exactly as the
    // walk would, so an explicit path indexes identically to one found by enumeration.
    private static IReadOnlyList<TranscriptFile> BuildTranscriptFiles(
        IReadOnlyCollection<string> paths,
        IReadOnlyList<SessionSource> sources,
        IReadOnlyDictionary<string, ArchiveSession> known)
    {
        var files = new Dictionary<string, TranscriptFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                var full = FullPath(path);
                var source = SourceForPath(sources, full);
                if (source is null) continue;
                var file = BuildTranscriptFile(full, source, known);
                if (file is not null) files[full] = file;
            }
            catch
            {
            }
        }
        return files.Values.ToList();
    }

    // Longest-root match, so a nested source (an account sessions dir inside the codex home) wins over its
    // parent and the file is indexed with the tool that owns it.
    private static SessionSource? SourceForPath(IReadOnlyList<SessionSource> sources, string path)
    {
        SessionSource? best = null;
        var bestLength = -1;
        foreach (var source in sources)
        {
            if (!source.Enabled || string.IsNullOrWhiteSpace(source.Root)) continue;
            var root = source.Root.TrimEnd('\\', '/');
            if (root.Length == 0 || path.Length <= root.Length || bestLength >= root.Length) continue;
            if (path[root.Length] is not ('\\' or '/')) continue;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            best = source;
            bestLength = root.Length;
        }
        return best;
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
            // Keep the write-ahead log bounded: auto-checkpoint every ~4 MB of frames, and cap the log
            // file at 64 MB so a checkpoint truncates it back rather than leaving it at its high-water mark.
            Execute(connection, "PRAGMA wal_autocheckpoint=1000;");
            Execute(connection, "PRAGMA journal_size_limit=67108864;");
        }
        return connection;
    }

    // Fold the WAL back into the main DB without ever blocking: PASSIVE checkpoints whatever is not held
    // by a reader and returns immediately, and journal_size_limit then truncates the log file. Run after
    // a write pass, off the request path.
    private static void TryCheckpoint(SqliteConnection connection)
    {
        try { Execute(connection, "PRAGMA wal_checkpoint(PASSIVE);"); }
        catch { }
    }

    // Size of the write-ahead log file, for the sync trace. Zero when it has been folded and removed.
    private double WalBytes()
    {
        try
        {
            var info = new FileInfo(_dbPath + "-wal");
            return info.Exists ? info.Length / 1048576.0 : 0;
        }
        catch { return 0; }
    }

    // Materialise the `files` table once per process. Every later pass works from the in-memory map, so
    // this whole-table read happens once rather than on every cycle.
    private void EnsureFileStates(SqliteConnection connection)
    {
        if (_fileStates is not null) return;
        var states = LoadFileStates(connection);
        _fileStates = states;
        _totalFiles = states.Count;
        _totalBytes = states.Values.Sum(state => state.ObservedLength);
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
        // Conditional upsert: on every sync but the first the version already matches, and the WHERE keeps
        // the statement from writing a row (and so a WAL frame) just because the sync ran.
        command.CommandText = """
            INSERT INTO meta(key, value)
            VALUES('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            WHERE meta.value <> excluded.value;
            """;
        command.Parameters.AddWithValue("$version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private Dictionary<string, FileState> LoadFileStates(SqliteConnection connection)
    {
        var states = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
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
                complete
            FROM files;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            var sessionId = reader.GetString(1);
            var title = reader.GetString(2);
            var tool = reader.GetString(3);
            var updatedAt = reader.GetString(4);
            var tailHash = reader.GetString(9);
            states[path] = new FileState(
                sessionId,
                title,
                tool,
                updatedAt,
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                tailHash,
                reader.GetInt32(10),
                reader.GetInt32(11) != 0);
            FileRowsRead++;
            FileStateBytesRead += path.Length + sessionId.Length + title.Length + tool.Length
                + updatedAt.Length + tailHash.Length + 24;
        }
        return states;
    }

    private void RegisterPendingFiles(
        SqliteConnection connection,
        SqliteTransaction? batch,
        IReadOnlyList<TranscriptFile> files,
        Dictionary<string, FileState> states)
    {
        var owns = batch is null;
        var transaction = batch ?? connection.BeginTransaction();
        try
        {
            foreach (var file in files)
            {
                if (states.TryGetValue(file.Path, out var state))
                {
                    // The observed stamp and the display metadata are the only things this pass can change,
                    // and a file that did not move has neither. Skipping the write is what keeps the DB's
                    // mtime (and the WAL) still on a cycle where nothing changed.
                    if (state.ObservedLastWriteTicks == file.LastWriteTicks
                        && state.ObservedLength == file.Length
                        && string.Equals(state.SessionId, file.SessionId, StringComparison.Ordinal)
                        && string.Equals(state.Title, file.Title, StringComparison.Ordinal)
                        && string.Equals(state.Tool, file.Tool, StringComparison.Ordinal)
                        && string.Equals(state.UpdatedAt, file.UpdatedAt, StringComparison.Ordinal))
                    {
                        continue;
                    }
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
                    _totalBytes += file.Length - state.ObservedLength;
                    states[file.Path] = state with
                    {
                        SessionId = file.SessionId,
                        Title = file.Title,
                        Tool = file.Tool,
                        UpdatedAt = file.UpdatedAt,
                        ObservedLastWriteTicks = file.LastWriteTicks,
                        ObservedLength = file.Length,
                    };
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
                states[file.Path] = new FileState(
                    file.SessionId,
                    file.Title,
                    file.Tool,
                    file.UpdatedAt,
                    0,
                    file.LastWriteTicks,
                    0,
                    file.Length,
                    "",
                    SchemaVersion,
                    false);
                _totalFiles++;
                _totalBytes += file.Length;
            }
            if (owns) transaction.Commit();
        }
        finally
        {
            if (owns) transaction.Dispose();
        }
    }

    private void RemoveMissingFiles(
        SqliteConnection connection,
        IReadOnlyList<TranscriptFile> files,
        Dictionary<string, FileState> states)
    {
        var current = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in states.Keys.Where(path => !current.Contains(path)).ToList())
        {
            var removed = states[path];
            using var transaction = connection.BeginTransaction();
            DeleteFileTurns(connection, transaction, path);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM files WHERE source_path = $path;";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
            transaction.Commit();
            states.Remove(path);
            _totalFiles--;
            _totalBytes -= removed.ObservedLength;
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

    private int NextTurnOrdinal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path)
    {
        OrdinalQueries++;
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

    private void UpsertFileState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TranscriptFile file,
        Dictionary<string, FileState> states,
        string sessionId,
        string title,
        long indexedLength,
        long observedLength,
        string tailHash,
        int nextTurnOrdinal,
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
        // RegisterPendingFiles ran for this file in the same pass, so its entry is always present.
        var previous = states[file.Path];
        _totalBytes += observedLength - previous.ObservedLength;
        states[file.Path] = previous with
        {
            SessionId = sessionId,
            Title = title,
            Tool = file.Tool,
            UpdatedAt = file.UpdatedAt,
            LastWriteTicks = file.LastWriteTicks,
            ObservedLastWriteTicks = file.LastWriteTicks,
            IndexedLength = indexedLength,
            ObservedLength = observedLength,
            TailHash = tailHash,
            IndexVersion = SchemaVersion,
            Complete = complete,
            NextTurnOrdinal = nextTurnOrdinal,
        };
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
        string SessionId,
        string Title,
        string Tool,
        string UpdatedAt,
        long LastWriteTicks,
        long ObservedLastWriteTicks,
        long IndexedLength,
        long ObservedLength,
        string TailHash,
        int IndexVersion,
        bool Complete,
        // The ordinal the next appended turn takes. Null means "not seeded in this process": the next
        // append runs one NextTurnOrdinal query and caches the answer here, and every later append uses
        // the cache. A rebuild or a removal drops the cache, so the next append re-seeds.
        int? NextTurnOrdinal = null);

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
