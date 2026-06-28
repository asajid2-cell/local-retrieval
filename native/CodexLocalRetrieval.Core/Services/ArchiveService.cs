using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Core.Services;

public sealed class ArchiveService
{
    private const int CurrentIndexVersion = 10; // bump on any parser change to force a full re-parse
    private const int MaxIndexedFiles = 4000;
    // The reader keeps the MOST RECENT messages (not the oldest), so a long chat opens on its latest
    // turns. The first prompt is still captured for the title before this window is applied.
    private const int RecentMessageWindow = 600;
    private const int MaxLinesPerSession = 18_000;
    private const int MaxLineChars = 512_000;
    private const int ClaudeTailBytes = 32 * 1024 * 1024;
    private const int SearchTextCap = 6_000; // capped, in-memory searchable text per session (full content lazy-loads)

    private static string CapText(string s) => string.IsNullOrEmpty(s) || s.Length <= SearchTextCap ? s : s[^SearchTextCap..];

    private readonly string _rootPath;
    private readonly string _storePath;
    private readonly string _bundledStorePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public AppStoreData Store { get; private set; } = new();
    public ObservableCollection<ArchiveSession> Sessions { get; } = new();

    public ArchiveService(string? storePath = null, bool useBundledStore = false)
    {
        _rootPath = FindProjectRoot();
        _bundledStorePath = Path.Combine(_rootPath, "data", "app-store.json");
        _storePath = useBundledStore
            ? _bundledStorePath
            : storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLocalRetrieval",
            "app-store.json");
    }

    public async Task LoadAsync()
    {
        var loadPath = File.Exists(_storePath) ? _storePath : _bundledStorePath;
        if (File.Exists(loadPath))
        {
            // The store can be tens of MB. Read + deserialize OFF the calling (UI) thread, straight from
            // the file stream (no giant intermediate string), so launch never blocks the UI.
            Store = await Task.Run(() =>
            {
                using var fs = File.OpenRead(loadPath);
                var data = JsonSerializer.Deserialize<AppStoreData>(fs, _jsonOptions) ?? new AppStoreData();
                // A store written before the metadata-only change still has full (uncapped) Text; cap it in
                // memory so even the first load is light. Next save persists the capped form.
                foreach (var s in data.Sessions.Values)
                    if (s.Text.Length > SearchTextCap) s.Text = s.Text[..SearchTextCap];
                return data;
            }) ?? new AppStoreData();
        }
        NormalizeSettings();
        RefreshSessions(OrderedVisibleSessions(Store.Sessions.Values));
    }

    // Lazy-load a chat's full content (messages + code blocks) from its source file on demand, OFF the
    // UI thread. The store holds only metadata, so this runs when a chat is opened or its content is
    // needed (co-pilot, copy-code). Cheap no-op once loaded.
    public async Task EnsureContentAsync(ArchiveSession session)
    {
        if (session.ContentLoaded) return;
        // SourcePath may be relative (the bundled demo store) — resolve against the project root.
        var path = string.IsNullOrEmpty(session.SourcePath) ? "" :
            Path.IsPathRooted(session.SourcePath) ? session.SourcePath : Path.Combine(_rootPath, session.SourcePath);
        if (path.Length == 0 || !File.Exists(path))
        {
            session.ContentLoaded = true;
            return;
        }
        try
        {
            var parsed = await Task.Run(async () => await ParseSessionAsync(path, session.Tool));
            if (parsed is not null)
            {
                session.Messages = parsed.Messages;
                session.CodeBlocks = parsed.CodeBlocks;
                session.MessageCount = parsed.Messages.Count;
            }
        }
        catch (Exception ex)
        {
            session.Messages = new ObservableCollection<ArchiveMessage>
            {
                new()
                {
                    Id = "load-error",
                    Role = "assistant",
                    Timestamp = session.UpdatedAt,
                    Text = $"Could not load the full transcript from disk.\n\n{ex.Message}\n\nSource: {path}"
                }
            };
            session.CodeBlocks = new ObservableCollection<CodeBlock>();
            session.MessageCount = session.Messages.Count;
        }
        session.ContentLoaded = true;
    }

    // Synchronous lazy-load for callers that can't await (the co-pilot's sync tool delegates, the web read
    // handler). Runs the parse on the thread pool (no captured context) so it can't deadlock the UI.
    public void EnsureContent(ArchiveSession session)
    {
        if (session.ContentLoaded) return;
        Task.Run(() => EnsureContentAsync(session)).GetAwaiter().GetResult();
    }

    // Force a fresh re-parse from disk (the live tail of an open chat as the agent keeps writing it).
    public async Task ReloadContentAsync(ArchiveSession session)
    {
        session.ContentLoaded = false;
        await EnsureContentAsync(session);
    }

    // The last-write time of a session's source transcript, for cheap "did it change?" polling.
    public DateTime SourceWriteTimeUtc(ArchiveSession session) => SourceFileWriteTimeUtc(session);

    public async Task<bool> EnrichTitlesFromLocalStateAsync()
    {
        // Read the (potentially slow) sqlite/jsonl off-thread, but APPLY the changes on the caller
        // (UI) thread — mutating bound sessions raises INotifyPropertyChanged, which must not fire
        // from a worker.
        var titles = await Task.Run(LoadThreadTitles);
        var changed = ApplyThreadTitles(titles);
        if (changed) RefreshSessions(Store.Sessions.Values);
        return changed;
    }

    public async Task SaveAsync()
    {
        // The store can be tens of MB, and SaveAsync runs on the UI thread from many actions (pin, rename,
        // sync). Snapshot the top-level dictionaries on the caller's thread (cheap — references only, and the
        // merge REPLACES session objects rather than mutating them, so the snapshot stays stable), then
        // serialize + write entirely OFF the UI thread so a save never freezes the app.
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var snapshot = new AppStoreData
        {
            Settings = Store.Settings,
            Sessions = new Dictionary<string, ArchiveSession>(Store.Sessions),
            Collections = new Dictionary<string, ArchiveCollection>(Store.Collections),
            DeletedCollections = new List<DeletedCollection>(Store.DeletedCollections),
            FileStamps = new Dictionary<string, string>(Store.FileStamps),
        };
        await _saveGate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                await File.WriteAllTextAsync(_storePath, json);
            });
        }
        finally { _saveGate.Release(); }
    }

    public void RefreshSessions(IEnumerable<ArchiveSession> sessions)
    {
        Sessions.Clear();
        foreach (var session in OrderedVisibleSessions(sessions).Take(600))
        {
            if (session.Tags.Count == 0)
            {
                session.Tags.Add("archive");
                if (session.CodeBlocks.Count > 0) session.Tags.Add("code");
            }
            Sessions.Add(session);
        }
    }

    public IReadOnlyList<ArchiveSession> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return OrderedVisibleSessions(Store.Sessions.Values).ToList();
        }

        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Store.Sessions.Values
            .Where(session => !session.Archived)
            .Where(session => terms.All(term => SearchText(session).Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(session => Score(session, terms))
            .ThenByDescending(session => session.Pinned)
            .ThenByDescending(session => session.UpdatedAt)
            .ToList();
    }

    public IReadOnlyList<ArchiveSearchHit> DeepSearch(string query, int limit = 80)
    {
        var visible = Store.Sessions.Values.Where(session => !session.Archived);
        if (string.IsNullOrWhiteSpace(query))
        {
            return OrderedVisibleSessions(visible)
                .Take(limit)
                .Select(session => new ArchiveSearchHit
                {
                    Session = session,
                    Message = session.Messages.LastOrDefault(),
                    SourceLabel = "recent chat",
                    Snippet = MakeSnippet(session.Messages.LastOrDefault()?.Text ?? session.Text, []),
                    Score = session.Pinned ? 5 : 0
                })
                .ToList();
        }

        var terms = QueryTerms(query);
        return visible
            .SelectMany(session => DeepHitsForSession(session, terms))
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Session.Pinned)
            .ThenByDescending(hit => hit.Session.UpdatedAt)
            .Take(limit)
            .ToList();
    }

    public async Task RenameSessionAsync(ArchiveSession session, string title)
    {
        session.CustomTitle = CleanTitle(title);
        await SaveAsync();
        RefreshSessions(Store.Sessions.Values);
    }

    public async Task TogglePinAsync(ArchiveSession session)
    {
        session.Pinned = !session.Pinned;
        await SaveAsync();
        RefreshSessions(Store.Sessions.Values);
    }

    public async Task ArchiveSessionAsync(ArchiveSession session)
    {
        session.Archived = true;
        await SaveAsync();
        RefreshSessions(Store.Sessions.Values);
    }

    public async Task AddToCollectionAsync(ArchiveSession session, string collectionName)
    {
        var id = Slug(collectionName);
        if (!Store.Collections.TryGetValue(id, out var collection))
        {
            collection = new ArchiveCollection { Id = id, Name = collectionName, Color = Store.Settings.AccentHex };
            Store.Collections[id] = collection;
        }
        if (!collection.SessionIds.Contains(session.Id)) collection.SessionIds.Add(session.Id);
        await SaveAsync();
    }

    // Create an empty collection from the Collections control panel (no chat needed yet). Returns
    // the collection, whether it was newly created or already existed under the same slug — so an
    // agent self-filing into the same name later lands in this exact collection.
    public async Task<ArchiveCollection> CreateCollectionAsync(string collectionName)
    {
        var id = Slug(collectionName);
        if (!Store.Collections.TryGetValue(id, out var collection))
        {
            collection = new ArchiveCollection { Id = id, Name = collectionName, Color = Store.Settings.AccentHex };
            Store.Collections[id] = collection;
            await SaveAsync();
            WriteAutoBackup();   // keep the latest app backup reflecting the new project
        }
        return collection;
    }

    private const int MaxDeletedCollections = 100;

    // Delete a project/collection — only the grouping; the chats themselves stay in the archive.
    // Soft delete: the grouping moves to Recently Deleted (and an app backup is written first), so an
    // accidental delete is fully recoverable.
    public async Task RemoveCollectionAsync(string collectionId)
    {
        if (!Store.Collections.TryGetValue(collectionId, out var col)) return;
        WriteAutoBackup();                       // capture a restore point that still includes this collection
        Store.Collections.Remove(collectionId);
        Store.DeletedCollections.Insert(0, new DeletedCollection { Collection = col, DeletedAt = DateTime.UtcNow.ToString("O") });
        while (Store.DeletedCollections.Count > MaxDeletedCollections)
            Store.DeletedCollections.RemoveAt(Store.DeletedCollections.Count - 1);
        await SaveAsync();
    }

    // Bring a soft-deleted collection back. If a collection with the same id now exists, its chats are
    // merged in rather than overwritten.
    public async Task RestoreDeletedCollectionAsync(string collectionId)
    {
        var idx = Store.DeletedCollections.FindIndex(d => d.Collection.Id == collectionId);
        if (idx < 0) return;
        var col = Store.DeletedCollections[idx].Collection;
        Store.DeletedCollections.RemoveAt(idx);
        if (Store.Collections.TryGetValue(col.Id, out var existing))
        {
            foreach (var sid in col.SessionIds)
                if (!existing.SessionIds.Contains(sid)) existing.SessionIds.Add(sid);
        }
        else
        {
            Store.Collections[col.Id] = col;
        }
        await SaveAsync();
    }

    // Permanently drop a collection from Recently Deleted (app backups may still hold it).
    public async Task PurgeDeletedCollectionAsync(string collectionId)
    {
        var idx = Store.DeletedCollections.FindIndex(d => d.Collection.Id == collectionId);
        if (idx >= 0) { Store.DeletedCollections.RemoveAt(idx); await SaveAsync(); }
    }

    public async Task EmptyRecentlyDeletedAsync()
    {
        if (Store.DeletedCollections.Count == 0) return;
        Store.DeletedCollections.Clear();
        await SaveAsync();
    }

    // ---- Backup / export / import (lightweight metadata only) -------------------------------------

    private const int MaxAutoBackups = 30;
    public string CollectionBackupsDir => Path.Combine(Path.GetDirectoryName(_storePath)!, "collection-backups");

    // Serialize the current collections to a portable JSON backup (no chat content).
    public string ExportCollectionsJson()
    {
        var backup = new CollectionsBackup
        {
            ExportedAt = DateTime.UtcNow.ToString("O"),
            Collections = Store.Collections.Values.ToList()
        };
        return JsonSerializer.Serialize(backup, _jsonOptions);
    }

    // Merge a backup into the current collections: new ones are added, existing ones gain any missing
    // chats. Never destructive. Returns the number of collections added, or -1 if the file isn't valid.
    public async Task<int> ImportCollectionsJsonAsync(string json)
    {
        CollectionsBackup? backup;
        try { backup = JsonSerializer.Deserialize<CollectionsBackup>(json, _jsonOptions); }
        catch { return -1; }
        if (backup?.Collections is null) return -1;

        var added = 0;
        foreach (var col in backup.Collections)
        {
            if (string.IsNullOrWhiteSpace(col.Id)) col.Id = Slug(col.Name);
            if (string.IsNullOrWhiteSpace(col.Id)) continue;
            if (Store.Collections.TryGetValue(col.Id, out var existing))
            {
                foreach (var sid in col.SessionIds)
                    if (!existing.SessionIds.Contains(sid)) existing.SessionIds.Add(sid);
            }
            else
            {
                Store.Collections[col.Id] = col;
                added++;
            }
        }
        await SaveAsync();
        return added;
    }

    // App-side automatic backup: a timestamped snapshot of collections, keeping the most recent N.
    public void WriteAutoBackup()
    {
        try
        {
            Directory.CreateDirectory(CollectionBackupsDir);
            var name = "collections-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json";
            File.WriteAllText(Path.Combine(CollectionBackupsDir, name), ExportCollectionsJson());
            foreach (var old in Directory.GetFiles(CollectionBackupsDir, "collections-*.json")
                         .OrderByDescending(f => f).Skip(MaxAutoBackups))
                try { File.Delete(old); } catch { }
        }
        catch { /* backups are best-effort; never block the app */ }
    }

    // The app's automatic backups, newest first.
    public IReadOnlyList<(string Path, DateTime When, int Count)> ListAppBackups()
    {
        try
        {
            if (!Directory.Exists(CollectionBackupsDir)) return Array.Empty<(string, DateTime, int)>();
            return Directory.GetFiles(CollectionBackupsDir, "collections-*.json")
                .Select(f =>
                {
                    var count = 0;
                    try { count = JsonSerializer.Deserialize<CollectionsBackup>(File.ReadAllText(f), _jsonOptions)?.Collections.Count ?? 0; }
                    catch { }
                    return (f, File.GetLastWriteTime(f), count);
                })
                .OrderByDescending(x => x.Item2)
                .ToList();
        }
        catch { return Array.Empty<(string, DateTime, int)>(); }
    }

    public async Task<int> RestoreAppBackupAsync(string path)
    {
        if (!File.Exists(path)) return -1;
        return await ImportCollectionsJsonAsync(File.ReadAllText(path));
    }

    // Remove a single chat from a project without deleting the project or the chat.
    public async Task RemoveFromCollectionAsync(string collectionId, string sessionId)
    {
        if (Store.Collections.TryGetValue(collectionId, out var collection) && collection.SessionIds.Remove(sessionId))
            await SaveAsync();
    }

    // Narrow by-id operations for the co-pilot tools. App metadata only — these never write the
    // canonical agent store and never touch source .jsonl files.
    public ArchiveSession? GetSession(string sessionId) =>
        Store.Sessions.TryGetValue(sessionId, out var session) ? session : null;

    public async Task<bool> SetFavoriteAsync(string sessionId, bool favorite)
    {
        if (!Store.Sessions.TryGetValue(sessionId, out var session)) return false;
        session.Pinned = favorite;
        await SaveAsync();
        RefreshSessions(Store.Sessions.Values);
        return true;
    }

    public async Task<bool> RenameLocalAsync(string sessionId, string title)
    {
        if (!Store.Sessions.TryGetValue(sessionId, out var session)) return false;
        await RenameSessionAsync(session, title); // CustomTitle only; no canonical write-back
        return true;
    }

    public async Task<bool> AddToProjectByIdAsync(string sessionId, string project)
    {
        if (!Store.Sessions.TryGetValue(sessionId, out var session)) return false;
        await AddToCollectionAsync(session, project);
        return true;
    }

    // Apply one command from an outside agent (see AGENTS.md). Lets a user point any Claude/Codex
    // chat at the app and say "set yourself up / favorite yourself / file yourself into project X".
    public async Task<AgentCommandResult> ApplyAgentCommandAsync(AgentCommand cmd)
    {
        var op = NormalizeAgentOp(cmd.op);
        switch (op)
        {
            case "init":
                EnsureDefaultSources();
                await SaveAsync();
                return new AgentCommandResult(true, "Initialized default sources (Codex + Claude).");

            case "addsource":
                if (string.IsNullOrWhiteSpace(cmd.root) || string.IsNullOrWhiteSpace(cmd.tool))
                    return new AgentCommandResult(false, "addSource needs both 'tool' and 'root'.");
                EnsureDefaultSources();
                if (!Store.Settings.Sources.Any(s => string.Equals(s.Root, cmd.root, StringComparison.OrdinalIgnoreCase)
                                                     && string.Equals(s.Tool, cmd.tool, StringComparison.OrdinalIgnoreCase)))
                    Store.Settings.Sources.Add(new SessionSource { Tool = cmd.tool!.ToLowerInvariant(), Root = cmd.root! });
                await SaveAsync();
                return new AgentCommandResult(true, $"Added {cmd.tool} source: {cmd.root}");

            case "favorite":
            case "pin":
            {
                var s = await ResolveOrIndexTargetAsync(cmd);
                var inputId = ExplicitId(cmd);
                if (s is null) return new AgentCommandResult(false, ResolveFailureHelp("favorite"), inputId);
                s.Pinned = true;
                await SaveAsync();
                RefreshSessions(Store.Sessions.Values);
                return new AgentCommandResult(true, $"Favorited \"{s.DisplayTitle}\".", inputId, s.Id, Persisted: true);
            }

            case "addselftoproject":
            {
                if (string.IsNullOrWhiteSpace(cmd.project)) return new AgentCommandResult(false, "addToProject needs 'project'.");
                var s = await ResolveOrIndexTargetAsync(cmd);
                var inputId = ExplicitId(cmd);
                if (s is null) return new AgentCommandResult(false, ResolveFailureHelp("addToCollection"), inputId, Project: cmd.project);
                await AddToCollectionAsync(s, cmd.project!);
                var named = ApplyOptionalName(s, cmd);  // optional app-local name in the same call
                if (named) await SaveAsync();
                RefreshSessions(Store.Sessions.Values);
                var persisted = Store.Collections.TryGetValue(Slug(cmd.project!), out var collection)
                                && collection.SessionIds.Contains(s.Id);
                return new AgentCommandResult(
                    true,
                    $"Added \"{s.DisplayTitle}\" to project \"{cmd.project}\".{(named ? " Named it in the app." : "")}",
                    inputId,
                    s.Id,
                    cmd.project,
                    persisted);
            }

            case "rename":
            {
                var s = await ResolveOrIndexTargetAsync(cmd);
                var inputId = ExplicitId(cmd);
                if (s is null) return new AgentCommandResult(false, ResolveFailureHelp("rename"), inputId);
                if (!ApplyOptionalName(s, cmd) && string.IsNullOrWhiteSpace(cmd.canonicalName))
                    return new AgentCommandResult(false, "rename/setName needs a 'name' (app-only) and/or 'canonicalName'.", inputId, s.Id);
                var canonical = await TryWriteCanonicalNameAsync(s, cmd.canonicalName);
                await SaveAsync();
                RefreshSessions(Store.Sessions.Values);
                return new AgentCommandResult(true, $"Renamed to \"{s.DisplayTitle}\".{(canonical is null ? "" : " " + canonical)}", inputId, s.Id, Persisted: true);
            }

            case "bump":
            {
                var s = await ResolveOrIndexTargetAsync(cmd);
                var inputId = ExplicitId(cmd);
                if (s is null) return new AgentCommandResult(false, ResolveFailureHelp("bump"), inputId);
                var native = await BumpSessionAsync(s);
                var where = string.Equals(s.Tool, "codex", StringComparison.OrdinalIgnoreCase) ? "codex resume" : "Claude's recent chats";
                return new AgentCommandResult(
                    true,
                    native ? $"Bumped \"{s.DisplayTitle}\" to the top of {where}." : $"Bumped \"{s.DisplayTitle}\" in the app (native list unchanged — chat not found in {where}).",
                    inputId, s.Id, Persisted: native);
            }

            case "tag":
            case "untag":
            {
                var s = await ResolveOrIndexTargetAsync(cmd);
                var inputId = ExplicitId(cmd);
                if (s is null) return new AgentCommandResult(false, ResolveFailureHelp(op), inputId);
                var tags = (cmd.tags ?? new List<string>()).ToList();
                if (!string.IsNullOrWhiteSpace(cmd.name)) tags.Add(cmd.name!);
                tags = tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                if (tags.Count == 0) return new AgentCommandResult(false, $"{op} needs 'tags' (array) or 'name'.", inputId, s.Id);
                var changed = 0;
                foreach (var t in tags)
                    changed += (op == "tag" ? AddTagTo(s.Tags, t, reserved: true) : RemoveTagFrom(s.Tags, t)) ? 1 : 0;
                if (changed > 0) { await SaveAsync(); RefreshSessions(Store.Sessions.Values); }
                var verb = op == "tag" ? "Tagged" : "Untagged";
                return new AgentCommandResult(true, $"{verb} \"{s.DisplayTitle}\" ({changed} change{(changed == 1 ? "" : "s")}).", inputId, s.Id, Persisted: changed > 0);
            }

            default:
                return new AgentCommandResult(false, $"Unknown op: '{cmd.op}'.");
        }
    }

    public static string NormalizeAgentOp(string? op)
    {
        var value = (op ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "addtoproject" or "addtocollection" or "addselftoproject" or "addselftocollection" => "addselftoproject",
            "setname" or "name" or "label" or "setlabel" => "rename",
            "pin" => "pin",
            _ => value
        };
    }

    // Optional app-LOCAL chat name (cmd.name, falling back to cmd.localName). Never touches the
    // agent's own global title - that's canonicalName's job. Returns true if a name was applied.
    private bool ApplyOptionalName(ArchiveSession session, AgentCommand cmd)
    {
        var name = !string.IsNullOrWhiteSpace(cmd.name) ? cmd.name : cmd.localName;
        if (string.IsNullOrWhiteSpace(name)) return false;
        session.CustomTitle = CleanTitle(name!);
        return true;
    }

    private void EnsureDefaultSources()
    {
        if (Store.Settings.Sources.Count == 0) Store.Settings.Sources.AddRange(DefaultSources());
    }

    // The id an agent supplied for a per-chat op, if any (cmd.id, or cmd.target when it is a concrete id).
    private static string? ExplicitId(AgentCommand cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd.id)) return cmd.id.Trim();
        if (!string.IsNullOrWhiteSpace(cmd.target) && !IsSelfTarget(cmd.target)) return cmd.target.Trim();
        return null;
    }

    private static bool IsSelfTarget(string? target) =>
        string.Equals(target, "self", StringComparison.OrdinalIgnoreCase)
        || string.Equals(target, "latest", StringComparison.OrdinalIgnoreCase);

    private static bool IsSelfOp(AgentCommand cmd) =>
        NormalizeAgentOp(cmd.op) is "addselftoproject" or "addselftocollection";

    // Resolve the target session, and if an exact id was given but isn't in the store yet, index just
    // that one file from disk (a brand-new session adding itself before a full sync has seen it).
    private async Task<ArchiveSession?> ResolveOrIndexTargetAsync(AgentCommand cmd)
    {
        var s = ResolveTargetSession(cmd);
        if (s is not null) return await RefreshIndexedSessionAsync(s, ExplicitId(cmd), cmd.tool);
        var id = ExplicitId(cmd);
        return string.IsNullOrWhiteSpace(id) ? null : await EnsureSessionIndexedAsync(id!, cmd.tool);
    }

    // Exact-id/self commands must not act on stale metadata. A long Claude session can keep the same
    // resumable id while new turns are appended far past the original indexed window, so refresh the
    // matched source file before filing/favoriting/naming it.
    private async Task<ArchiveSession?> RefreshIndexedSessionAsync(ArchiveSession existing, string? expectedId, string? tool)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(existing.SourcePath) ? ""
                : Path.IsPathRooted(existing.SourcePath) ? existing.SourcePath : Path.Combine(_rootPath, existing.SourcePath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return existing;

            var parsed = await ParseSessionAsync(path, string.IsNullOrWhiteSpace(tool) ? existing.Tool : tool!);
            if (parsed is null) return existing;

            var targetId = !string.IsNullOrWhiteSpace(expectedId) ? expectedId! : existing.Id;
            if (!SessionHasIdOrAlias(parsed, targetId) && !string.Equals(parsed.Id, existing.Id, StringComparison.OrdinalIgnoreCase))
                return existing;

            PreserveAppFields(existing, parsed);
            if (!string.Equals(parsed.Id, existing.Id, StringComparison.OrdinalIgnoreCase)) Store.Sessions.Remove(existing.Id);
            Store.Sessions[parsed.Id] = parsed;

            var info = new FileInfo(path);
            Store.FileStamps[path] = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
            return parsed;
        }
        catch
        {
            return existing;
        }
    }

    // Find and index the single transcript whose id matches, across the configured sources, so an
    // exact-id command works for a session the store hasn't scanned yet. Returns null if no such file
    // exists (then the caller fails closed - never a different chat).
    private async Task<ArchiveSession?> EnsureSessionIndexedAsync(string id, string? tool)
    {
        if (ResolveSessionByIdOrAlias(id, tool) is { } existing) return existing;
        var sources = Store.Settings.Sources.Count > 0 ? Store.Settings.Sources : DefaultSources();
        foreach (var src in sources)
        {
            if (!string.IsNullOrWhiteSpace(tool) && !string.Equals(src.Tool, tool, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(src.Root) || !Directory.Exists(src.Root)) continue;
            string? file = null;
            try { file = Directory.EnumerateFiles(src.Root, "*" + id + "*.jsonl", SearchOption.AllDirectories).FirstOrDefault(); }
            catch { }
            if (file is null) continue;
            var parsed = await ParseSessionAsync(file, src.Tool);
            if (parsed is null || !SessionHasIdOrAlias(parsed, id)) continue;
            Store.Sessions[parsed.Id] = parsed;
            await SaveAsync();
            return parsed;
        }
        return null;
    }

    // Shown to an agent when nothing matched - steers it to its env-provided runtime id (fail closed).
    private static string ResolveFailureHelp(string op) =>
        $"Could not identify your chat for '{op}'. Send your runtime session id: Codex uses the CODEX_THREAD_ID " +
        "environment variable, Claude uses CLAUDE_CODE_SESSION_ID. The app resolves that id to its stored chat key. Example: " +
        $"{{\"op\":\"{op}\",\"project\":\"...\",\"id\":\"<that id>\",\"tool\":\"codex\"}}. " +
        "(target:\"self\" only matches an already-indexed chat in the exact same folder + tool, and never guesses another chat.)";

    // FAIL CLOSED. An agent's only strong identity is its runtime session id, which it gets from its
    // environment (Codex: CODEX_THREAD_ID, Claude: CLAUDE_CODE_SESSION_ID). The store may key a
    // resumed/forked chat by another canonical id, so id matching accepts exact aliases from the
    // transcript header/path. So:
    //   1. If an id is supplied, it must match an indexed session id/alias, or we return null. We never
    //      "fall through" to a heuristic - guessing is what filed a random old chat before.
    //   2. "self"/"latest" is a fallback only, and only with BOTH a tool and a cwd; it matches within
    //      that exact workspace+tool. There is NO global "most recent" fallback.
    // Returning null is correct - the caller turns it into an error so the agent re-sends an exact id,
    // rather than the app silently picking the wrong conversation.
    public ArchiveSession? ResolveTargetSession(AgentCommand cmd)
    {
        var explicitId = ExplicitId(cmd);
        if (!string.IsNullOrWhiteSpace(explicitId))
            return ResolveSessionByIdOrAlias(explicitId!, cmd.tool);   // strong id/alias or nothing

        var isSelf = IsSelfTarget(cmd.target) || IsSelfOp(cmd);
        if (!isSelf) return null;
        if (string.IsNullOrWhiteSpace(cmd.tool) || string.IsNullOrWhiteSpace(cmd.cwd)) return null; // self needs tool+cwd

        var norm = NormalizePath(cmd.cwd!);
        return Store.Sessions.Values
            .Where(s => string.Equals(s.Tool, cmd.tool, StringComparison.OrdinalIgnoreCase))
            .Where(s => NormalizePath(s.Workspace) == norm)
            .OrderByDescending(SourceFileWriteTimeUtc)
            .ThenByDescending(s => s.UpdatedAt)
            .FirstOrDefault();   // null if nothing in that exact workspace+tool
    }

    private ArchiveSession? ResolveSessionByIdOrAlias(string id, string? tool)
    {
        var input = id.Trim();
        if (Store.Sessions.TryGetValue(input, out var byKey) && ToolMatches(byKey, tool)) return byKey;

        var exact = Store.Sessions.Values
            .Where(s => ToolMatches(s, tool))
            .Where(s => string.Equals(s.Id, input, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;

        var aliases = Store.Sessions.Values
            .Where(s => ToolMatches(s, tool))
            .Where(s => SessionHasIdOrAlias(s, input))
            .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return aliases.Count == 1 ? aliases[0] : null;
    }

    private static bool ToolMatches(ArchiveSession session, string? tool) =>
        string.IsNullOrWhiteSpace(tool) || string.Equals(session.Tool, tool, StringComparison.OrdinalIgnoreCase);

    private static bool SessionHasIdOrAlias(ArchiveSession session, string id)
    {
        if (string.Equals(session.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        if (session.Aliases.Any(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase))) return true;
        return IsAliasToken(id)
               && !string.IsNullOrWhiteSpace(session.SourcePath)
               && session.SourcePath.Contains(id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAliasToken(string id) => id.Length >= 8 && IsResumableId(id);

    // The last-write time of a session's source transcript on disk (resolving a stored relative path
    // against the root). This is what makes "self" pick the chat that's live right now; if the file
    // is gone we fall back to the parsed UpdatedAt so ordering still degrades sanely.
    private DateTime SourceFileWriteTimeUtc(ArchiveSession s)
    {
        try
        {
            var path = string.IsNullOrEmpty(s.SourcePath) ? ""
                : Path.IsPathRooted(s.SourcePath) ? s.SourcePath : Path.Combine(_rootPath, s.SourcePath);
            if (path.Length > 0 && File.Exists(path)) return File.GetLastWriteTimeUtc(path);
        }
        catch { }
        return DateTime.TryParse(s.UpdatedAt, out var d) ? d.ToUniversalTime() : DateTime.MinValue;
    }

    private static string NormalizePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try { return Path.GetFullPath(p).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant(); }
        catch { return p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant(); }
    }

    // L11: write the canonical name back to the agent's own store so the rename shows up in
    // Codex/Claude's native resume picker too — not just inside this app.
    public async Task<string?> TryWriteCanonicalNameAsync(ArchiveSession session, string? canonicalName)
    {
        if (string.IsNullOrWhiteSpace(canonicalName)) return null;
        if (string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase))
            return await Task.Run(() => WriteCodexThreadTitle(session.Id, canonicalName!));
        return "Claude has no external rename API yet, so the canonical name stays app-only.";
    }

    private static string WriteCodexThreadTitle(string id, string title)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");
            if (!File.Exists(path)) return "Codex title DB not found; local name updated.";
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using (var busy = connection.CreateCommand()) { busy.CommandText = "PRAGMA busy_timeout=2000;"; busy.ExecuteNonQuery(); }
            using var command = connection.CreateCommand();
            command.CommandText = "update threads set title = $t where id = $id";
            command.Parameters.AddWithValue("$t", title);
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0 ? "Canonical name written to Codex." : "No matching Codex thread; local name updated.";
        }
        catch (Exception ex)
        {
            return "Codex canonical rename failed (" + ex.Message + "); local name updated.";
        }
    }

    // "Bump": float a chat to the top of Codex/Claude's OWN resume picker without sending a message.
    // Each picker sorts by recency, so we just refresh the recency signal it reads:
    //   - Codex orders by threads.updated_at_ms in ~/.codex/state_5.sqlite (keyed by the thread id).
    //   - Claude lists transcript .jsonl files by file mtime.
    // Also floats the chat to the top of THIS app's list. Returns true if the native signal was updated.
    public async Task<bool> BumpSessionAsync(ArchiveSession session)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var native = await Task.Run(() =>
        {
            if (string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase))
            {
                var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");
                var ids = new List<string> { session.Id };
                ids.AddRange(session.Aliases);
                return BumpCodexThreadUpdatedAt(dbPath, ids, nowMs) > 0;
            }
            try
            {
                var path = string.IsNullOrEmpty(session.SourcePath) ? ""
                    : Path.IsPathRooted(session.SourcePath) ? session.SourcePath : Path.Combine(_rootPath, session.SourcePath);
                if (path.Length > 0 && File.Exists(path)) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return true; }
            }
            catch { }
            return false;
        });

        session.UpdatedAt = DateTime.UtcNow.ToString("O");   // top of our list too
        await SaveAsync();
        RefreshSessions(Store.Sessions.Values);
        return native;
    }

    // Set updated_at_ms (+ updated_at seconds) = now for the given thread ids in Codex's state DB,
    // so `codex resume` shows the chat first. Returns rows changed. Takes an explicit path for tests.
    public static int BumpCodexThreadUpdatedAt(string dbPath, IEnumerable<string> ids, long nowMs)
    {
        if (!File.Exists(dbPath)) return 0;
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using (var busy = connection.CreateCommand()) { busy.CommandText = "PRAGMA busy_timeout=2000;"; busy.ExecuteNonQuery(); }
            var changed = 0;
            foreach (var id in ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "update threads set updated_at_ms = $ms, updated_at = $s where id = $id";
                cmd.Parameters.AddWithValue("$ms", nowMs);
                cmd.Parameters.AddWithValue("$s", nowMs / 1000);
                cmd.Parameters.AddWithValue("$id", id);
                changed += cmd.ExecuteNonQuery();
            }
            return changed;
        }
        catch { return 0; }
    }

    // ---- Tagging & filtering -------------------------------------------------------------------
    // Reserved tags are auto-managed (archive = "lives in the archive", code = "has code blocks") and
    // are hidden from the tag UI/filters so user tags stay clean. Everything else is a user tag.
    public static readonly IReadOnlyCollection<string> ReservedTags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "archive", "code" };

    public static bool IsReservedTag(string? tag) => !string.IsNullOrWhiteSpace(tag) && ReservedTags.Contains(tag.Trim());

    public static string NormalizeTag(string? tag)
    {
        var t = Regex.Replace((tag ?? "").Trim(), "\\s+", " ");
        return t.Length > 40 ? t[..40].Trim() : t;
    }

    // The user-facing tags on a chat (reserved tags removed), sorted, de-duped case-insensitively.
    public static IReadOnlyList<string> UserTags(ArchiveSession session) =>
        session.Tags
            .Where(t => !string.IsNullOrWhiteSpace(t) && !IsReservedTag(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<bool> AddChatTagAsync(ArchiveSession session, string tag)
    {
        if (!AddTagTo(session.Tags, tag, reserved: true)) return false;
        await SaveAsync();
        RefreshSessions(Store.Sessions.Values);
        return true;
    }

    public async Task<bool> RemoveChatTagAsync(ArchiveSession session, string tag)
    {
        if (!RemoveTagFrom(session.Tags, tag)) return false;
        await SaveAsync();
        RefreshSessions(Store.Sessions.Values);
        return true;
    }

    public async Task<bool> AddCollectionTagAsync(string collectionId, string tag)
    {
        if (!Store.Collections.TryGetValue(collectionId, out var c)) return false;
        if (!AddTagTo(c.Tags, tag, reserved: false)) return false;
        await SaveAsync();
        return true;
    }

    public async Task<bool> RemoveCollectionTagAsync(string collectionId, string tag)
    {
        if (!Store.Collections.TryGetValue(collectionId, out var c)) return false;
        if (!RemoveTagFrom(c.Tags, tag)) return false;
        await SaveAsync();
        return true;
    }

    // All user chat tags with counts (most-used first, then alpha) - drives the chat filter strip.
    public IReadOnlyList<TagCount> AllChatTags() =>
        Store.Sessions.Values
            .Where(s => !s.Archived)
            .SelectMany(UserTags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TagCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<TagCount> AllCollectionTags() =>
        Store.Collections.Values
            .SelectMany(c => c.Tags.Where(t => !string.IsNullOrWhiteSpace(t)))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TagCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Text search (if any) intersected with a tag filter. matchAll=false => chats with ANY of the tags.
    public IReadOnlyList<ArchiveSession> Filter(string query, IReadOnlyCollection<string>? tags, bool matchAll = false)
    {
        IEnumerable<ArchiveSession> baseSet = string.IsNullOrWhiteSpace(query)
            ? OrderedVisibleSessions(Store.Sessions.Values)
            : Search(query);
        if (tags is null || tags.Count == 0) return baseSet.ToList();
        bool Has(ArchiveSession s, string tag) => s.Tags.Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
        return baseSet
            .Where(s => matchAll ? tags.All(t => Has(s, t)) : tags.Any(t => Has(s, t)))
            .ToList();
    }

    // Generic add/remove against either tag store (ObservableCollection for chats, List for collections).
    private static bool AddTagTo(ICollection<string> store, string tag, bool reserved)
    {
        var t = NormalizeTag(tag);
        if (t.Length == 0 || (reserved && IsReservedTag(t))) return false;
        if (store.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) return false;
        store.Add(t);
        return true;
    }

    private static bool RemoveTagFrom(ICollection<string> store, string tag)
    {
        var match = store.FirstOrDefault(x => string.Equals(x, (tag ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        store.Remove(match);
        return true;
    }

    public AiProviderSettings EnsureAiProvider(string name, string baseUrl, string model)
    {
        var id = Slug(name);
        if (!Store.Settings.AiProviders.Any(provider => provider.Id == id))
        {
            Store.Settings.AiProviders.Add(new AiProviderSettings
            {
                Id = id,
                Name = name,
                BaseUrl = baseUrl,
                Model = model,
                Models = string.IsNullOrWhiteSpace(model) ? new List<string>() : new List<string> { model },
                Kind = "openai-compatible",
                Enabled = true
            });
        }
        Store.Settings.ActiveAiProviderId = id;
        return Store.Settings.AiProviders.First(provider => provider.Id == id);
    }

    public AiProviderSettings? ActiveAiProvider()
    {
        return Store.Settings.AiProviders.FirstOrDefault(provider => provider.Id == Store.Settings.ActiveAiProviderId)
               ?? Store.Settings.AiProviders.FirstOrDefault();
    }

    public IReadOnlyList<ArchiveSearchHit> RetrieveForQuestion(string question, int limit = 10)
    {
        return DeepSearch(question, limit);
    }

    public string CopyPayload(ArchiveSession session, string mode)
    {
        if (mode is not ("path" or "paths")) EnsureContent(session); // restore/code/resume need content (lazy)
        return mode switch
        {
            "code" => session.CodeBlocks.Count == 0
                ? "No code blocks found."
                : string.Join("\n\n", session.CodeBlocks.Select(block => $"```{block.Language}\n{block.Code}\n```")),
            "path" => session.SourcePath,
            "paths" => $"Chat source: {session.SourcePath}\nWorkspace: {session.Workspace}",
            "restore" => RestorePacket(session),
            _ => ResumePrompt(session)
        };
    }

    public string RestorePacket(ArchiveSession session)
    {
        var firstUser = session.Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? session.Title;
        return string.Join("\n\n", new[]
        {
            $"# Restore Packet: {session.DisplayTitle}",
            $"## Goal summary\n{firstUser}",
            $"## Current state\nLast archived activity: {session.UpdatedAt}",
            $"## Important paths\nChat source: {session.SourcePath}\nWorkspace: {session.Workspace}",
            $"## Code blocks\n{CopyPayload(session, "code")}",
            "## Suggested first prompt\nContinue from this restore packet. Treat source paths as read-only and recover the relevant code, decisions, and next actions."
        });
    }

    // Index one folder as Codex rollouts (kept for callers/tests that target a single codex root).
    public async Task<int> IndexRootAsync(string rootPath, IProgress<string>? progress = null, bool refreshList = true)
    {
        if (!Directory.Exists(rootPath))
        {
            progress?.Report("Root does not exist.");
            return 0;
        }
        var (parsed, stamps) = await ParseSourceAsync(new SessionSource { Tool = "codex", Root = rootPath },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), progress);
        return await MergeScanAsync(new DiskScan(parsed, new List<ArchiveSession>()) { Stamps = stamps }, refreshList);
    }

    // Default on-disk stores. Codex rollouts and Claude transcripts both land under the home dir.
    public static string DefaultCodexSessionsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    public static string DefaultClaudeSessionsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static List<SessionSource> DefaultSources() => new()
    {
        new SessionSource { Tool = "codex", Root = DefaultCodexSessionsRoot },
        new SessionSource { Tool = "claude", Root = DefaultClaudeSessionsRoot },
    };

    // The roots to scan: whatever the user/agent configured, else the codex + claude defaults.
    public IReadOnlyList<SessionSource> EffectiveSources() =>
        Store.Settings.Sources.Count > 0 ? Store.Settings.Sources : DefaultSources();

    // Resurface everything in one call (tests + simple callers). UI callers split this across
    // threads: ScanDiskAsync on a worker (no shared state) then MergeScanAsync on the UI thread.
    public async Task<int> SyncFromDiskAsync(IProgress<string>? progress = null, bool refreshList = true)
    {
        var scan = await ScanDiskAsync(progress);
        return await MergeScanAsync(scan, refreshList);
    }

    // OFF-THREAD SAFE: reads files only and returns parsed/changed sessions + current file stamps.
    // Iterates every configured source (codex + claude), skips files whose stamp is unchanged
    // (incremental), and routes parsing by the source's tool. Touches no shared mutable state.
    public async Task<DiskScan> ScanDiskAsync(IProgress<string>? progress = null)
    {
        var bundled = Store.Settings.BundledHistoryAbsorbed ? new List<ArchiveSession>() : LoadBundledHistory(progress);
        // Honor the incremental cache only when the parser version matches; otherwise re-parse all.
        var fullRescan = Store.Settings.IndexVersion != CurrentIndexVersion;
        var known = fullRescan
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(Store.FileStamps, StringComparer.OrdinalIgnoreCase);
        var disk = new List<ArchiveSession>();
        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var anyRoot = false;
        foreach (var src in EffectiveSources())
        {
            if (!src.Enabled || string.IsNullOrWhiteSpace(src.Root) || !Directory.Exists(src.Root)) continue;
            anyRoot = true;
            var (parsed, srcStamps) = await ParseSourceAsync(src, known, progress);
            disk.AddRange(parsed);
            foreach (var kv in srcStamps) stamps[kv.Key] = kv.Value;
        }
        if (!anyRoot) progress?.Report("No session folders found.");
        return new DiskScan(disk, bundled) { Stamps = stamps, FullRescan = fullRescan };
    }

    // Enumerate one source, skip unchanged files (incremental), parse the rest by tool.
    private async Task<(List<ArchiveSession> Parsed, Dictionary<string, string> Stamps)> ParseSourceAsync(
        SessionSource src, IReadOnlyDictionary<string, string> known, IProgress<string>? progress)
    {
        var files = Directory.EnumerateFiles(src.Root, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxIndexedFiles)
            .ToList();
        var parsed = new List<ArchiveSession>();
        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var stamp = file.LastWriteTimeUtc.Ticks + ":" + file.Length;

            // Claude workflow/subagent journals collide on the filename "journal" and aren't chats.
            // Stamp them (so they're "scanned" -> any old orphan entry prunes + we skip them next
            // time) but don't parse them into a session.
            if (src.Tool == "claude" && (file.Name == "journal.jsonl"
                || file.FullName.Contains("\\subagents\\", StringComparison.OrdinalIgnoreCase)))
            {
                stamps[file.FullName] = stamp;
                continue;
            }

            if (known.TryGetValue(file.FullName, out var old) && old == stamp)
            {
                stamps[file.FullName] = stamp; // unchanged: carry the stamp forward, skip the parse
                continue;
            }
            try
            {
                var session = await ParseSessionAsync(file.FullName, src.Tool);
                if (session is not null) parsed.Add(session);
                stamps[file.FullName] = stamp; // stamp only after a successful parse (or intentional sidechain skip)
            }
            catch (Exception ex)
            {
                // Do NOT stamp a failed parse, so a transient/locked read is retried next scan.
                progress?.Report($"Skipped {file.Name}: {ex.Message}");
            }
        }
        progress?.Report($"{src.Tool}: {parsed.Count} new/changed of {files.Count}");
        return (parsed, stamps);
    }

    // Route by tool; auto-detect when a source's tool is unknown.
    private async Task<ArchiveSession?> ParseSessionAsync(string path, string tool)
    {
        var resolved = string.IsNullOrWhiteSpace(tool) || tool == "auto" ? DetectTool(path) : tool;
        return resolved == "claude" ? await ParseClaudeSessionAsync(path) : await ParseJsonlAsync(path);
    }

    // Peek the first useful line: claude transcripts carry a sessionId; codex rollouts carry a payload.
    private static string DetectTool(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("\"sessionId\"")) return "claude";
                if (line.Contains("\"payload\"") || line.Contains("session_meta")) return "codex";
                break;
            }
        }
        catch { }
        return "codex";
    }

    // UI-THREAD: merge scanned sessions into the store. Disk content always refreshes a session,
    // but app-owned organization (custom title, pin, archive, review, star, user tags) is preserved
    // so a re-sync never wipes how you've filed your chats. Bundled history only fills genuine gaps.
    public async Task<int> MergeScanAsync(DiskScan scan, bool refreshList = true)
    {
        var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in scan.Disk)
        {
            if (Store.Sessions.TryGetValue(incoming.Id, out var existing)) PreserveAppFields(existing, incoming);
            Store.Sessions[incoming.Id] = incoming;
            imported.Add(incoming.Id);
        }
        var recovered = 0;
        foreach (var bundled in scan.Bundled)
        {
            if (string.IsNullOrWhiteSpace(bundled.Id) || Store.Sessions.ContainsKey(bundled.Id)) continue;
            Store.Sessions[bundled.Id] = bundled;
            imported.Add(bundled.Id);
            recovered++;
        }
        if (imported.Count > 0)
        {
            RemoveBundledSampleSessions(imported);
            // Read the (slow) sqlite/jsonl titles OFF the UI thread; apply on this (UI) thread so the
            // INotifyPropertyChanged raised by ApplyThreadTitles never fires from a worker. The final
            // RefreshSessions below repaints the list, so we don't refresh here.
            var titles = await Task.Run(LoadThreadTitles);
            ApplyThreadTitles(titles);
        }
        // On a parser-version migration every file was re-parsed: prune sessions whose file WAS
        // scanned but no longer yields that id (old id scheme, or the file is now a skipped sidechain).
        if (scan.FullRescan)
        {
            var scannedPaths = new HashSet<string>(scan.Stamps.Keys, StringComparer.OrdinalIgnoreCase);
            var orphans = Store.Sessions.Values
                .Where(s => scannedPaths.Contains(s.SourcePath) && !imported.Contains(s.Id))
                .Select(s => s.Id)
                .ToList();
            foreach (var id in orphans) Store.Sessions.Remove(id);
        }
        foreach (var kv in scan.Stamps) Store.FileStamps[kv.Key] = kv.Value;
        Store.Settings.BundledHistoryAbsorbed = true;
        Store.Settings.IndexVersion = CurrentIndexVersion;
        await SaveAsync();
        if (refreshList) RefreshSessions(Store.Sessions.Values.OrderByDescending(s => s.UpdatedAt));
        return scan.Disk.Count + recovered;
    }

    // Refresh a session's content from disk while keeping the user's organization intact.
    private static void PreserveAppFields(ArchiveSession existing, ArchiveSession incoming)
    {
        incoming.CustomTitle = existing.CustomTitle;
        incoming.Pinned = existing.Pinned;
        incoming.Archived = existing.Archived;
        incoming.Reviewed = existing.Reviewed;
        incoming.Starred = existing.Starred;
        foreach (var alias in existing.Aliases)
        {
            if (!incoming.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) incoming.Aliases.Add(alias);
        }
        foreach (var tag in existing.Tags)
        {
            if (!incoming.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) incoming.Tags.Add(tag);
        }
    }

    private List<ArchiveSession> LoadBundledHistory(IProgress<string>? progress)
    {
        var list = new List<ArchiveSession>();
        try
        {
            if (string.Equals(_storePath, _bundledStorePath, StringComparison.OrdinalIgnoreCase)) return list;
            if (!File.Exists(_bundledStorePath)) return list;
            var bundled = JsonSerializer.Deserialize<AppStoreData>(File.ReadAllText(_bundledStorePath), _jsonOptions);
            if (bundled is null) return list;
            foreach (var pair in bundled.Sessions)
            {
                if (IsSampleSource(pair.Value.SourcePath)) continue;
                list.Add(pair.Value);
            }
            if (list.Count > 0) progress?.Report($"Recovering {list.Count} archived chats from local history...");
        }
        catch
        {
            // History recovery is best-effort; a missing or partial snapshot must not block a sync.
        }
        return list;
    }

    private static bool IsSampleSource(string sourcePath) =>
        sourcePath.Contains("sample-sessions", StringComparison.OrdinalIgnoreCase)
        || sourcePath.Contains("data\\fixtures", StringComparison.OrdinalIgnoreCase)
        || sourcePath.Contains("data/fixtures", StringComparison.OrdinalIgnoreCase);

    // Everything the UI needs to actually resume a chat in a terminal, routed by which agent owns
    // it: Claude uses `claude --resume <id>`; Codex uses `codex resume --include-non-interactive
    // <id>` (options before the positional id; include-non-interactive so exec rollouts qualify).
    // Both start in the chat's original workspace cwd.
    // A session id is only ever a UUID or a rollout/file token. Anything else (e.g. a crafted
    // filename `x & calc` from a malicious source root) is refused so it can't be injected into the
    // terminal command line.
    public static bool IsResumableId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 200 && Regex.IsMatch(id, "^[A-Za-z0-9._-]+$");

    public ResumeLaunch BuildResumeLaunch(ArchiveSession session, string? exeOverride = null)
    {
        var id = string.IsNullOrWhiteSpace(session.Id) ? Path.GetFileNameWithoutExtension(session.SourcePath) : session.Id;
        var cwd = ResolveWorkingDirectory(session);
        if (!IsResumableId(id))
            return new ResumeLaunch("", "", cwd, "Refused: session id is not a safe token.");

        var isClaude = string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase);
        var exe = !string.IsNullOrWhiteSpace(exeOverride) ? exeOverride!
            : isClaude ? ResolveClaudeExe() : ResolveCodexExe();

        // The terminal opens in the chat's workspace, and cmd resolves a bare command against the
        // current directory before PATH — so a workspace that contains a planted codex.exe/claude.bat
        // could be run. Require a trusted ABSOLUTE existing exe (overrides are caller-trusted, e.g. tests).
        if (string.IsNullOrWhiteSpace(exeOverride) && (!Path.IsPathRooted(exe) || !File.Exists(exe)))
            return new ResumeLaunch("", "", cwd, $"Refused: the {(isClaude ? "claude" : "codex")} CLI was not found at a trusted path.");

        var args = isClaude ? $"--resume {id}" : $"resume --include-non-interactive {id}";
        return new ResumeLaunch(exe, args, cwd, $"\"{exe}\" {args}");
    }

    public static string ResolveClaudeExe()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(local) ? local : "claude";
    }

    private static string ResolveWorkingDirectory(ArchiveSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Workspace) && Directory.Exists(session.Workspace)) return session.Workspace;
        var sourceDir = Path.GetDirectoryName(session.SourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDir) && Directory.Exists(sourceDir)) return sourceDir;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string ResolveCodexExe()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin", "codex.exe");
        return File.Exists(local) ? local : "codex";
    }

    // The raw rollout timeline: one entry per recorded event (messages, tool/function calls,
    // command runs, reasoning) so the Source inspector shows what actually happened, not a path.
    // Off-thread variant: the Source inspector reads + JSON-parses up to `limit` lines, which must
    // not happen on the UI thread for a large rollout.
    public Task<IReadOnlyList<RawEvent>> ReadEventsAsync(ArchiveSession session, int limit = 400)
        => Task.Run(() => ReadEvents(session, limit));

    public IReadOnlyList<RawEvent> ReadEvents(ArchiveSession session, int limit = 400)
    {
        var events = new List<RawEvent>();
        if (string.IsNullOrWhiteSpace(session.SourcePath) || !File.Exists(session.SourcePath)) return events;
        try
        {
            using var stream = new FileStream(session.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while (events.Count < limit && (line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineChars) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    try
                    {
                        var root = doc.RootElement;
                        var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                        if (!root.TryGetProperty("payload", out var payload)) continue;
                        // Prefer payload.type; fall back to the line's root type (e.g. session_meta).
                        var kind =
                            payload.TryGetProperty("type", out var typeProp) && typeProp.GetString() is { Length: > 0 } pt ? pt :
                            root.TryGetProperty("type", out var rootType) ? rootType.GetString() ?? "event" : "event";
                        if (kind == "message" && payload.TryGetProperty("role", out var roleProp))
                            kind = "message:" + (roleProp.GetString() ?? "");
                        events.Add(new RawEvent(kind, timestamp, EventPreview(kind, payload)));
                    }
                    catch
                    {
                        // One malformed event must not abort the rest of the timeline.
                    }
                }
            }
        }
        catch
        {
            // Source files are read-only previews; a locked or partial file just yields fewer events.
        }
        return events;
    }

    // Best-effort human preview across the many rollout payload shapes (messages, tool calls,
    // command runs, patch/mcp results). Never throws and never returns raw JSON for the common cases.
    private static string EventPreview(string kind, JsonElement payload)
    {
        string raw =
            Field(payload, "message") is { Length: > 0 } msg ? msg :
            payload.TryGetProperty("content", out _) ? ExtractContent(payload) :
            CommandText(payload) is { Length: > 0 } cmd ? cmd :
            Field(payload, "output") is { Length: > 0 } outp ? outp :
            Field(payload, "stdout") is { Length: > 0 } so ? so :
            Field(payload, "stderr") is { Length: > 0 } se ? se :
            Field(payload, "input") is { Length: > 0 } inp ? inp :
            Field(payload, "arguments") is { Length: > 0 } argp ? argp :
            Field(payload, "result") is { Length: > 0 } res ? res :
            Field(payload, "last_agent_message") is { Length: > 0 } lam ? lam :
            Field(payload, "name") is { Length: > 0 } nm ? nm :
            Field(payload, "text") is { Length: > 0 } tx ? tx :
            "";
        raw = Regex.Replace(raw.Trim(), "\\s+", " ");
        return raw.Length > 200 ? raw[..200] + "..." : raw;
    }

    private static string Field(JsonElement payload, string prop)
    {
        if (!payload.TryGetProperty(prop, out var v)) return "";
        return ElementText(v);
    }

    private static string ElementText(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            JsonValueKind.Object or JsonValueKind.Array => v.ToString(),
            _ => ""
        };

    private static string ElementString(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string CommandText(JsonElement payload)
    {
        if (!payload.TryGetProperty("command", out var c)) return "";
        return c.ValueKind == JsonValueKind.Array
            ? string.Join(" ", c.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()))
            : c.ToString();
    }

    private async Task<ArchiveSession> ParseJsonlAsync(string filePath)
    {
        var messages = new ObservableCollection<ArchiveMessage>();
        var codeBlocks = new ObservableCollection<CodeBlock>();
        var fallbackMessages = new ObservableCollection<ArchiveMessage>();
        var fallbackCodeBlocks = new ObservableCollection<CodeBlock>();
        var id = Path.GetFileNameWithoutExtension(filePath);
        var cwd = "";
        var created = "";
        var updated = "";

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fallbackSeen = new HashSet<string>(StringComparer.Ordinal);
        var sawEventMessages = false;
        var info = new FileInfo(filePath);
        updated = info.LastWriteTimeUtc.ToString("O");
        ArchiveMessage? lastTool = null;   // the function_call awaiting its function_call_output

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineCount = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            lineCount++;
            if (lineCount > MaxLinesPerSession) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > MaxLineChars) continue;
            JsonDocument doc;
            // A partial/malformed line — common at the tail of an in-progress rollout, i.e. the
            // very session you most want to resume — must not discard the whole file. Skip the line.
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(created)) created = timestamp;
                if (!string.IsNullOrWhiteSpace(timestamp)) updated = timestamp;

                if (root.TryGetProperty("payload", out var payload))
                {
                    // The session id (== Codex thread id, i.e. CODEX_THREAD_ID, which agents pass as "id")
                    // comes ONLY from the session_meta header line. Later payload lines carry per-item
                    // "id" values (rs_... response ids) that must NOT overwrite it - that mis-keyed every
                    // session and is why exact-id self-add could never match.
                    var rootType = root.TryGetProperty("type", out var rtProp) ? ElementString(rtProp) : null;
                    if (rootType == "session_meta")
                    {
                        var sessionMetaAssigned = false;
                        if (payload.TryGetProperty("session_id", out var sidProp) && ElementString(sidProp) is { Length: > 0 } sid)
                        {
                            id = sid;
                            sessionMetaAssigned = true;
                            AddAlias(aliases, sid);
                        }
                        if (payload.TryGetProperty("id", out var midProp) && ElementString(midProp) is { Length: > 0 } mid)
                        {
                            if (!sessionMetaAssigned) id = mid;
                            AddAlias(aliases, mid);
                        }
                        if (payload.TryGetProperty("forked_from_id", out var forkProp) && ElementString(forkProp) is { Length: > 0 } fork)
                        {
                            AddAlias(aliases, fork);
                        }
                    }
                    if (payload.TryGetProperty("cwd", out var cwdProp) && ElementString(cwdProp) is { Length: > 0 } cw) cwd = cw;

                    var pType = payload.TryGetProperty("type", out var typeProp) ? ElementString(typeProp) : null;
                    if (rootType == "event_msg")
                    {
                        // Clean turn-level text. We take user/assistant text ONLY from event_msg; the
                        // parallel response_item "message" lines are the same text again (different
                        // timestamps) and are what made every turn show up twice.
                        if (pType is "user_message" or "agent_message")
                        {
                            sawEventMessages = true;
                            var role = pType == "agent_message" ? "assistant" : "user";
                            var text = Field(payload, "message");
                            AddMessage(messages, codeBlocks, role, text, timestamp, seen);
                        }
                    }
                    else if (rootType == "response_item")
                    {
                        // Tool calls become one compact, collapsible step (command + its output), not a
                        // wall of separate bubbles. response_item "message"/"reasoning" are skipped.
                        if (pType == "function_call")
                        {
                            var name = Field(payload, "name");
                            if (string.IsNullOrWhiteSpace(name)) name = "tool";
                            lastTool = AddToolStep(messages, name, ExtractToolCommand(payload), timestamp);
                        }
                        else if (pType == "function_call_output" && lastTool is not null)
                        {
                            var output = Field(payload, "output");
                            lastTool.ToolOutput = CapDisplayText(output, 4000);
                        }
                        else if (pType == "message")
                        {
                            var role = Field(payload, "role");
                            if (string.IsNullOrWhiteSpace(role)) role = "message";
                            if (IsIndexedRole(role)) AddMessage(fallbackMessages, fallbackCodeBlocks, role, ExtractContent(payload), timestamp, fallbackSeen);
                        }
                    }
                }
            }
        }

        if (!sawEventMessages && fallbackMessages.Count > 0)
        {
            var merged = fallbackMessages
                .Concat(messages.Where(m => m.EffectiveKind == "tool"))
                .OrderBy(m => m.Timestamp, StringComparer.Ordinal)
                .ToList();
            messages = new ObservableCollection<ArchiveMessage>(merged);
            codeBlocks = new ObservableCollection<CodeBlock>(fallbackCodeBlocks.Concat(merged.SelectMany(m => m.CodeBlocks)));
        }

        if (string.IsNullOrWhiteSpace(created)) created = info.CreationTimeUtc.ToString("O");
        if (string.IsNullOrWhiteSpace(updated)) updated = info.LastWriteTimeUtc.ToString("O");

        // Title comes from the first prompt, captured from the FULL message list before we trim to the
        // recent window (so a long chat keeps its real opening title).
        var title = CleanFallbackTitle(messages.FirstOrDefault(m => m.Role == "user" && IsTitleCandidate(m.Text))?.Text ?? Path.GetFileNameWithoutExtension(filePath));
        var total = messages.Count;
        (messages, codeBlocks) = KeepRecentWindow(messages, codeBlocks);
        return new ArchiveSession
        {
            Id = id,
            Title = title,
            SourcePath = filePath,
            Aliases = new ObservableCollection<string>(aliases.Where(a => !string.Equals(a, id, StringComparison.OrdinalIgnoreCase))),
            CreatedAt = created,
            UpdatedAt = updated,
            Workspace = string.IsNullOrWhiteSpace(cwd) ? "Unknown workspace" : cwd,
            WorkspaceName = string.IsNullOrWhiteSpace(cwd) ? "Unknown" : Path.GetFileName(cwd.TrimEnd('\\', '/')),
            Model = "codex",
            Tool = "codex",
            Messages = messages,
            CodeBlocks = codeBlocks,
            MessageCount = total,
            ContentLoaded = true,
            Text = CapText(string.Join("\n\n", messages.Select(m => m.Text))),
            Tags = new ObservableCollection<string>(codeBlocks.Count > 0 ? new[] { "archive", "code" } : new[] { "archive" })
        };
    }

    private static void AddAlias(HashSet<string> aliases, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (IsAliasToken(trimmed)) aliases.Add(trimmed);
    }

    // Claude Code transcript: one JSON object per line with sessionId/cwd/timestamp and a
    // message{role,content[]}. content is an array of {type:"text",text} blocks (plus tool_use/
    // tool_result we skip for the reader). Mirrors ParseJsonlAsync but for Claude's shape.
    private async Task<ArchiveSession?> ParseClaudeSessionAsync(string filePath)
    {
        var messages = new ObservableCollection<ArchiveMessage>();
        var codeBlocks = new ObservableCollection<CodeBlock>();
        // The filename IS Claude's resumable session id; the in-line sessionId is shared by a
        // session's sidechain (subagent) files, so we never key off it.
        var id = Path.GetFileNameWithoutExtension(filePath);
        var cwd = "";
        var created = "";
        var summary = "";
        var isSidechain = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var toolUseById = new Dictionary<string, ArchiveMessage>(StringComparer.Ordinal);  // tool_use id -> step (output attached from the later tool_result)
        var info = new FileInfo(filePath);
        var updated = info.LastWriteTimeUtc.ToString("O");

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineCount = 0;
        var hitLineCap = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            lineCount++;
            if (lineCount > MaxLinesPerSession) { hitLineCap = true; break; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > MaxLineChars) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                try
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("isSidechain", out var scProp) && scProp.ValueKind == JsonValueKind.True) isSidechain = true;
                    if (root.TryGetProperty("cwd", out var cwdProp) && cwdProp.GetString() is { Length: > 0 } cw) cwd = cw;
                    var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(created) && !string.IsNullOrWhiteSpace(timestamp)) created = timestamp;
                    if (!string.IsNullOrWhiteSpace(timestamp)) updated = timestamp;

                    var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                    if (type == "summary" && root.TryGetProperty("summary", out var sumProp))
                    {
                        summary = sumProp.GetString() ?? summary;
                        continue;
                    }
                    if (type != "user" && type != "assistant") continue;
                    if (!root.TryGetProperty("message", out var message)) continue;
                    var role = message.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? type : type;
                    if (!IsIndexedRole(role)) continue;
                    ProcessClaudeMessage(message, role, timestamp, messages, codeBlocks, seen, toolUseById);
                }
                catch
                {
                    // One bad line never discards the session.
                }
            }
        }

        // Sidechain (subagent) transcripts aren't independently resumable conversations — skip them
        // so the list shows one entry per real chat instead of hundreds of subagent fragments.
        if (isSidechain) return null;

        var titleSeed = FirstMeaningfulUserText(messages);
        if (hitLineCap)
        {
            var tail = ParseClaudeTail(filePath, info);
            if (tail.messages.Count > 0)
            {
                messages = tail.messages;
                codeBlocks = tail.codeBlocks;
                if (!string.IsNullOrWhiteSpace(tail.cwd)) cwd = tail.cwd;
                if (!string.IsNullOrWhiteSpace(tail.summary)) summary = tail.summary;
                if (!string.IsNullOrWhiteSpace(tail.updated)) updated = tail.updated;
            }
        }

        if (string.IsNullOrWhiteSpace(created)) created = info.CreationTimeUtc.ToString("O");
        // The session's real name: a user/agent custom-title wins, then Claude's ai-title, then a
        // summary record, then the first prompt. Custom/ai titles are appended at the file TAIL (often
        // far past the message window), so scan the tail for them rather than relying on the forward parse.
        var (tailCustom, tailAi) = ClaudeTailTitle(filePath);
        var titleSource =
            !string.IsNullOrWhiteSpace(tailCustom) ? tailCustom! :
            !string.IsNullOrWhiteSpace(tailAi) ? tailAi! :
            !string.IsNullOrWhiteSpace(summary) ? summary :
            titleSeed ?? FirstMeaningfulUserText(messages) ?? Path.GetFileNameWithoutExtension(filePath);
        var title = CleanTitle(titleSource);
        var total = messages.Count;
        (messages, codeBlocks) = KeepRecentWindow(messages, codeBlocks);
        return new ArchiveSession
        {
            Id = id,
            Title = title,
            SourcePath = filePath,
            CreatedAt = created,
            UpdatedAt = updated,
            Workspace = string.IsNullOrWhiteSpace(cwd) ? "Unknown workspace" : cwd,
            WorkspaceName = string.IsNullOrWhiteSpace(cwd) ? "Unknown" : Path.GetFileName(cwd.TrimEnd('\\', '/')),
            Model = "claude",
            Tool = "claude",
            Messages = messages,
            CodeBlocks = codeBlocks,
            MessageCount = total,
            ContentLoaded = true,
            Text = CapText(string.Join("\n\n", messages.Select(m => m.Text))),
            Tags = new ObservableCollection<string>(codeBlocks.Count > 0 ? new[] { "archive", "code" } : new[] { "archive" })
        };
    }

    private static (ObservableCollection<ArchiveMessage> messages, ObservableCollection<CodeBlock> codeBlocks, string cwd, string summary, string updated)
        ParseClaudeTail(string path, FileInfo info)
    {
        var messages = new ObservableCollection<ArchiveMessage>();
        var codeBlocks = new ObservableCollection<CodeBlock>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var toolUseById = new Dictionary<string, ArchiveMessage>(StringComparer.Ordinal);
        var cwd = "";
        var summary = "";
        var updated = info.LastWriteTimeUtc.ToString("O");

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var n = (int)Math.Min(fs.Length, ClaudeTailBytes);
            if (n <= 0) return (messages, codeBlocks, cwd, summary, updated);
            var offset = fs.Length - n;
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[n];
            var read = fs.Read(buf, 0, n);
            var lines = Encoding.UTF8.GetString(buf, 0, read).Replace("\r\n", "\n").Split('\n');
            var usable = lines.Skip(offset > 0 ? 1 : 0).ToList(); // first line is partial when starting mid-file
            if (usable.Count > MaxLinesPerSession) usable = usable.Skip(usable.Count - MaxLinesPerSession).ToList();

            foreach (var line in usable)
            {
                if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineChars) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    try
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("cwd", out var cwdProp) && cwdProp.GetString() is { Length: > 0 } cw) cwd = cw;
                        var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(timestamp)) updated = timestamp;

                        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                        if (type == "summary" && root.TryGetProperty("summary", out var sumProp))
                        {
                            summary = sumProp.GetString() ?? summary;
                            continue;
                        }
                        if (type != "user" && type != "assistant") continue;
                        if (!root.TryGetProperty("message", out var message)) continue;
                        var role = message.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? type : type;
                        if (!IsIndexedRole(role)) continue;
                        ProcessClaudeMessage(message, role, timestamp, messages, codeBlocks, seen, toolUseById);
                    }
                    catch
                    {
                        // One bad line never discards the tail window.
                    }
                }
            }
        }
        catch
        {
            // Tail parsing is best-effort; the head parse remains usable if this fails.
        }

        return (messages, codeBlocks, cwd, summary, updated);
    }

    // Claude message.content is usually an array of typed blocks; keep the readable text ones.
    private static string ExtractClaudeContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) { builder.AppendLine(item.GetString()); continue; }
            if (item.ValueKind != JsonValueKind.Object) continue;
            var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;
            if (itemType == "text" && item.TryGetProperty("text", out var text)) builder.AppendLine(text.GetString());
            else if (itemType == "tool_result" && item.TryGetProperty("content", out var tr))
            {
                if (tr.ValueKind == JsonValueKind.String) builder.AppendLine(tr.GetString());
            }
        }
        return builder.ToString();
    }

    // Parse one Claude message into clean chat entries: text becomes a bubble; each tool_use becomes a
    // compact tool step (with its output attached from the later tool_result); a message that is only a
    // tool_result is NOT shown as a bubble (its output rides on the tool step). This is what turns a
    // wall of tool dumps into a readable conversation.
    private static void ProcessClaudeMessage(JsonElement message, string role, string timestamp,
        ObservableCollection<ArchiveMessage> messages, ObservableCollection<CodeBlock> codeBlocks,
        HashSet<string> seen, Dictionary<string, ArchiveMessage> toolUseById)
    {
        if (!message.TryGetProperty("content", out var content)) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            AddMessage(messages, codeBlocks, role, content.GetString() ?? "", timestamp, seen);
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        var textSb = new StringBuilder();
        void FlushText()
        {
            if (textSb.Length == 0) return;
            AddMessage(messages, codeBlocks, role, textSb.ToString(), timestamp, seen);
            textSb.Clear();
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) { textSb.AppendLine(item.GetString()); continue; }
            if (item.ValueKind != JsonValueKind.Object) continue;
            var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;
            if (itemType == "text" && item.TryGetProperty("text", out var t))
            {
                textSb.AppendLine(t.GetString());
            }
            else if (itemType == "tool_use")
            {
                FlushText();   // keep order: any text before the call shows first
                var name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "tool" : "tool";
                var step = AddToolStep(messages, name, ClaudeToolCommand(item), timestamp);
                if (item.TryGetProperty("id", out var idp) && idp.GetString() is { Length: > 0 } tid) toolUseById[tid] = step;
            }
            else if (itemType == "tool_result")
            {
                var outText = ExtractToolResultText(item);
                if (item.TryGetProperty("tool_use_id", out var tup) && tup.GetString() is { Length: > 0 } tuid
                    && toolUseById.TryGetValue(tuid, out var step))
                    step.ToolOutput = CapDisplayText(outText, 4000);
                // a tool_result with no matching call is dropped (it's noise, not a chat turn)
            }
        }
        FlushText();
    }

    // A short label for a Claude tool call from its input (the command / file / pattern it acted on).
    private static string ClaudeToolCommand(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in new[] { "command", "file_path", "path", "pattern", "url", "query", "prompt" })
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                return s;
        return CapDisplayText(input.ToString(), 400);
    }

    private static string ExtractToolResultText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var b in c.EnumerateArray())
            if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("text", out var t)) sb.AppendLine(t.GetString());
        return sb.ToString();
    }

    private static bool IsIndexedRole(string role)
    {
        return role.Equals("user", StringComparison.OrdinalIgnoreCase)
            || role.Equals("assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddMessage(ObservableCollection<ArchiveMessage> messages, ObservableCollection<CodeBlock> codeBlocks, string role, string text, string timestamp, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var capped = text.Length > 4000 ? text[..4000] + "\n\n[Truncated in native index.]" : text;
        var duplicateKey = $"{role}\n{timestamp}\n{capped}";
        if (!seen.Add(duplicateKey)) return;
        var blocks = ExtractCodeBlocks(capped);
        foreach (var block in blocks) codeBlocks.Add(block);
        messages.Add(new ArchiveMessage
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Role = role,
            Text = capped,
            Timestamp = timestamp,
            CodeBlocks = new ObservableCollection<CodeBlock>(blocks)
        });
    }

    // A consolidated tool step: one command + (later) its output, rendered as a single collapsible line
    // instead of separate bubbles.
    private static ArchiveMessage AddToolStep(ObservableCollection<ArchiveMessage> messages, string name, string command, string timestamp)
    {
        var step = new ArchiveMessage
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Role = "tool",
            Kind = "tool",
            ToolName = FriendlyToolName(name),
            Text = CapDisplayText(command, 1200),
            Timestamp = timestamp
        };
        messages.Add(step);
        return step;
    }

    private static string FriendlyToolName(string name) => name switch
    {
        "shell_command" or "shell" or "exec_command" or "local_shell" => "shell",
        "apply_patch" or "edit_file" or "write_file" => "edit",
        "read_file" or "view" => "read",
        "update_plan" => "plan",
        _ => string.IsNullOrWhiteSpace(name) ? "tool" : name
    };

    // Codex tool args arrive as a JSON string, e.g. {"command":["bash","-lc","..."]} or {"path":"..."}.
    private static string ExtractToolCommand(JsonElement payload)
    {
        if (!payload.TryGetProperty("arguments", out var argsProp)) return "";
        var raw = ElementText(argsProp);
        try
        {
            using var d = JsonDocument.Parse(raw);
            var r = d.RootElement;
            if (r.TryGetProperty("command", out var c))
            {
                if (c.ValueKind == JsonValueKind.Array)
                    return string.Join(" ", c.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()));
                if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? raw;
            }
            if (r.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String) return p.GetString() ?? raw;
        }
        catch { }
        return raw;
    }

    private static string CapDisplayText(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "\n…(truncated)";

    // Trim a parsed transcript to its most recent messages so a long chat opens on its latest turns
    // (memory stays bounded). Code blocks follow the kept messages. The caller captures the title from
    // the full list BEFORE calling this, so trimming never loses the opening prompt.
    private static (ObservableCollection<ArchiveMessage> messages, ObservableCollection<CodeBlock> codeBlocks)
        KeepRecentWindow(ObservableCollection<ArchiveMessage> messages, ObservableCollection<CodeBlock> codeBlocks)
    {
        if (messages.Count <= RecentMessageWindow) return (messages, codeBlocks);
        var keep = messages.Skip(messages.Count - RecentMessageWindow).ToList();
        return (new ObservableCollection<ArchiveMessage>(keep),
                new ObservableCollection<CodeBlock>(keep.SelectMany(m => m.CodeBlocks)));
    }

    // Read the last 96KB of a Claude transcript for the latest custom-title / ai-title record. Renames
    // are appended at the file TAIL (often far past the message window), so the forward parse misses
    // them; this is how the reader and list show the real session name instead of the first prompt.
    private static (string? custom, string? ai) ClaudeTailTitle(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var n = (int)Math.Min(fs.Length, 96 * 1024);
            if (n <= 0) return (null, null);
            fs.Seek(-n, SeekOrigin.End);
            var buf = new byte[n];
            var read = fs.Read(buf, 0, n);
            string? custom = null, ai = null;
            foreach (var line in Encoding.UTF8.GetString(buf, 0, read).Split('\n'))
            {
                if (line.IndexOf("Title", StringComparison.Ordinal) < 0) continue;
                try
                {
                    using var d = JsonDocument.Parse(line);
                    var r = d.RootElement;
                    if (r.TryGetProperty("customTitle", out var cu) && cu.GetString() is { Length: > 0 } cuv) custom = cuv;       // latest wins
                    else if (r.TryGetProperty("aiTitle", out var at) && at.GetString() is { Length: > 0 } atv) ai = atv;
                }
                catch { }
            }
            return (custom, ai);
        }
        catch { return (null, null); }
    }

    private void RemoveBundledSampleSessions(HashSet<string> importedIds)
    {
        var sampleIds = Store.Sessions.Values
            .Where(session => !importedIds.Contains(session.Id))
            .Where(session =>
                session.SourcePath.Contains("sample-sessions", StringComparison.OrdinalIgnoreCase)
                || session.SourcePath.Contains("data\\fixtures", StringComparison.OrdinalIgnoreCase)
                || session.SourcePath.Contains("data/fixtures", StringComparison.OrdinalIgnoreCase))
            .Select(session => session.Id)
            .ToList();

        foreach (var id in sampleIds)
        {
            Store.Sessions.Remove(id);
        }
    }

    private static string ExtractContent(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return content.ToString();
        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            // Array items can be primitives, not just objects — TryGetProperty would throw on those.
            if (item.ValueKind == JsonValueKind.String) { builder.AppendLine(item.GetString()); continue; }
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("text", out var text)) builder.AppendLine(text.GetString());
            else if (item.TryGetProperty("input_text", out var input)) builder.AppendLine(input.GetString());
            else if (item.TryGetProperty("output_text", out var output)) builder.AppendLine(output.GetString());
        }
        return builder.ToString();
    }

    private static readonly Regex NoiseBlocks = new(
        "<(environment_context|goal_context|ide_selection|ide_opened_file|ide_diagnostics|system-reminder|user_instructions|context|command-message|command-name|command-args|local-command-stdout|local-command-stderr)>[\\s\\S]*?</\\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // First user message that reads like a real prompt (machine-context noise stripped) — used for
    // the chat title so the list shows what the chat is about, not an IDE/context preamble.
    private static string? FirstMeaningfulUserText(IEnumerable<ArchiveMessage> messages)
    {
        foreach (var message in messages.Where(m => m.Role == "user"))
        {
            var clean = ForReading(message.Text);
            if (clean.Length >= 8 && !clean.StartsWith("<")) return clean;
        }
        return null;
    }

    // Strip the machine-context noise (IDE/environment/goal/system-reminder blocks) and fenced
    // code from a message so the readable human conversation is what surfaces in the reader.
    public static string ForReading(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = Regex.Replace(text, "```[\\s\\S]*?```", "").Trim();
        value = NoiseBlocks.Replace(value, "");
        value = Regex.Replace(value, "(?im)^#\\s*Context from my IDE setup:.*$", "");
        value = Regex.Replace(value, "\\n{3,}", "\n\n").Trim();
        return value;
    }

    private static List<CodeBlock> ExtractCodeBlocks(string text)
    {
        return Regex.Matches(text, "```([a-zA-Z0-9_+.-]*)\\n([\\s\\S]*?)```")
            .Select(match => new CodeBlock
            {
                Language = string.IsNullOrWhiteSpace(match.Groups[1].Value) ? "text" : match.Groups[1].Value,
                Code = match.Groups[2].Value.Trim()
            })
            .ToList();
    }

    private static bool IsTitleCandidate(string text)
    {
        var value = text.Trim();
        return value.Length > 0 && !value.StartsWith("<environment_context>") && !value.StartsWith("<goal_context>");
    }

    private static string CleanTitle(string text)
    {
        var value = Regex.Replace(text.Trim(), "\\s+", " ");
        return value[..Math.Min(120, value.Length)];
    }

    private static string CleanFallbackTitle(string text)
    {
        var value = Regex.Replace(text.Trim(), "\\s+", " ");
        value = Regex.Replace(value, @"^#\s*Context from my IDE setup:\s*", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"^##\s*Open tabs:\s*-\s*", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"^<environment_context>.*?</environment_context>\s*", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (string.IsNullOrWhiteSpace(value)) value = "Untitled chat";
        return CleanTitle(value);
    }

    private static string SearchText(ArchiveSession session)
    {
        return $"{session.Id}\n{session.DisplayTitle}\n{session.Title}\n{session.Text}\n{session.SourcePath}\n{session.Workspace}\n{string.Join(' ', session.Tags)}";
    }

    private static IEnumerable<ArchiveSearchHit> DeepHitsForSession(ArchiveSession session, string[] terms)
    {
        var sessionScore = FuzzyScore($"{session.DisplayTitle}\n{session.WorkspaceName}\n{session.Workspace}\n{session.SourcePath}\n{string.Join(' ', session.Tags)}", terms);
        if (sessionScore > 0)
        {
            yield return new ArchiveSearchHit
            {
                Session = session,
                SourceLabel = "title/path/tags",
                Snippet = MakeSnippet($"{session.DisplayTitle}\n{session.Workspace}\n{session.SourcePath}", terms),
                MatchedTerms = MatchedTerms($"{session.DisplayTitle}\n{session.Workspace}\n{session.SourcePath}", terms),
                Score = sessionScore + 20
            };
        }

        // Content match against the capped, in-memory search text (the full transcript lazy-loads on open,
        // so we don't hold every message of every chat in memory just to search).
        var contentScore = FuzzyScore(session.Text, terms);
        if (contentScore > 0)
        {
            yield return new ArchiveSearchHit
            {
                Session = session,
                SourceLabel = "chat content",
                Snippet = MakeSnippet(session.Text, terms),
                MatchedTerms = MatchedTerms(session.Text, terms),
                Score = contentScore
            };
        }

        foreach (var block in session.CodeBlocks)
        {
            var score = FuzzyScore(block.Code, terms);
            if (score <= 0) continue;
            yield return new ArchiveSearchHit
            {
                Session = session,
                SourceLabel = string.IsNullOrWhiteSpace(block.Language) ? "code" : $"code:{block.Language}",
                Snippet = MakeSnippet(block.Code, terms),
                MatchedTerms = MatchedTerms(block.Code, terms),
                Score = score + 8
            };
        }
    }

    private static string[] QueryTerms(string query)
    {
        return Regex.Matches(query.ToLowerInvariant(), "[a-z0-9_./\\\\:-]+")
            .Select(match => match.Value)
            .Where(term => term.Length > 1)
            .Distinct()
            .Take(12)
            .ToArray();
    }

    private static int FuzzyScore(string text, string[] terms)
    {
        if (terms.Length == 0 || string.IsNullOrWhiteSpace(text)) return 0;
        var normalized = Regex.Replace(text.ToLowerInvariant(), "\\s+", " ");
        var score = 0;
        foreach (var term in terms)
        {
            if (normalized.Contains(term))
            {
                score += term.Length >= 5 ? 20 : 12;
                continue;
            }

            var compactTerm = Regex.Replace(term, "[^a-z0-9]", "");
            if (compactTerm.Length >= 4 && IsLooseSubsequence(compactTerm, normalized))
            {
                score += 7;
                continue;
            }

            if (compactTerm.Length >= 5 && HasCloseToken(compactTerm, normalized))
            {
                score += 5;
            }
        }
        return score;
    }

    private static bool IsLooseSubsequence(string needle, string haystack)
    {
        var index = 0;
        foreach (var ch in haystack)
        {
            if (index < needle.Length && ch == needle[index]) index++;
            if (index == needle.Length) return true;
        }
        return false;
    }

    private static bool HasCloseToken(string term, string text)
    {
        foreach (Match match in Regex.Matches(text, "[a-z0-9_./\\\\:-]+"))
        {
            var token = Regex.Replace(match.Value, "[^a-z0-9]", "");
            if (token.Length < 4 || Math.Abs(token.Length - term.Length) > 2) continue;
            if (LevenshteinDistanceWithin(term, token, 2)) return true;
        }
        return false;
    }

    private static bool LevenshteinDistanceWithin(string left, string right, int maxDistance)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }
            if (best > maxDistance) return false;
            (previous, current) = (current, previous);
        }
        return previous[right.Length] <= maxDistance;
    }

    private static string MakeSnippet(string text, string[] terms)
    {
        var clean = Regex.Replace(text ?? "", "\\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(clean)) return "";

        var index = -1;
        foreach (var term in terms)
        {
            index = clean.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) break;
        }
        if (index < 0) index = 0;

        var start = Math.Max(0, index - 120);
        var length = Math.Min(clean.Length - start, 280);
        var snippet = clean.Substring(start, length);
        if (start > 0) snippet = "... " + snippet;
        if (start + length < clean.Length) snippet += " ...";
        return snippet;
    }

    private static string MatchedTerms(string text, string[] terms)
    {
        var matches = terms
            .Where(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToArray();
        return matches.Length == 0 ? "fuzzy match" : string.Join(", ", matches);
    }

    private static int Score(ArchiveSession session, string[] terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (session.DisplayTitle.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 8;
            if (session.CodeBlocks.Any(block => block.Code.Contains(term, StringComparison.OrdinalIgnoreCase))) score += 5;
            if (session.SourcePath.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 3;
        }
        return score;
    }

    // A prompt you paste into a fresh Claude/Codex agent to pick this chat back up. The source
    // transcript is treated as AUTHORITATIVE and the snippets as a mere preview, so the agent reads
    // the full file and reconstructs the real task instead of acting on a truncated excerpt.
    private string ResumePrompt(ArchiveSession session)
    {
        var preview = string.Join("\n", session.Messages.TakeLast(6).Select(m =>
        {
            var clean = Regex.Replace(m.Text ?? "", "\\s+", " ").Trim();
            return $"- {m.Role}: {clean[..Math.Min(260, clean.Length)]}";
        }));

        return
            "Continue this archived work from local context.\n\n" +
            "Your first step:\n" +
            $"Read the archived transcript at:\n{session.SourcePath}\n\n" +
            $"Workspace:\n{session.Workspace}\n\n" +
            $"Chat title:\n{session.DisplayTitle}\n\n" +
            "Goal:\n" +
            "Resume the previous conversation from that transcript. Reconstruct the latest user request, the " +
            "current task state, the relevant files and commands, and any unresolved next steps before you act.\n\n" +
            "Important:\n" +
            "The snippets below are only a recent preview and may be truncated or incomplete. The source " +
            "transcript above is authoritative - read it first, do not act on the preview alone.\n\n" +
            "Preview of recent context:\n" +
            (string.IsNullOrWhiteSpace(preview) ? "- (no preview available - read the transcript)" : preview) +
            "\n\nAfter reading the transcript, briefly state what you believe the active task is, then continue from there.";
    }

    private bool NormalizeSettings()
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(Store.Settings.Theme))
        {
            Store.Settings.Theme = "amoled";
            changed = true;
        }
        var oldBlueAccent = "codex-" + "blue";
        if (Store.Settings.Accent is "mint" or "" or null || Store.Settings.Accent == oldBlueAccent)
        {
            Store.Settings.Accent = "rose";
            Store.Settings.AccentHex = "#fb7185";
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(Store.Settings.Radius))
        {
            Store.Settings.Radius = "compact";
            changed = true;
        }
        if (Store.Settings.PanelRadius == 0)
        {
            Store.Settings.PanelRadius = Store.Settings.Radius == "compact" ? 12 : Store.Settings.Radius == "rounded" ? 18 : 24;
            changed = true;
        }
        if (Store.Settings.AiProviders.Count == 0)
        {
            Store.Settings.AiProviders.Add(new AiProviderSettings
            {
                Id = "deepseek",
                Name = "DeepSeek",
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-v4-flash",
                Models = new List<string> { "deepseek-v4-flash" },
                Kind = "openai-compatible",
                Enabled = true
            });
            Store.Settings.AiProviders.Add(new AiProviderSettings
            {
                Id = "openai",
                Name = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-4.1-mini",
                Models = new List<string> { "gpt-4.1-mini" },
                Kind = "openai-compatible",
                Enabled = true
            });
            Store.Settings.ActiveAiProviderId = "deepseek";
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(Store.Settings.ActiveAiProviderId) ||
            Store.Settings.AiProviders.All(provider => provider.Id != Store.Settings.ActiveAiProviderId))
        {
            Store.Settings.ActiveAiProviderId = Store.Settings.AiProviders.First().Id;
            changed = true;
        }
        Store.Settings.ReadOnlySourceMode = true;
        return changed;
    }

    private bool ApplyThreadTitles(Dictionary<string, ThreadTitle> titles)
    {
        if (titles.Count == 0) return false;

        var changed = false;
        foreach (var session in Store.Sessions.Values)
        {
            if (!titles.TryGetValue(session.Id, out var title) || string.IsNullOrWhiteSpace(title.Name)) continue;
            var clean = CleanTitle(title.Name);
            if (!string.Equals(session.Title, clean, StringComparison.Ordinal))
            {
                session.Title = clean;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(title.UpdatedAt) && string.CompareOrdinal(title.UpdatedAt, session.UpdatedAt) > 0)
            {
                session.UpdatedAt = title.UpdatedAt;
                changed = true;
            }
        }
        return changed;
    }

    private Dictionary<string, ThreadTitle> LoadThreadTitles()
    {
        var result = new Dictionary<string, ThreadTitle>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in CandidateSessionIndexFiles())
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idProp)) continue;
                    var id = idProp.GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var name = root.TryGetProperty("thread_name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    var updated = root.TryGetProperty("updated_at", out var updatedProp) ? updatedProp.GetString() ?? "" : "";
                    PutTitle(result, id, name, updated);
                }
            }
            catch
            {
                // Damaged index files are expected after repair experiments; ignore and continue.
            }
        }

        LoadSqliteThreadTitles(result);
        return result;
    }

    private static IEnumerable<string> CandidateSessionIndexFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".codex");
        var live = Path.Combine(root, "session_index.jsonl");
        if (File.Exists(live)) yield return live;
        var backups = Path.Combine(root, "repair-backups");
        if (!Directory.Exists(backups)) yield break;
        foreach (var file in Directory.EnumerateFiles(backups, "session_index.jsonl", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }

    private static void LoadSqliteThreadTitles(Dictionary<string, ThreadTitle> result)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");
        if (!File.Exists(path)) return;

        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "select id, title, updated_at_ms, updated_at from threads";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var updatedMs = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                var updatedSeconds = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                var updated = updatedMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(updatedMs).UtcDateTime.ToString("O")
                    : updatedSeconds > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(updatedSeconds).UtcDateTime.ToString("O")
                        : "";
                PutTitle(result, id, name, updated);
            }
        }
        catch
        {
            // SQLite may be locked or absent on older installs. JSONL titles remain enough for those cases.
        }
    }

    private static void PutTitle(Dictionary<string, ThreadTitle> result, string id, string name, string updatedAt)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return;
        if (!result.TryGetValue(id, out var existing) || string.CompareOrdinal(updatedAt, existing.UpdatedAt) >= 0)
        {
            result[id] = new ThreadTitle(name, updatedAt);
        }
    }

    private static IEnumerable<ArchiveSession> OrderedVisibleSessions(IEnumerable<ArchiveSession> sessions)
    {
        return sessions
            .Where(session => !session.Archived)
            .OrderByDescending(session => session.Pinned)
            .ThenByDescending(session => session.UpdatedAt);
    }

    private static string Slug(string value)
    {
        var slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "collection" : slug;
    }

    private sealed record ThreadTitle(string Name, string UpdatedAt);

    private static string FindProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "data", "app-store.json")) || Directory.Exists(Path.Combine(dir, "data", "fixtures")))
            {
                return dir;
            }
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent ?? "";
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
