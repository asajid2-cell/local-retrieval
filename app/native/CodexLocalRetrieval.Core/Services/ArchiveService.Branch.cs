using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Core.Services;

public sealed partial class ArchiveService
{
    private readonly string _codexSessionsRoot;
    private readonly string _claudeSessionsRoot;
    private readonly string _codexStateDbPath;
    private readonly string _templatesRoot;
    private readonly Func<ArchiveSession, string, string, bool> _codexThreadRegistrar;

    public readonly record struct BranchResult(bool Ok, string Message, ArchiveSession? Branch);
    public readonly record struct TemplateSnapshotResult(bool Ok, string Message, TemplateSnapshot? Snapshot);
    public readonly record struct SnapshotReaderResult(bool Ok, string Message, ArchiveSession? Reader);

    public async Task<BranchResult> BranchSessionAsync(
        ArchiveSession parent,
        SessionEventLedger.Options? eventOptions = null)
        => await ForkSessionAsync(parent, NativeLaunchMode, eventOptions);

    public async Task<BranchResult> ForkSessionAsync(
        ArchiveSession parent,
        string launchMode,
        SessionEventLedger.Options? eventOptions = null)
    {
        try
        {
            var result = await BranchSessionCoreAsync(parent, launchMode);
            RecordBranchOperation("branch", parent, result.Ok, result.Message, result.Branch, eventOptions);
            return result;
        }
        catch (Exception ex)
        {
            RecordBranchOperation("branch", parent, false, "Branch failed: " + ex.Message, null, eventOptions);
            throw;
        }
    }

    private async Task<BranchResult> BranchSessionCoreAsync(ArchiveSession parent, string launchMode)
    {
        if (parent is null) return new BranchResult(false, "No chat to branch.", null);
        if (!string.Equals(launchMode, NativeLaunchMode, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(launchMode, GatewayLaunchMode, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(launchMode, DeepSeekLaunchMode, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(launchMode, LunaLaunchMode, StringComparison.OrdinalIgnoreCase))
            return new BranchResult(false, $"Unsupported branch launch mode '{launchMode}'.", null);
        var sourcePath = ResolveSessionSourcePath(parent);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new BranchResult(false, "This chat's transcript file isn't on disk, so it can't be branched.", null);

        return await CreateNativeBranchAsync(
            parent,
            sourcePath,
            string.IsNullOrWhiteSpace(parent.DisplayTitle) ? parent.Id : parent.DisplayTitle,
            parent.Id,
            fromSnapshotId: "",
            launchMode: NormalizeLaunchMode(launchMode));
    }

    public async Task<TemplateSnapshotResult> CreateTemplateSnapshotAsync(
        ArchiveSession source,
        string? name = null,
        string? idempotencyKey = null,
        SessionEventLedger.Options? eventOptions = null)
    {
        try
        {
            var result = await CreateTemplateSnapshotCoreAsync(source, name, idempotencyKey);
            RecordCheckpointOperation("checkpoint.create", source, result.Ok, result.Message, result.Snapshot, eventOptions);
            return result;
        }
        catch (Exception ex)
        {
            RecordCheckpointOperation("checkpoint.create", source, false, "Checkpoint failed: " + ex.Message, null, eventOptions);
            throw;
        }
    }

    private async Task<TemplateSnapshotResult> CreateTemplateSnapshotCoreAsync(
        ArchiveSession source,
        string? name,
        string? idempotencyKey)
    {
        if (source is null) return new TemplateSnapshotResult(false, "No chat to checkpoint.", null);
        var sourcePath = ResolveSessionSourcePath(source);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new TemplateSnapshotResult(
                false,
                "This chat's transcript file isn't on disk. Its checkpoint is still pending and was not cleared.",
                null);

        var stableKey = (idempotencyKey ?? "").Trim();
        if (stableKey.Length > 0)
        {
            var existing = Store.TemplateSnapshots.Values.FirstOrDefault(snapshot =>
                string.Equals(snapshot.SourceSessionId, source.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.IdempotencyKey, stableKey, StringComparison.Ordinal));
            if (existing is not null)
                return new TemplateSnapshotResult(true, $"Checkpoint \"{existing.DisplayName}\" already exists.", existing);
        }

        var snapshotId = stableKey.Length == 0
            ? Guid.NewGuid().ToString("N")
            : DeterministicSnapshotId(source.Id, stableKey);
        if (Store.TemplateSnapshots.TryGetValue(snapshotId, out var byId))
            return new TemplateSnapshotResult(true, $"Checkpoint \"{byId.DisplayName}\" already exists.", byId);

        Directory.CreateDirectory(_templatesRoot);
        var snapshotPath = Path.Combine(_templatesRoot, snapshotId + ".jsonl");
        JsonlCloneResult capture;
        if (File.Exists(snapshotPath))
        {
            var length = new FileInfo(snapshotPath).Length;
            capture = new JsonlCloneResult(length, length, CountCompleteLines(snapshotPath));
        }
        else
        {
            capture = await JsonlTranscriptCloner.CloneAsync(sourcePath, snapshotPath);
        }

        if (capture.LineCount == 0)
        {
            TryDeleteFile(snapshotPath);
            return new TemplateSnapshotResult(false, "The transcript has no complete JSONL records to checkpoint.", null);
        }

        var createdAt = File.GetCreationTimeUtc(snapshotPath);
        if (createdAt.Year < 2000) createdAt = DateTime.UtcNow;
        var title = string.IsNullOrWhiteSpace(source.DisplayTitle) ? source.Id : source.DisplayTitle;
        var snapshot = new TemplateSnapshot
        {
            Id = snapshotId,
            Name = string.IsNullOrWhiteSpace(name)
                ? $"{title} — {createdAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                : name.Trim(),
            SourceSessionId = source.Id,
            SourceTitle = title,
            SourcePath = sourcePath,
            SnapshotPath = snapshotPath,
            Tool = (source.Tool ?? "").Trim().ToLowerInvariant(),
            Workspace = source.Workspace,
            WorkspaceName = source.WorkspaceName,
            Model = source.Model,
            CreatedAt = createdAt.ToString("O"),
            CapturedSourceLength = capture.CapturedSourceLength,
            LineCount = capture.LineCount,
            IdempotencyKey = stableKey
        };
        snapshot.MessageCount = await CountSnapshotMessagesAsync(snapshot);

        // The snapshot file is already captured; a lost save race must not discard it.
        try
        {
            await CommitBranchWorkAsync(() => Store.TemplateSnapshots[snapshot.Id] = snapshot);
        }
        catch (StoreGenerationConflictException)
        {
            Store.TemplateSnapshots.Remove(snapshot.Id);
            throw;
        }
        RefreshTemplateSnapshotCounts();
        ReapplyList();
        return new TemplateSnapshotResult(true, $"Created checkpoint \"{snapshot.DisplayName}\".", snapshot);
    }

    public async Task<BranchResult> SpawnTemplateAsync(
        TemplateSnapshot snapshot,
        SessionEventLedger.Options? eventOptions = null)
    {
        ArchiveSession? source = null;
        if (snapshot is not null && Store.TemplateSnapshots.TryGetValue(snapshot.Id, out var current))
            source = SnapshotSource(current);
        try
        {
            var result = await SpawnTemplateCoreAsync(snapshot);
            RecordBranchOperation("checkpoint.spawn", source, result.Ok, result.Message, result.Branch, eventOptions, snapshot?.Id);
            return result;
        }
        catch (Exception ex)
        {
            RecordBranchOperation("checkpoint.spawn", source, false, "Checkpoint spawn failed: " + ex.Message, null, eventOptions, snapshot?.Id);
            throw;
        }
    }

    private async Task<BranchResult> SpawnTemplateCoreAsync(TemplateSnapshot? snapshot)
    {
        if (snapshot is null) return new BranchResult(false, "No checkpoint selected.", null);
        if (!Store.TemplateSnapshots.TryGetValue(snapshot.Id, out var current))
            return new BranchResult(false, "That checkpoint no longer exists.", null);
        if (string.IsNullOrWhiteSpace(current.SnapshotPath) || !File.Exists(current.SnapshotPath))
            return new BranchResult(false, "The checkpoint transcript is missing from disk.", null);

        var parent = SnapshotSource(current);
        return await CreateNativeBranchAsync(
            parent,
            current.SnapshotPath,
            current.DisplayName,
            current.SourceSessionId,
            current.Id);
    }

    public IReadOnlyList<TemplateSnapshot> Templates() =>
        Store.TemplateSnapshots.Values
            .OrderByDescending(snapshot => snapshot.CreatedAt, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<TemplateSnapshot> TemplateSnapshotsForSource(string sourceSessionId) =>
        Store.TemplateSnapshots.Values
            .Where(snapshot => string.Equals(
                snapshot.SourceSessionId,
                sourceSessionId,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(snapshot => snapshot.CreatedAt, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<ArchiveSession> BranchesForSnapshot(string snapshotId) =>
        Store.Sessions.Values
            .Where(session =>
                !session.Archived
                && string.Equals(session.FromSnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.UpdatedAt, StringComparer.Ordinal)
            .ToList();

    public static string TemplateSnapshotDisplayLabel(TemplateSnapshot snapshot)
    {
        var created = DateTime.TryParse(snapshot.CreatedAt, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : snapshot.CreatedAt;
        var count = snapshot.MessageCount > 0
            ? $"{snapshot.MessageCount} message{(snapshot.MessageCount == 1 ? "" : "s")}"
            : $"{snapshot.LineCount} line{(snapshot.LineCount == 1 ? "" : "s")}";
        var suffix = snapshot.Id.Length <= 8 ? snapshot.Id : snapshot.Id[..8];
        return $"{snapshot.DisplayName} · {created} · {count} · {suffix}";
    }

    public async Task<SnapshotReaderResult> OpenTemplateSnapshotAsync(string snapshotId)
    {
        if (!Store.TemplateSnapshots.TryGetValue(snapshotId, out var snapshot))
            return new SnapshotReaderResult(false, "That checkpoint no longer exists.", null);
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotPath) || !File.Exists(snapshot.SnapshotPath))
            return new SnapshotReaderResult(false, "The checkpoint transcript is missing from disk.", null);

        var reader = new ArchiveSession
        {
            Id = "snapshot:" + snapshot.Id,
            Tool = snapshot.Tool,
            Title = snapshot.DisplayName,
            CustomTitle = TemplateSnapshotDisplayLabel(snapshot),
            SourcePath = snapshot.SnapshotPath,
            Workspace = snapshot.Workspace,
            WorkspaceName = snapshot.WorkspaceName,
            Model = snapshot.Model,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.CreatedAt,
            IsReadOnlySnapshot = true,
            ReadOnlySnapshotId = snapshot.Id
        };
        await EnsureContentAsync(reader);
        return new SnapshotReaderResult(true, "Opened checkpoint read-only.", reader);
    }

    public async Task<bool> RenameTemplateSnapshotAsync(string snapshotId, string name)
    {
        if (!Store.TemplateSnapshots.TryGetValue(snapshotId, out var snapshot)) return false;
        var clean = (name ?? "").Trim();
        if (clean.Length == 0 || string.Equals(snapshot.Name, clean, StringComparison.Ordinal)) return false;
        snapshot.Name = clean;
        await SaveAsync();
        return true;
    }

    public async Task<bool> DeleteTemplateSnapshotAsync(string snapshotId)
    {
        if (!Store.TemplateSnapshots.Remove(snapshotId, out var snapshot)) return false;
        try
        {
            await SaveAsync();
        }
        catch
        {
            Store.TemplateSnapshots[snapshotId] = snapshot;
            throw;
        }
        TryDeleteFile(snapshot.SnapshotPath);
        RefreshTemplateSnapshotCounts();
        ReapplyList();
        return true;
    }

    public async Task<int> RemoveTemplateSnapshotsForSourceAsync(string sourceSessionId)
    {
        var removed = Store.TemplateSnapshots.Values
            .Where(snapshot => string.Equals(
                snapshot.SourceSessionId,
                sourceSessionId,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (removed.Count == 0) return 0;
        foreach (var snapshot in removed) Store.TemplateSnapshots.Remove(snapshot.Id);
        try
        {
            await SaveAsync();
        }
        catch
        {
            foreach (var snapshot in removed) Store.TemplateSnapshots[snapshot.Id] = snapshot;
            throw;
        }
        foreach (var snapshot in removed) TryDeleteFile(snapshot.SnapshotPath);
        RefreshTemplateSnapshotCounts();
        ReapplyList();
        return removed.Count;
    }

    public async Task SetTemplateAsync(ArchiveSession session, bool isTemplate)
    {
        if (isTemplate)
            await CreateTemplateSnapshotAsync(session);
        else
            await RemoveTemplateSnapshotsForSourceAsync(session.Id);
    }

    internal string DescribeSession(ArchiveSession session)
    {
        var collections = Store.Collections.Values
            .Where(collection => collection.SessionIds.Contains(session.Id))
            .Select(collection => collection.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var phrases = session.SpecialPhrases.Where(phrase => !string.IsNullOrWhiteSpace(phrase)).ToList();
        // NAME THE CHECKPOINT, not just the live chat. BranchOfId points at the SOURCE SESSION, whose
        // title keeps moving: a chat renamed after a checkpoint was taken makes every branch from every
        // one of its checkpoints report the chat's LATEST name. Measured: branches spawned from
        // checkpoint "cleanmusic" carried cleanmusic's transcript exactly (1310 user messages, tip uuid
        // 1167d9f7) while this line called them "branch of iosnmusic-pixel" — the name the source chat
        // had acquired an hour later. The branch was right and the label said otherwise, which reads as
        // the spawn having used the wrong checkpoint.
        //
        // FromSnapshotId already records which checkpoint it actually came from, so use it. The
        // checkpoint name is immutable; the source title is not.
        var parent = "no";
        if (session.IsBranch)
        {
            var sourceLabel = Store.Sessions.TryGetValue(session.BranchOfId, out var source)
                ? $"\"{source.DisplayTitle}\""
                : session.BranchOfId;
            parent = !string.IsNullOrWhiteSpace(session.FromSnapshotId)
                     && Store.TemplateSnapshots.TryGetValue(session.FromSnapshotId, out var viaSnapshot)
                ? $"checkpoint \"{viaSnapshot.DisplayName}\" of {sourceLabel}"
                : sourceLabel;
        }
        var branchCount = Store.Sessions.Values.Count(candidate =>
            !candidate.Archived
            && string.Equals(candidate.BranchOfId, session.Id, StringComparison.OrdinalIgnoreCase));
        var checkpointCount = TemplateSnapshotsForSource(session.Id).Count;
        return $"Chat \"{session.DisplayTitle}\" [{session.Tool}]"
             + $" · native name: {(string.IsNullOrWhiteSpace(session.Title) ? "(none)" : $"\"{session.Title}\"")}"
             + $" · collections: {(collections.Count == 0 ? "none" : string.Join(", ", collections))}"
             + $" · phrases: {(phrases.Count == 0 ? "none" : string.Join(", ", phrases))}"
             + $" · checkpoints: {checkpointCount}"
             + (session.IsTemplate ? " · legacy checkpoint migration: pending" : "")
             + $" · branch of: {parent}"
             + (branchCount > 0 ? $" · branches: {branchCount}" : "")
             + $" · id: {session.Id}";
    }

    internal async Task MigrateLegacyTemplatesAsync()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var pendingIds = Store.Sessions.Values
                .Where(session => session.IsTemplate)
                .Select(session => session.Id)
                .ToList();
            if (pendingIds.Count == 0) return;

            var restart = false;
            foreach (var sessionId in pendingIds)
            {
                if (!Store.Sessions.TryGetValue(sessionId, out var session) || !session.IsTemplate) continue;
                try
                {
                    var snapshot = await CreateTemplateSnapshotAsync(
                        session,
                        idempotencyKey: "legacy-template:" + session.Id);
                    if (!snapshot.Ok) continue;

                    session.IsTemplate = false;
                    await SaveAsync();
                }
                catch (StoreGenerationConflictException)
                {
                    await LoadStoreStateAsync();
                    restart = true;
                    break;
                }
            }
            if (!restart) return;
        }
    }

    internal void RefreshTemplateSnapshotCounts()
    {
        var counts = Store.TemplateSnapshots.Values
            .GroupBy(snapshot => snapshot.SourceSessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var session in Store.Sessions.Values)
            session.TemplateSnapshotCount = counts.TryGetValue(session.Id, out var count) ? count : 0;
    }

    internal async Task BackfillTemplateSnapshotMetadataAsync()
    {
        var changed = false;
        foreach (var snapshot in Store.TemplateSnapshots.Values)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SnapshotPath) || !File.Exists(snapshot.SnapshotPath)) continue;
            if (snapshot.LineCount <= 0)
            {
                snapshot.LineCount = CountCompleteLines(snapshot.SnapshotPath);
                changed = true;
            }
            if (snapshot.MessageCount <= 0)
            {
                snapshot.MessageCount = await CountSnapshotMessagesAsync(snapshot);
                changed = true;
            }
        }
        if (changed) await SaveAsync();
    }

    private async Task<int> CountSnapshotMessagesAsync(TemplateSnapshot snapshot)
    {
        var reader = new ArchiveSession
        {
            Id = "snapshot-count:" + snapshot.Id,
            Tool = snapshot.Tool,
            SourcePath = snapshot.SnapshotPath
        };
        await EnsureContentAsync(reader);
        return reader.MessageCount;
    }

    // Branching and checkpointing write a FILE first (a cloned transcript or a snapshot), then record
    // it in the store. The desktop app and the always-on server share that store, so the recording
    // save can lose the generation race — and because these paths had no recovery, an unlucky
    // half-second turned a perfectly good transcript on disk into "branch failed" and deleted it.
    // The same reload-and-reapply idiom the small ops use: adopt the other writer's store, re-apply
    // ONLY the bookkeeping onto it, and save again. The file work is never repeated, so this cannot
    // produce a second branch. Bounded, because three writers interleaving this fast is a real
    // problem the caller should see.
    private async Task CommitBranchWorkAsync(Action apply)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                apply();
                await SaveAsync();
                return;
            }
            catch (StoreGenerationConflictException) when (attempt < 2)
            {
                await LoadAsync();
            }
        }
    }

    private async Task<BranchResult> CreateNativeBranchAsync(
        ArchiveSession parent,
        string sourcePath,
        string displayTitle,
        string parentId,
        string fromSnapshotId = "",
        string launchMode = NativeLaunchMode)
    {
        var tool = (parent.Tool ?? "").Trim().ToLowerInvariant();
        var newId = Guid.NewGuid().ToString();
        string destinationPath;
        try
        {
            destinationPath = tool switch
            {
                "claude" => await CloneClaudeTranscriptAsync(sourcePath, parent.SourcePath, parentId, newId),
                "codex" => await CloneCodexTranscriptAsync(sourcePath, parent, parentId, newId),
                _ => throw new InvalidOperationException($"branching isn't supported for tool '{parent.Tool}'.")
            };
        }
        catch (Exception error)
        {
            return new BranchResult(false, $"Branch failed: {error.Message}", null);
        }

        var now = DateTime.UtcNow.ToString("O");
        var branch = new ArchiveSession
        {
            Id = newId,
            Tool = tool,
            Title = parent.Title,
            CustomTitle = string.IsNullOrWhiteSpace(displayTitle) ? "" : displayTitle + " (branch)",
            SourcePath = destinationPath,
            Workspace = parent.Workspace,
            WorkspaceName = parent.WorkspaceName,
            Model = parent.Model,
            CreatedAt = now,
            UpdatedAt = now,
            BranchOfId = parentId,
            FromSnapshotId = fromSnapshotId,
            BranchedAt = now,
            LaunchMode = NormalizeLaunchMode(launchMode)
        };
        // The transcript is already written, so a lost save race must not throw the branch away —
        // see CommitBranchWorkAsync.
        try
        {
            await CommitBranchWorkAsync(() => Store.Sessions[newId] = branch);
        }
        catch
        {
            Store.Sessions.Remove(newId);
            throw;
        }
        ReapplyList();
        return new BranchResult(true, $"Branched \"{displayTitle}\".", branch);
    }

    private string ResolveSessionSourcePath(ArchiveSession session)
    {
        if (string.IsNullOrEmpty(session.SourcePath)) return "";
        return Path.IsPathRooted(session.SourcePath)
            ? session.SourcePath
            : Path.Combine(_rootPath, session.SourcePath);
    }

    private async Task<string> CloneClaudeTranscriptAsync(
        string sourcePath,
        string nativeSourcePath,
        string parentId,
        string newId)
    {
        var nativeDirectory = Path.GetDirectoryName(nativeSourcePath);
        if (string.IsNullOrWhiteSpace(nativeDirectory))
            nativeDirectory = _claudeSessionsRoot;
        Directory.CreateDirectory(nativeDirectory);
        var destinationPath = Path.Combine(nativeDirectory, newId + ".jsonl");

        var capturedPath = Path.Combine(_templatesRoot, ".spawn-" + Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(_templatesRoot);
        try
        {
            await JsonlTranscriptCloner.CloneAsync(sourcePath, capturedPath);
            var tipUuid = FindClaudeTipUuid(capturedPath);
            var stampedFork = false;
            await JsonlTranscriptCloner.CloneAsync(capturedPath, destinationPath, (_, line) =>
            {
                if (string.IsNullOrWhiteSpace(line)) return line;
                JsonObject? obj;
                // A corrupt/malformed line must not abort the whole branch — pass it through unchanged.
                try { obj = JsonNode.Parse(line) as JsonObject; } catch { return line; }
                if (obj is null) return line;
                if (obj.ContainsKey("sessionId")) obj["sessionId"] = newId;
                if (!stampedFork)
                {
                    obj["forkedFrom"] = new JsonObject
                    {
                        ["sessionId"] = parentId,
                        ["messageUuid"] = tipUuid
                    };
                    stampedFork = true;
                }
                return obj.ToJsonString();
            });
        }
        finally
        {
            TryDeleteFile(capturedPath);
        }
        return destinationPath;
    }

    private async Task<string> CloneCodexTranscriptAsync(
        string sourcePath,
        ArchiveSession parent,
        string parentId,
        string newId)
    {
        var now = DateTime.Now;
        var directory = Path.Combine(
            _codexSessionsRoot,
            now.ToString("yyyy"),
            now.ToString("MM"),
            now.ToString("dd"));
        Directory.CreateDirectory(directory);
        var destinationPath = Path.Combine(
            directory,
            $"rollout-{now:yyyy-MM-ddTHH-mm-ss}-{newId}.jsonl");

        try
        {
            await JsonlTranscriptCloner.CloneAsync(
                sourcePath,
                destinationPath,
                (lineNumber, line) => lineNumber == 0
                    ? RewriteCodexSessionMeta(line, parentId, newId)
                    : line);
            if (!_codexThreadRegistrar(parent, newId, destinationPath))
                throw new InvalidOperationException("the source Codex thread row was not found or the new row could not be verified.");
        }
        catch
        {
            TryDeleteFile(destinationPath);
            throw;
        }
        return destinationPath;
    }

    internal static string RewriteCodexSessionMeta(string line, string parentId, string newId)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;
        JsonObject? obj;
        // A corrupt/malformed session-meta line must not abort the branch — pass it through unchanged.
        try { obj = JsonNode.Parse(line) as JsonObject; } catch { return line; }
        if (obj is null) return line;
        if (obj["payload"] is JsonObject payload)
        {
            if (payload.ContainsKey("id")) payload["id"] = newId;
            if (payload.ContainsKey("session_id")) payload["session_id"] = newId;
            payload["forked_from_id"] = parentId;
        }
        if (obj.ContainsKey("id")) obj["id"] = newId;
        return obj.ToJsonString();
    }

    internal bool RegisterCodexThread(ArchiveSession parent, string newId, string rolloutPath)
    {
        if (!File.Exists(_codexStateDbPath)) return false;
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = _codexStateDbPath }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var columns = new List<string>();
            using (var pragma = connection.CreateCommand())
            {
                pragma.Transaction = transaction;
                pragma.CommandText = "pragma table_info(threads)";
                using var reader = pragma.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
            }
            if (columns.Count == 0) return false;

            var row = ReadThreadRow(
                connection,
                transaction,
                columns,
                "select * from threads where id = $id limit 1",
                parent.Id);
            if (row is null) return false;

            var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            void Set(string column, object? value)
            {
                if (row.ContainsKey(column)) row[column] = value;
            }
            row["id"] = newId;
            Set("rollout_path", rolloutPath);
            Set("created_at", nowSeconds);
            Set("updated_at", nowSeconds);
            Set("recency_at", nowSeconds);
            Set("created_at_ms", nowMilliseconds);
            Set("updated_at_ms", nowMilliseconds);
            Set("recency_at_ms", nowMilliseconds);
            Set("archived", 0L);
            Set("archived_at", null);
            if (row.TryGetValue("title", out var title)
                && title is string text
                && !string.IsNullOrWhiteSpace(text))
                row["title"] = text + " (branch)";

            var columnList = string.Join(",", columns);
            var parameterList = string.Join(",", columns.Select((_, index) => "$p" + index));
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"insert into threads ({columnList}) values ({parameterList})";
                for (var index = 0; index < columns.Count; index++)
                    insert.Parameters.AddWithValue("$p" + index, row[columns[index]] ?? DBNull.Value);
                if (insert.ExecuteNonQuery() != 1) return false;
            }

            using var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandText = "select rollout_path from threads where id = $id";
            verify.Parameters.AddWithValue("$id", newId);
            var verified = string.Equals(
                verify.ExecuteScalar() as string,
                rolloutPath,
                StringComparison.OrdinalIgnoreCase);
            if (!verified) return false;
            transaction.Commit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?>? ReadThreadRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<string> columns,
        string sql,
        string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columns.Count; index++)
            row[columns[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        return row;
    }

    private static string FindClaudeTipUuid(string path)
    {
        string tip = "";
        foreach (var line in SafeReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject obj && obj["uuid"] is JsonValue uuid)
                    tip = uuid.ToString();
            }
            catch { }
        }
        return tip;
    }

    private static int CountCompleteLines(string path)
    {
        var count = 0;
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            for (var index = 0; index < read; index++)
                if (buffer[index] == (byte)'\n') count++;
        return count;
    }

    private static ArchiveSession SnapshotSource(TemplateSnapshot snapshot) => new()
    {
        Id = snapshot.SourceSessionId,
        Tool = snapshot.Tool,
        Title = snapshot.SourceTitle,
        Workspace = snapshot.Workspace,
        WorkspaceName = snapshot.WorkspaceName,
        Model = snapshot.Model,
        SourcePath = snapshot.SourcePath
    };

    private static void RecordBranchOperation(
        string operation,
        ArchiveSession? source,
        bool ok,
        string summary,
        ArchiveSession? branch,
        SessionEventLedger.Options? options,
        string? checkpointId = null)
    {
        var ids = new[] { source?.Id, branch?.Id }
            .Concat(source?.Aliases ?? [])
            .Concat(branch?.Aliases ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var details = new Dictionary<string, string>
        {
            ["operation"] = operation,
            ["outcome"] = ok ? "succeeded" : "failed"
        };
        if (!string.IsNullOrWhiteSpace(checkpointId)) details["checkpointId"] = checkpointId!;
        if (branch is not null) details["createdSessionId"] = branch.Id;
        var ev = SessionEventLedger.Create(
            operation + (ok ? ".succeeded" : ".failed"),
            summary,
            source?.Id,
            source?.Tool,
            source?.DisplayTitle,
            source?.WorkspaceName,
            source: "archive",
            severity: ok ? "info" : "error",
            details: details,
            sessionIds: ids);
        SessionEventLedger.AppendBestEffort(ev, options: options);
    }

    private static void RecordCheckpointOperation(
        string operation,
        ArchiveSession? source,
        bool ok,
        string summary,
        TemplateSnapshot? snapshot,
        SessionEventLedger.Options? options)
    {
        var details = new Dictionary<string, string>
        {
            ["operation"] = operation,
            ["outcome"] = ok ? "succeeded" : "failed"
        };
        if (snapshot is not null) details["checkpointId"] = snapshot.Id;
        var ev = SessionEventLedger.Create(
            operation + (ok ? ".succeeded" : ".failed"),
            summary,
            source?.Id,
            source?.Tool,
            source?.DisplayTitle,
            source?.WorkspaceName,
            source: "archive",
            severity: ok ? "info" : "error",
            details: details,
            sessionIds: source is null ? [] : new[] { source.Id }.Concat(source.Aliases));
        SessionEventLedger.AppendBestEffort(ev, options: options);
    }

    private static string DeterministicSnapshotId(string sourceId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceId + "\n" + idempotencyKey));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }
}
