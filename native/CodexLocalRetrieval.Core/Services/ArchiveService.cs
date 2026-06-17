using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Core.Services;

public sealed class ArchiveService
{
    private const int CurrentIndexVersion = 4; // bump on any parser change to force a full re-parse
    private const int MaxIndexedFiles = 4000;
    private const int MaxMessagesPerSession = 220;
    private const int MaxLinesPerSession = 18_000;
    private const int MaxLineChars = 512_000;

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
            var json = await File.ReadAllTextAsync(loadPath);
            Store = JsonSerializer.Deserialize<AppStoreData>(json, _jsonOptions) ?? new AppStoreData();
        }
        NormalizeSettings();
        RefreshSessions(OrderedVisibleSessions(Store.Sessions.Values));
    }

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
        // Serialize a synchronous snapshot, then gate the file write so overlapping saves
        // (a background sync finishing while the user pins/files a chat) can't clobber each other.
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var json = JsonSerializer.Serialize(Store, _jsonOptions);
        await _saveGate.WaitAsync();
        try { await File.WriteAllTextAsync(_storePath, json); }
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

    // Delete a project/collection — only the grouping; the chats themselves stay in the archive.
    public async Task RemoveCollectionAsync(string collectionId)
    {
        if (Store.Collections.Remove(collectionId)) await SaveAsync();
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
        switch ((cmd.op ?? "").Trim().ToLowerInvariant())
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
                var s = ResolveTargetSession(cmd);
                if (s is null) return new AgentCommandResult(false, "No matching session to favorite.");
                s.Pinned = true;
                await SaveAsync();
                RefreshSessions(Store.Sessions.Values);
                return new AgentCommandResult(true, $"Favorited \"{s.DisplayTitle}\".");
            }

            case "addtoproject":
            case "addtocollection":
            {
                if (string.IsNullOrWhiteSpace(cmd.project)) return new AgentCommandResult(false, "addToProject needs 'project'.");
                var s = ResolveTargetSession(cmd);
                if (s is null) return new AgentCommandResult(false, "No matching session to add to a project.");
                await AddToCollectionAsync(s, cmd.project!);
                RefreshSessions(Store.Sessions.Values);
                return new AgentCommandResult(true, $"Added \"{s.DisplayTitle}\" to project \"{cmd.project}\".");
            }

            case "rename":
            {
                var s = ResolveTargetSession(cmd);
                if (s is null) return new AgentCommandResult(false, "No matching session to rename.");
                if (!string.IsNullOrWhiteSpace(cmd.localName)) s.CustomTitle = CleanTitle(cmd.localName!);
                var canonical = await TryWriteCanonicalNameAsync(s, cmd.canonicalName);
                await SaveAsync();
                RefreshSessions(Store.Sessions.Values);
                return new AgentCommandResult(true, $"Renamed to \"{s.DisplayTitle}\".{(canonical is null ? "" : " " + canonical)}");
            }

            default:
                return new AgentCommandResult(false, $"Unknown op: '{cmd.op}'.");
        }
    }

    private void EnsureDefaultSources()
    {
        if (Store.Settings.Sources.Count == 0) Store.Settings.Sources.AddRange(DefaultSources());
    }

    // Resolve which session a command targets: an explicit id, or "self"/"latest" = the newest
    // session in the agent's workspace (cwd), optionally filtered by tool.
    public ArchiveSession? ResolveTargetSession(AgentCommand cmd)
    {
        var explicitId = cmd.id;
        if (string.IsNullOrWhiteSpace(explicitId) && !string.IsNullOrWhiteSpace(cmd.target)
            && cmd.target != "self" && cmd.target != "latest")
            explicitId = cmd.target;
        if (!string.IsNullOrWhiteSpace(explicitId) && Store.Sessions.TryGetValue(explicitId!, out var byId))
            return byId;

        if (string.IsNullOrWhiteSpace(cmd.cwd)) return null;
        var norm = NormalizePath(cmd.cwd!);
        return Store.Sessions.Values
            .Where(s => NormalizePath(s.Workspace) == norm)
            .Where(s => string.IsNullOrWhiteSpace(cmd.tool) || string.Equals(s.Tool, cmd.tool, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault();
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
            EnrichTitlesFromLocalState();
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
        if (string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase))
        {
            var claude = string.IsNullOrWhiteSpace(exeOverride) ? ResolveClaudeExe() : exeOverride!;
            var claudeArgs = $"--resume {id}";
            return new ResumeLaunch(claude, claudeArgs, cwd, $"\"{claude}\" {claudeArgs}");
        }
        var exe = string.IsNullOrWhiteSpace(exeOverride) ? ResolveCodexExe() : exeOverride!;
        var args = $"resume --include-non-interactive {id}";
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
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            JsonValueKind.Object or JsonValueKind.Array => v.ToString(),
            _ => ""
        };
    }

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
        var id = Path.GetFileNameWithoutExtension(filePath);
        var cwd = "";
        var created = "";
        var updated = "";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var info = new FileInfo(filePath);
        updated = info.LastWriteTimeUtc.ToString("O");

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineCount = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            lineCount++;
            if (lineCount > MaxLinesPerSession) break;
            if (messages.Count >= MaxMessagesPerSession) break;
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
                    if (payload.TryGetProperty("id", out var idProp)) id = idProp.GetString() ?? id;
                    if (payload.TryGetProperty("cwd", out var cwdProp)) cwd = cwdProp.GetString() ?? cwd;

                    if (payload.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "message")
                    {
                        var role = payload.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "message" : "message";
                        if (!IsIndexedRole(role)) continue;
                        var text = ExtractContent(payload);
                        AddMessage(messages, codeBlocks, role, text, timestamp, seen);
                    }
                    else if (payload.TryGetProperty("type", out var eventTypeProp))
                    {
                        var eventType = eventTypeProp.GetString();
                        if (eventType is "user_message" or "agent_message")
                        {
                            var role = eventType == "agent_message" ? "assistant" : "user";
                            var text = payload.TryGetProperty("message", out var messageProp) ? messageProp.GetString() ?? "" : "";
                            AddMessage(messages, codeBlocks, role, text, timestamp, seen);
                        }
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(created)) created = info.CreationTimeUtc.ToString("O");
        if (string.IsNullOrWhiteSpace(updated)) updated = info.LastWriteTimeUtc.ToString("O");

        var title = CleanFallbackTitle(messages.FirstOrDefault(m => m.Role == "user" && IsTitleCandidate(m.Text))?.Text ?? Path.GetFileNameWithoutExtension(filePath));
        return new ArchiveSession
        {
            Id = id,
            Title = title,
            SourcePath = filePath,
            CreatedAt = created,
            UpdatedAt = updated,
            Workspace = string.IsNullOrWhiteSpace(cwd) ? "Unknown workspace" : cwd,
            WorkspaceName = string.IsNullOrWhiteSpace(cwd) ? "Unknown" : Path.GetFileName(cwd.TrimEnd('\\', '/')),
            Model = "codex",
            Tool = "codex",
            Messages = messages,
            CodeBlocks = codeBlocks,
            Text = string.Join("\n\n", messages.Select(m => m.Text)),
            Tags = new ObservableCollection<string>(codeBlocks.Count > 0 ? new[] { "archive", "code" } : new[] { "archive" })
        };
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
        var info = new FileInfo(filePath);
        var updated = info.LastWriteTimeUtc.ToString("O");

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineCount = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            lineCount++;
            if (lineCount > MaxLinesPerSession) break;
            if (messages.Count >= MaxMessagesPerSession) break;
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
                    var text = ExtractClaudeContent(message);
                    AddMessage(messages, codeBlocks, role, text, timestamp, seen);
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

        if (string.IsNullOrWhiteSpace(created)) created = info.CreationTimeUtc.ToString("O");
        var title = !string.IsNullOrWhiteSpace(summary)
            ? CleanTitle(summary)
            : CleanTitle(FirstMeaningfulUserText(messages) ?? Path.GetFileNameWithoutExtension(filePath));
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
            Text = string.Join("\n\n", messages.Select(m => m.Text)),
            Tags = new ObservableCollection<string>(codeBlocks.Count > 0 ? new[] { "archive", "code" } : new[] { "archive" })
        };
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

        foreach (var message in session.Messages)
        {
            var score = FuzzyScore(message.Text, terms);
            if (score <= 0) continue;
            yield return new ArchiveSearchHit
            {
                Session = session,
                Message = message,
                SourceLabel = message.RoleLabel,
                Snippet = MakeSnippet(message.Text, terms),
                MatchedTerms = MatchedTerms(message.Text, terms),
                Score = score
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

    private string ResumePrompt(ArchiveSession session)
    {
        return $"Continue this archived work from local context.\n\nChat title: {session.DisplayTitle}\nSource path: {session.SourcePath}\nWorkspace: {session.Workspace}\n\nImportant context:\n" +
               string.Join("\n", session.Messages.TakeLast(6).Select(m => $"- {m.Role}: {Regex.Replace(m.Text, "\\s+", " ")[..Math.Min(260, Regex.Replace(m.Text, "\\s+", " ").Length)]}"));
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

    private bool EnrichTitlesFromLocalState() => ApplyThreadTitles(LoadThreadTitles());

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
