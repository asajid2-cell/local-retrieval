using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Memory;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Core.Services;

public sealed class ArchiveService
{
    private const int CurrentIndexVersion = 17; // bump on any parser change to force a full re-parse
    private const int MaxIndexedFiles = 4000;
    // The reader keeps the MOST RECENT messages (not the oldest), so a long chat opens on its latest
    // turns. The first prompt is still captured for the title before this window is applied.
    private const int RecentMessageWindow = 600;
    private const int MaxLinesPerSession = 18_000;
    private const int MaxLineChars = 512_000;
    private const int ClaudeTailBytes = 32 * 1024 * 1024;
    private const int CodexTailBytes = 16 * 1024 * 1024;
    private const int SearchTextCap = 6_000; // capped, in-memory searchable text per session (full content lazy-loads)

    private static string CapText(string s) => string.IsNullOrEmpty(s) || s.Length <= SearchTextCap ? s : s[^SearchTextCap..];

    // The last / first USER message in a full transcript, collapsed to a single capped line — used as the row
    // title for the "last/first user message" sorts (computed at parse time, persisted on the session).
    private static string OneLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var line = s.Replace("\r", " ").Replace("\n", " ").Trim();
        while (line.Contains("  ")) line = line.Replace("  ", " ");
        return line.Length <= 200 ? line : line[..200];
    }
    private static string LastUserText(IEnumerable<ArchiveMessage> messages) =>
        OneLine(messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Text);
    private static string FirstUserText(IEnumerable<ArchiveMessage> messages) =>
        OneLine(messages.FirstOrDefault(IsRealUserMessage)?.Text);
    // A REAL user prompt (your turn) — not a tool result (Claude files those as role=user) and not empty.
    private static bool IsRealUserMessage(ArchiveMessage m) =>
        string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
        && m.EffectiveKind != "tool"
        && !string.IsNullOrWhiteSpace(m.Text) && m.Text.Trim().Length > 1;
    private static int UserMsgCount(IEnumerable<ArchiveMessage> messages) => messages.Count(IsRealUserMessage);

    // Read a transcript WITHOUT ever blocking the agent that owns it. A live claude/codex holds its
    // transcript open for append; opening it with the default FileShare.Read DENIES that append — and
    // (verified empirically) the agent does NOT error, it SILENTLY DROPS the turn, losing it forever.
    // Every read of a file an agent might be writing MUST use ReadWrite|Delete sharing. Use these.
    public static string SafeReadAllText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    public static IEnumerable<string> SafeReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = sr.ReadLine()) is not null) yield return line;
    }

    // Count REAL user prompts across the ENTIRE transcript (NOT bounded by the 18000-line parse cap), so a
    // long autonomous run's user-message count is accurate for the "min user messages" filter. The windowed
    // parse only saw prompts in its window and badly undercounted (a 52-prompt chat read as 1). Cheap: a
    // pre-filtered line scan that JSON-parses only the candidate lines.
    private static int CountUserPrompts(string filePath, string tool)
    {
        var isClaude = string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase);
        var count = 0;
        try
        {
            // Shared read (ReadWrite|Delete) so a LIVE rollout — one the running agent still has open for
            // append — is counted instead of throwing a sharing violation (which silently zeroed the count).
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length < 12 || line.Length > MaxLineChars) continue;
                try
                {
                    if (isClaude)
                    {
                        // Claude files a real prompt as type "user" with text content; tool_results are also
                        // role=user but aren't prompts, so require a text block and reject tool_result lines.
                        if (!line.Contains("\"user\"", StringComparison.Ordinal)) continue;
                        if (line.Contains("\"tool_result\"", StringComparison.Ordinal)) continue;
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var tp) || tp.GetString() != "user") continue;
                        if (!root.TryGetProperty("message", out var msg)) continue;
                        if (msg.TryGetProperty("role", out var r) && r.GetString() != "user") continue;
                        if (ClaudeContentIsRealText(msg)) count++;
                    }
                    else
                    {
                        // Codex records the typed prompt as an event_msg with payload.type "user_message".
                        if (!line.Contains("\"user_message\"", StringComparison.Ordinal)) continue;
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("payload", out var p)
                            && p.TryGetProperty("type", out var t) && t.GetString() == "user_message") count++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return count;
    }
    private static bool ClaudeContentIsRealText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var c)) return false;
        if (c.ValueKind == JsonValueKind.String) return (c.GetString()?.Trim().Length ?? 0) > 1;
        if (c.ValueKind == JsonValueKind.Array)
            foreach (var item in c.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var it) && it.GetString() == "text") return true;
        return false;
    }

    // Recompute UserMessageCount from the source file for chats the disk scan couldn't reach this pass —
    // recovered chats in backup folders OUTSIDE the scan roots, or a file that was momentarily locked. Only
    // the zero-counts with real content, so it stays cheap; off the UI thread.
    private async Task BackfillUserCountsAsync()
    {
        var candidates = Store.Sessions.Values
            .Where(s => s.UserMessageCount == 0 && s.MessageCount >= 5 && !string.IsNullOrEmpty(s.SourcePath))
            .ToList();
        if (candidates.Count == 0) return;
        await Task.Run(() =>
        {
            foreach (var s in candidates)
            {
                try { if (File.Exists(s.SourcePath)) s.UserMessageCount = CountUserPrompts(s.SourcePath, s.Tool); }
                catch { }
            }
        });
    }

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
            Store = await LoadStoreWithRecoveryAsync(loadPath);
        }
        NormalizeSettings();
        EnsureDecks();
        RefreshSessions(OrderedVisibleSessions(Store.Sessions.Values));
    }

    private async Task<AppStoreData> LoadStoreWithRecoveryAsync(string loadPath)
    {
        return await Task.Run(async () =>
        {
            try
            {
                return ReadStore(loadPath);
            }
            catch (Exception primaryError) when (string.Equals(loadPath, _storePath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var backup in StoreBackupFiles())
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(backup);
                        var recovered = ReadStore(bytes, backup);
                        var corruptBackup = Path.Combine(
                            StoreBackupsDir,
                            "corrupt-app-store-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-"
                            + Guid.NewGuid().ToString("N") + ".json");
                        await DurableFileStore.WriteAtomicAsync(_storePath, bytes, corruptBackup);
                        return recovered;
                    }
                    catch
                    {
                        // Try the next older verified generation.
                    }
                }

                throw new InvalidDataException(
                    "The app store is corrupt and no valid durable backup could be recovered.",
                    primaryError);
            }
        });
    }

    private AppStoreData ReadStore(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var data = JsonSerializer.Deserialize<AppStoreData>(stream, _jsonOptions)
            ?? throw new InvalidDataException("App store deserialized to null: " + path);
        NormalizeLoadedStore(data);
        return data;
    }

    private AppStoreData ReadStore(byte[] bytes, string source)
    {
        var data = JsonSerializer.Deserialize<AppStoreData>(bytes, _jsonOptions)
            ?? throw new InvalidDataException("App store deserialized to null: " + source);
        NormalizeLoadedStore(data);
        return data;
    }

    private static void NormalizeLoadedStore(AppStoreData data)
    {
        foreach (var session in data.Sessions.Values)
            if (session.Text.Length > SearchTextCap) session.Text = session.Text[..SearchTextCap];
    }

    private IEnumerable<string> StoreBackupFiles() =>
        Directory.Exists(StoreBackupsDir)
            ? Directory.EnumerateFiles(StoreBackupsDir, "app-store-*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
            : Array.Empty<string>();

    // Read ONLY the top-level Settings object straight from the store file, WITHOUT materializing the
    // (tens-of-MB) session index — so the always-on light headless server can learn its multiplex SSH
    // target/port for the remote bridge and still stay ~40MB. Returns defaults if the store is absent.
    public ArchiveSettings ReadSettingsOnly()
    {
        var loadPath = File.Exists(_storePath) ? _storePath : _bundledStorePath;
        if (!File.Exists(loadPath)) return new ArchiveSettings();
        try
        {
            return ReadSettingsOnlyFrom(loadPath);
        }
        catch (Exception primaryError) when (string.Equals(loadPath, _storePath, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var backup in StoreBackupFiles())
            {
                try { return ReadSettingsOnlyFrom(backup); }
                catch { }
            }
            throw new InvalidDataException(
                "The app store settings are corrupt and no valid durable backup could be read.",
                primaryError);
        }
    }

    private ArchiveSettings ReadSettingsOnlyFrom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1
                || !string.Equals(reader.GetString(), "Settings", StringComparison.OrdinalIgnoreCase))
                continue;
            reader.Read();
            return JsonSerializer.Deserialize<ArchiveSettings>(ref reader, _jsonOptions)
                ?? throw new InvalidDataException("Settings deserialized to null: " + path);
        }
        return new ArchiveSettings();
    }

    // Release the in-memory store (all sessions + their loaded content + capped search text) so an idle
    // background server hands its RAM back to the OS. LoadAsync re-reads everything from app-store.json on
    // the next access, so this is LOSSLESS — purely a memory/first-hit-latency trade. The caller forces a
    // compacting GC afterwards to actually return the pages.
    public void Unload()
    {
        Store = new AppStoreData();
        RefreshSessions(Array.Empty<ArchiveSession>());
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
                // Only the LAST user message is safe to refresh from the recent window; FirstUserMessage and
                // UserMessageCount need the FULL transcript (set at index time), so don't clobber them here
                // with windowed values — the recent window would undercount / show the wrong "first".
                session.LastUserMessage = LastUserText(parsed.Messages);
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
        if (changed) ReapplyList();
        return changed;
    }

    public async Task SaveAsync()
    {
        // The store can be tens of MB, and SaveAsync runs on the UI thread from many actions (pin, rename,
        // sync). Capture the snapshot only after this save owns the gate, so an older queued save cannot
        // overwrite newer metadata with a stale pre-gate snapshot. Serialization + write stay off-thread.
        // The final commit is
        // temp-write + parse validation + atomic replace, so a crash or partial write cannot truncate the
        // only copy of the app metadata.
        var storeDir = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(storeDir);
        await _saveGate.WaitAsync();
        try
        {
            var snapshot = new AppStoreData
            {
                Settings = Store.Settings,
                Sessions = new Dictionary<string, ArchiveSession>(Store.Sessions),
                Collections = new Dictionary<string, ArchiveCollection>(Store.Collections),
                Decks = new List<Deck>(Store.Decks),
                DeletedCollections = new List<DeletedCollection>(Store.DeletedCollections),
                FileStamps = new Dictionary<string, string>(Store.FileStamps),
                TagColors = new Dictionary<string, string>(Store.TagColors),
                TagLayers = new Dictionary<string, int>(Store.TagLayers),
                PendingNewChats = new List<PendingNewChat>(Store.PendingNewChats),
                MuxTabHistory = new Dictionary<string, MuxTabRecord>(Store.MuxTabHistory),
                MuxTabMeta = new Dictionary<string, MuxTabMeta>(Store.MuxTabMeta),
            };
            await Task.Run(async () =>
            {
                var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                if (JsonSerializer.Deserialize<AppStoreData>(json, _jsonOptions) is null)
                    throw new InvalidDataException("serialized app store did not round-trip");
                Directory.CreateDirectory(StoreBackupsDir);
                var backup = Path.Combine(
                    StoreBackupsDir,
                    "app-store-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-"
                    + Guid.NewGuid().ToString("N") + ".json");
                await DurableFileStore.WriteAtomicAsync(_storePath, Encoding.UTF8.GetBytes(json), backup);
                PruneOldFiles(StoreBackupsDir, "app-store-*.json", MaxAutoBackups);
            });
        }
        finally { _saveGate.Release(); }
    }

    // The UI sets this to re-run the ACTIVE chat filter (preserving the selection). Every mutation + sync
    // that used to blow the list back to ALL sessions now calls ReapplyList() instead, so an add-to-
    // collection / rename / tag / pin / archive / background sync no longer silently drops your filters.
    public Action? OnReapplyFilter;
    private void ReapplyList()
    {
        if (OnReapplyFilter is not null) OnReapplyFilter();
        else RefreshSessions(Store.Sessions.Values);
    }

    public void RefreshSessions(IEnumerable<ArchiveSession> sessions, bool preserveOrder = false)
    {
        Sessions.Clear();
        // preserveOrder: the caller already ordered the set deliberately (e.g. by creation date) —
        // re-sorting by pinned/recency here would silently undo that.
        var ordered = preserveOrder ? sessions.Where(s => !s.Archived) : OrderedVisibleSessions(sessions);
        foreach (var session in ordered.Take(600))
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

        // [phrase] syntax — a fast, PRECISE codename lookup: return ONLY chats explicitly stamped with that
        // special phrase (exact, case-insensitive), with none of the title/text false positives a plain
        // search would pull in. Lets you jump to a whole codenamed set (e.g. "[petunia]") in one keystroke.
        var q = query.Trim();
        if (q.Length >= 3 && q[0] == '[' && q[^1] == ']')
        {
            var phrase = q[1..^1].Trim();
            if (phrase.Length > 0)
                return Store.Sessions.Values
                    .Where(s => !s.Archived && s.SpecialPhrases.Any(p => string.Equals(p, phrase, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(s => s.Pinned)
                    .ThenByDescending(s => s.UpdatedAt, StringComparer.Ordinal)
                    .ToList();
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

    // FUZZY full-content search: scan the actual transcript FILES for how many of a pasted turn's distinctive
    // WORDS appear (order-independent), so you can paste a turn — even one from the middle of a long chat,
    // beyond the in-memory 6000-char cap — with a word or two off, in different casing, or stripped of its
    // markdown backticks/quotes by a terminal copy, and still find the chat. Whole-word matching means the
    // stored `nexport` (backticked) still matches your plain "nexport". Parallel + newest-first + bounded.
    public async Task<IReadOnlyList<ArchiveSession>> SearchDiskPhraseAsync(string phrase, int limit = 40)
    {
        var tokens = DistinctiveTokens(phrase);
        if (tokens.Count < 2) return Array.Empty<ArchiveSession>();
        // A short query needs almost all its (few) words; a long paste can lose a couple and still be sure.
        var need = tokens.Count <= 4 ? tokens.Count : (int)Math.Ceiling(tokens.Count * 0.6);
        var sessions = Store.Sessions.Values
            .Where(s => !s.Archived && !string.IsNullOrEmpty(s.SourcePath))
            .OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal)
            .Take(2000)   // newest 2000 chats — covers everything but pathological archives; keeps it fast
            .ToList();
        var scored = new System.Collections.Concurrent.ConcurrentBag<(ArchiveSession s, int hitCount)>();
        await Task.Run(() =>
        {
            Parallel.ForEach(sessions, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, s =>
            {
                try
                {
                    var path = Path.IsPathRooted(s.SourcePath) ? s.SourcePath : Path.Combine(_rootPath, s.SourcePath);
                    if (!File.Exists(path)) return;
                    if (new FileInfo(path).Length > 96L * 1024 * 1024) return;
                    var lower = SafeReadAllText(path).ToLowerInvariant();   // shared read: never block a live agent's append
                    var present = 0;
                    foreach (var t in tokens) if (lower.Contains(t, StringComparison.Ordinal)) present++;
                    if (present >= need) scored.Add((s, present));
                }
                catch { }
            });
        });
        return scored.OrderByDescending(x => x.hitCount).ThenByDescending(x => x.s.UpdatedAt, StringComparer.Ordinal)
            .Take(limit).Select(x => x.s).ToList();
    }

    // Very common words that carry no search signal — dropped so a multi-word query ranks on its
    // distinctive terms, not on "the"/"and" appearing in every transcript.
    private static readonly HashSet<string> SearchStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","you","are","this","that","with","was","but","not","can","how","why","what",
        "have","has","from","your","our","get","use","using","made","make","about","when","then","them",
        "into","out","one","two","all","any","some","its","it's","were","did","does","done","also","been",
    };

    // The words of a query worth searching: lowercase, 3+ letters/digits, stopwords removed, deduped, capped.
    private static List<string> ContentQueryTokens(string query) =>
        System.Text.RegularExpressions.Regex.Matches((query ?? "").ToLowerInvariant(), "[a-z0-9]{3,}")
            .Select(m => m.Value)
            .Where(t => !SearchStopwords.Contains(t))
            .Distinct()
            .Take(12)
            .ToList();

    // Count (capped) case-insensitive, non-overlapping occurrences of a needle in a haystack.
    private static int CountOccurrencesCI(string haystack, string needle, int cap = 50)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            n++; i += needle.Length;
            if (n >= cap) break;
        }
        return n;
    }

    // A readable snippet from a raw JSONL window around the first match — strips JSON escaping/braces.
    private static string BuildFileSnippet(string raw, int idx, int span = 150)
    {
        if (idx < 0 || string.IsNullOrEmpty(raw)) return "";
        var start = Math.Max(0, idx - span / 3);
        var end = Math.Min(raw.Length, idx + span);
        var frag = raw.Substring(start, end - start);
        frag = Regex.Replace(frag, @"\\[nrt]", " ");
        frag = Regex.Replace(frag, "[\\\\{}\\[\\]\"]", " ");
        frag = Regex.Replace(frag, "\\s+", " ").Trim();
        return (start > 0 ? "…" : "") + frag + (end < raw.Length ? "…" : "");
    }

    // CONTENT-FIRST deep search: scan the REAL transcript files for the query's words and rank by how many
    // times they occur (frequency) plus a strong title/customTitle boost — so remembering a word or two from
    // a chat ("promicro", "venpod magenta", "tranzit parity") surfaces THAT chat, not path-token noise. Exact
    // substring is primary; long tokens also match close (typo'd) words on smaller files. Parallel + bounded.
    public async Task<IReadOnlyList<ArchiveSearchHit>> DeepSearchContentAsync(string query, int limit = 300)
    {
        var tokens = ContentQueryTokens(query);
        if (tokens.Count == 0) return Array.Empty<ArchiveSearchHit>();
        var phrase = Regex.Replace((query ?? "").Trim().ToLowerInvariant(), "\\s+", " ");
        var need = tokens.Count <= 2 ? tokens.Count : (int)Math.Ceiling(tokens.Count * 0.6);

        var sessions = Store.Sessions.Values
            .Where(s => !s.Archived && !string.IsNullOrEmpty(s.SourcePath))
            .OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal)
            .Take(3000)
            .ToList();

        var scored = new System.Collections.Concurrent.ConcurrentBag<ArchiveSearchHit>();
        await Task.Run(() =>
        {
            Parallel.ForEach(sessions, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, s =>
            {
                try
                {
                    var path = Path.IsPathRooted(s.SourcePath) ? s.SourcePath : Path.Combine(_rootPath, s.SourcePath);
                    if (!File.Exists(path)) return;
                    var len = new FileInfo(path).Length;
                    if (len > 48L * 1024 * 1024) return;   // bound peak memory (raw text is UTF-16 in RAM, parallel)
                    var raw = SafeReadAllText(path);       // shared read: never block a live agent's append
                    var allowFuzzy = len < 3L * 1024 * 1024;
                    string? low = allowFuzzy ? raw.ToLowerInvariant() : null;

                    var titleLower = (s.DisplayTitle ?? "").ToLowerInvariant();
                    var present = 0;
                    var contentScore = 0;
                    var titleHits = 0;
                    var firstIdx = -1;
                    var matched = new List<string>();
                    foreach (var t in tokens)
                    {
                        var inTitle = titleLower.Contains(t);
                        if (inTitle) titleHits++;
                        var c = CountOccurrencesCI(raw, t);
                        if (c > 0)
                        {
                            present++; matched.Add(t);
                            contentScore += Math.Min(c, 25);
                            var idx = raw.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0 && (firstIdx < 0 || idx < firstIdx)) firstIdx = idx;
                        }
                        else if (t.Length >= 5 && low is not null && HasCloseToken(t, low))
                        {
                            present++; matched.Add(t + "~");
                            contentScore += 4;
                        }
                        else if (inTitle) { matched.Add(t); }
                    }
                    if (titleHits == 0 && low is not null)
                        titleHits = tokens.Count(t => t.Length >= 5 && HasCloseToken(t, titleLower));

                    if (present < need && titleHits == 0) return;   // not enough of the query is in this chat

                    var score = contentScore + titleHits * 60;
                    if (present == tokens.Count && tokens.Count > 1) score += 40;             // every word present
                    if (phrase.Length > tokens[0].Length && raw.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0) score += 80;  // exact phrase

                    scored.Add(new ArchiveSearchHit
                    {
                        Session = s,
                        SourceLabel = present == 0 ? "title" : (titleHits > 0 ? "title + content" : "chat content"),
                        Snippet = firstIdx >= 0 ? BuildFileSnippet(raw, firstIdx) : (s.DisplayTitle ?? ""),
                        MatchedTerms = string.Join(", ", matched.Distinct()),
                        Score = score
                    });
                }
                catch { }
            });
        });
        return scored.OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Session.Pinned)
            .ThenByDescending(h => h.Session.UpdatedAt, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    // The distinctive WORDS of a phrase (lowercase, 4+ letters/digits, deduped, capped) — the rare/meaningful
    // ones that identify a specific turn, skipping short filler ("the", "is"). Matched whole so surrounding
    // markdown/punctuation in the transcript never matters.
    private static List<string> DistinctiveTokens(string phrase) =>
        System.Text.RegularExpressions.Regex.Matches((phrase ?? "").ToLowerInvariant(), "[a-z0-9]{4,}")
            .Select(m => m.Value).Distinct().Take(16).ToList();

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
        ReapplyList();
    }

    // Set the app-only display override (CustomTitle) for a chat by session id — the web tab-rename's
    // "App name" field. Only meaningful for an indexed chat (the override lives in THIS app's store); an
    // empty title clears the override so the native name shows again. Distinct from RenameNativeByIdAsync.
    public async Task<bool> RenameAppTitleByIdAsync(string sessionId, string title)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var existing = Store.Sessions.Values.FirstOrDefault(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase) ||
            s.Aliases.Any(a => string.Equals(a, sessionId, StringComparison.OrdinalIgnoreCase)));
        if (existing is null) return false;
        await RenameSessionAsync(existing, title ?? "");
        return true;
    }

    // Rename the chat's NATIVE name (the tool's own title) and persist + refresh. Returns the write
    // status. Distinct from RenameSessionAsync (which sets the app-only CustomTitle).
    public async Task<string?> RenameNativeAsync(ArchiveSession session, string title)
    {
        var status = await TryWriteCanonicalNameAsync(session, title);
        await SaveAsync();
        ReapplyList();
        return status;
    }

    // Native-rename a chat by its session id (used by the remote/web rename of a running session, which
    // may not be in the archive). Prefers an indexed session (so the app's state updates too); otherwise
    // writes straight to the tool's store by id (codex DB) / transcript (claude). Returns a status.
    public async Task<string?> RenameNativeByIdAsync(string tool, string sessionId, string title)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(title))
            return "rename needs a session id and a title.";
        var existing = Store.Sessions.Values.FirstOrDefault(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase) ||
            s.Aliases.Any(a => string.Equals(a, sessionId, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null) return await RenameNativeAsync(existing, title);
        var transient = new ArchiveSession { Id = sessionId, Tool = tool, SourcePath = ResolveClaudeTranscriptPath(tool, sessionId) ?? "" };
        return await TryWriteCanonicalNameAsync(transient, title);
    }

    private static string? ResolveClaudeTranscriptPath(string tool, string id)
    {
        if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase)) return null; // codex renames by DB id
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (Directory.Exists(root))
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var f = Path.Combine(dir, id + ".jsonl");
                    if (File.Exists(f)) return f;
                }
        }
        catch { }
        return null;
    }

    // Remember that the next new chat started in `cwd` with `tool` should be filed into `collectionId`.
    // Reconciled on the next index pass (ReconcilePendingNewChats). No-op if the collection is unknown.
    public async Task QueuePendingNewChatAsync(string tool, string cwd, string collectionId)
    {
        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(collectionId) || !Store.Collections.ContainsKey(collectionId)) return;
        // Replace any prior pending for the same cwd+collection (a re-launch supersedes).
        Store.PendingNewChats.RemoveAll(p =>
            NormalizePath(p.Cwd) == NormalizePath(cwd) && string.Equals(p.CollectionId, collectionId, StringComparison.Ordinal));
        Store.PendingNewChats.Add(new PendingNewChat
        {
            Cwd = cwd,   // keep the ORIGINAL cwd so we can find the Claude project folder (case-encoded)
            Tool = tool,
            CollectionId = collectionId,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            KnownIds = ClaudeFolderTranscripts(cwd).Select(t => t.id).ToList()   // snapshot ids already there
        });
        await SaveAsync();
    }

    // (id, creation-time) of every Claude transcript currently in the project folder for `cwd`. The folder
    // name is the cwd with [\/:.\s] -> '-' (Claude may also lowercase the drive letter), so we match the
    // directory case-insensitively. Returns empty if the folder doesn't exist yet (no chats there).
    private static List<(string id, DateTime created)> ClaudeFolderTranscripts(string cwd)
    {
        var list = new List<(string, DateTime)>();
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) return list;
            var want = EncodeClaudeProjectFolder(cwd);
            var folder = Directory.EnumerateDirectories(root)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), want, StringComparison.OrdinalIgnoreCase));
            if (folder is not null)
                foreach (var f in Directory.EnumerateFiles(folder, "*.jsonl"))
                {
                    DateTime ct; try { ct = File.GetCreationTimeUtc(f); } catch { ct = DateTime.UtcNow; }
                    list.Add((Path.GetFileNameWithoutExtension(f), ct));
                }
        }
        catch { }
        return list;
    }

    // File freshly-started "Start chat" sessions into their target collection. Runs cheap (one folder
    // listing per pending) and on the UI thread (the caller mutates the store). Returns true if anything
    // was filed/dropped. Claude: the new chat = the transcript that appeared in the cwd folder since launch
    // (KnownIds snapshot). Codex: the most-recent codex session in that cwd created after launch.
    public async Task<bool> ReconcilePendingNewChatsAsync()
    {
        var changed = ReconcilePendingNewChats();
        if (changed) { await SaveAsync(); ReapplyList(); }
        return changed;
    }

    // File freshly-indexed sessions into the collection a "Start chat" targeted, matching by tool +
    // normalized cwd + creation time. Drops filed/expired/orphaned entries. Pure in-memory (the caller
    // saves). Expires entries older than 2h so a chat that was never actually started doesn't linger.
    private bool ReconcilePendingNewChats()
    {
        if (Store.PendingNewChats.Count == 0) return false;
        var now = DateTimeOffset.UtcNow;
        var keep = new List<PendingNewChat>();
        var changed = false;
        foreach (var p in Store.PendingNewChats)
        {
            DateTimeOffset.TryParse(p.CreatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var created);
            if (created != default && now - created > TimeSpan.FromHours(24)) { changed = true; continue; }   // expired -> drop
            if (!Store.Collections.TryGetValue(p.CollectionId, out var collection)) { changed = true; continue; } // collection gone -> drop

            string? newId = null;
            if (string.Equals(p.Tool, "codex", StringComparison.OrdinalIgnoreCase))
            {
                // Codex has no per-cwd folder; match the earliest codex session in this cwd created after launch.
                var cwdN = NormalizePath(p.Cwd);
                newId = Store.Sessions.Values
                    .Where(s => string.Equals(s.Tool, "codex", StringComparison.OrdinalIgnoreCase) && NormalizePath(s.Workspace) == cwdN)
                    .Where(s => { DateTimeOffset.TryParse(s.CreatedAt, out var c); return c == default || c >= created.AddMinutes(-2); })
                    .OrderBy(s => s.CreatedAt, StringComparer.Ordinal)
                    .FirstOrDefault()?.Id;
            }
            else
            {
                // Claude: the new chat = the EARLIEST transcript in the cwd folder that wasn't there at launch.
                var known = new HashSet<string>(p.KnownIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                newId = ClaudeFolderTranscripts(p.Cwd)
                    .Where(t => !known.Contains(t.id))
                    .OrderBy(t => t.created)
                    .Select(t => t.id)
                    .FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(newId))
            {
                if (!collection.SessionIds.Contains(newId)) collection.SessionIds.Add(newId);
                changed = true;   // filed -> drop the pending entry
            }
            else keep.Add(p);     // no new chat yet -> keep waiting (up to 24h)
        }
        if (changed) Store.PendingNewChats = keep;
        return changed;
    }

    public async Task TogglePinAsync(ArchiveSession session)
    {
        session.Pinned = !session.Pinned;
        await SaveAsync();
        ReapplyList();
    }

    public async Task ArchiveSessionAsync(ArchiveSession session)
    {
        session.Archived = true;
        await SaveAsync();
        ReapplyList();
    }

    public async Task AddToCollectionAsync(ArchiveSession session, string collectionName, string? deckId = null)
    {
        var deck = ResolveDeckId(deckId ?? MainDeckId);
        var id = DeckCollectionId(collectionName, deck);
        if (!Store.Collections.TryGetValue(id, out var collection))
        {
            collection = new ArchiveCollection { Id = id, Name = collectionName, Color = Store.Settings.AccentHex, DeckId = deck };
            Store.Collections[id] = collection;
        }
        else if (string.IsNullOrWhiteSpace(collection.DeckId)) collection.DeckId = deck;
        if (!collection.SessionIds.Contains(session.Id)) collection.SessionIds.Add(session.Id);
        await SaveAsync();
    }

    // Create an empty collection from the Collections control panel (no chat needed yet). Returns
    // the collection, whether it was newly created or already existed under the same slug — so an
    // agent self-filing into the same name later lands in this exact collection.
    public async Task<ArchiveCollection> CreateCollectionAsync(string collectionName, string? deckId = null)
    {
        var deck = ResolveDeckId(deckId ?? ActiveDeckId);
        var id = DeckCollectionId(collectionName, deck);
        if (!Store.Collections.TryGetValue(id, out var collection))
        {
            collection = new ArchiveCollection { Id = id, Name = collectionName, Color = Store.Settings.AccentHex, DeckId = deck };
            Store.Collections[id] = collection;
            await SaveAsync();
            WriteAutoBackup();   // keep the latest app backup reflecting the new project
        }
        return collection;
    }

    // Add a chat to an EXISTING collection by its id (used by the "Add to collection" menus, which
    // already know the collection - avoids any name/deck ambiguity).
    public async Task AddToCollectionByIdAsync(ArchiveSession session, string collectionId)
    {
        if (!Store.Collections.TryGetValue(collectionId, out var col)) return;
        if (!col.SessionIds.Contains(session.Id)) col.SessionIds.Add(session.Id);
        await SaveAsync();
    }

    // Bulk add: file several chats into ONE existing collection with a single save (multi-select
    // "add to collection" — avoids re-serializing the whole store once per chat). Returns how many
    // were newly added (already-members are skipped, so re-adding is a no-op).
    public async Task<int> AddManyToCollectionByIdAsync(IEnumerable<ArchiveSession> sessions, string collectionId)
    {
        if (!Store.Collections.TryGetValue(collectionId, out var col)) return 0;
        var added = 0;
        foreach (var s in sessions)
        {
            if (s is null || string.IsNullOrEmpty(s.Id)) continue;
            if (!col.SessionIds.Contains(s.Id)) { col.SessionIds.Add(s.Id); added++; }
        }
        if (added > 0) await SaveAsync();
        return added;
    }

    // A live multiplex tab with no indexed chat (a shell you started `claude`/`codex` in yourself) has no
    // ArchiveSession, so "add all tabs to a collection" used to silently skip it. Materialize a lightweight
    // NAMED placeholder (tagged "mux-tab") keyed by the tab name so the whole open working set is captured.
    // Idempotent — re-adding the same tab reuses its placeholder. Not resumable (no transcript); it's a marker.
    public ArchiveSession EnsureMuxTabPlaceholder(string muxName, string tool)
    {
        muxName = (muxName ?? "").Trim();
        var id = "muxtab:" + muxName.ToLowerInvariant();
        if (Store.Sessions.TryGetValue(id, out var existing)) return existing;
        var now = DateTime.UtcNow.ToString("O");
        var s = new ArchiveSession
        {
            Id = id,
            Title = muxName,
            Tool = string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase) ? "codex" : "claude",
            SourcePath = "",
            CreatedAt = now,
            UpdatedAt = now,
            Workspace = "Live multiplex tab",
            WorkspaceName = "multiplex",
            MessageCount = 0,
            ContentLoaded = true,
        };
        s.Tags.Add("mux-tab");
        Store.Sessions[id] = s;
        return s;
    }

    // ---- Decks (top-level groupings of collections) --------------------------------------------
    public const string MainDeckId = "main";

    public void EnsureDecks()
    {
        if (!Store.Decks.Any(d => string.Equals(d.Id, MainDeckId, StringComparison.OrdinalIgnoreCase)))
            Store.Decks.Insert(0, new Deck { Id = MainDeckId, Name = "Main" });
        if (string.IsNullOrWhiteSpace(Store.Settings.ActiveDeckId)
            || !Store.Decks.Any(d => string.Equals(d.Id, Store.Settings.ActiveDeckId, StringComparison.OrdinalIgnoreCase)))
            Store.Settings.ActiveDeckId = MainDeckId;
    }

    public IReadOnlyList<Deck> Decks => Store.Decks;
    public string ActiveDeckId => string.IsNullOrWhiteSpace(Store.Settings.ActiveDeckId) ? MainDeckId : Store.Settings.ActiveDeckId;
    public static string CollectionDeck(ArchiveCollection c) => string.IsNullOrWhiteSpace(c.DeckId) ? MainDeckId : c.DeckId;

    public IReadOnlyList<ArchiveCollection> CollectionsInDeck(string deckId) =>
        Store.Collections.Values.Where(c => string.Equals(CollectionDeck(c), deckId, StringComparison.OrdinalIgnoreCase)).ToList();

    public int CollectionCountInDeck(string deckId) =>
        Store.Collections.Values.Count(c => string.Equals(CollectionDeck(c), deckId, StringComparison.OrdinalIgnoreCase));

    // Resolve a deck reference (id OR name, case-insensitive) to a deck id; null/unknown -> main.
    public string ResolveDeckId(string? deckRef)
    {
        if (string.IsNullOrWhiteSpace(deckRef)) return MainDeckId;
        var d = Store.Decks.FirstOrDefault(x =>
            string.Equals(x.Id, deckRef, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Name, deckRef, StringComparison.OrdinalIgnoreCase));
        return d?.Id ?? MainDeckId;
    }

    private string DeckCollectionId(string name, string deckId) =>
        string.Equals(deckId, MainDeckId, StringComparison.OrdinalIgnoreCase) ? Slug(name) : $"{Slug(deckId)}--{Slug(name)}";

    public async Task<Deck> CreateDeckAsync(string name)
    {
        var id = UniqueDeckId(name);
        var deck = new Deck { Id = id, Name = name.Trim(), CreatedAt = DateTime.UtcNow.ToString("O") };
        Store.Decks.Add(deck);
        await SaveAsync();
        return deck;
    }

    private string UniqueDeckId(string name)
    {
        var baseId = Slug(name);
        if (string.Equals(baseId, MainDeckId, StringComparison.OrdinalIgnoreCase)) baseId = "deck-" + baseId;
        var id = baseId;
        var n = 2;
        while (Store.Decks.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))) id = $"{baseId}-{n++}";
        return id;
    }

    public async Task RenameDeckAsync(string deckId, string name)
    {
        var d = Store.Decks.FirstOrDefault(x => string.Equals(x.Id, deckId, StringComparison.OrdinalIgnoreCase));
        if (d is null || string.IsNullOrWhiteSpace(name)) return;
        d.Name = name.Trim();
        await SaveAsync();
    }

    // Delete a deck (never Main) - its collections move to Main so nothing is orphaned.
    public async Task DeleteDeckAsync(string deckId)
    {
        if (string.Equals(deckId, MainDeckId, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var c in Store.Collections.Values.Where(c => string.Equals(CollectionDeck(c), deckId, StringComparison.OrdinalIgnoreCase)))
            c.DeckId = MainDeckId;
        Store.Decks.RemoveAll(d => string.Equals(d.Id, deckId, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(Store.Settings.ActiveDeckId, deckId, StringComparison.OrdinalIgnoreCase))
            Store.Settings.ActiveDeckId = MainDeckId;
        await SaveAsync();
    }

    public async Task SetActiveDeckAsync(string deckId)
    {
        if (!Store.Decks.Any(d => string.Equals(d.Id, deckId, StringComparison.OrdinalIgnoreCase))) return;
        Store.Settings.ActiveDeckId = deckId;
        await SaveAsync();
    }

    public async Task MoveCollectionToDeckAsync(string collectionId, string deckId)
    {
        if (!Store.Collections.TryGetValue(collectionId, out var c)) return;
        if (!Store.Decks.Any(d => string.Equals(d.Id, deckId, StringComparison.OrdinalIgnoreCase))) return;
        c.DeckId = ResolveDeckId(deckId);
        await SaveAsync();
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
    public string StoreBackupsDir => Path.Combine(Path.GetDirectoryName(_storePath)!, "store-backups");
    public string CollectionBackupsDir => Path.Combine(Path.GetDirectoryName(_storePath)!, "collection-backups");

    // Restorable copies of any transcript the app modifies (entrypoint recovery). A transcript is a
    // LIVE, append-only file owned by a running claude/codex process — so before we ever rewrite one we
    // (1) refuse if its agent is live, (2) keep a timestamped backup here so any rewrite is reversible.
    public string TranscriptBackupsDir => Path.Combine(Path.GetDirectoryName(_storePath)!, "transcript-backups");

    // True if a live claude/codex agent process is currently resuming this session (matched by the
    // resumed session id on its command line). If so, its transcript file MUST NOT be written by us —
    // deleting/replacing/appending underneath a live agent is what silently destroyed conversations.
    public static bool IsSessionProcessLive(ArchiveSession? session)
    {
        try
        {
            if (session is null) return false;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(session.Id)) ids.Add(session.Id);
            foreach (var a in session.Aliases) if (!string.IsNullOrEmpty(a)) ids.Add(a);
            if (ids.Count == 0) return false;
            return CodexLocalRetrieval.Core.Remote.RunningSessions.Scan()
                .Any(r => !string.IsNullOrEmpty(r.SessionId) && ids.Contains(r.SessionId));
        }
        catch { return false; }   // if we can't tell, the unchanged-file guard in the rewriter is the backstop
    }

    private static void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    // Per-collection Memory Bank / Project Brain vaults live here, a sibling of the backups dir.
    public string BrainsDir => Path.Combine(Path.GetDirectoryName(_storePath)!, "brains");

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
            PruneOldFiles(CollectionBackupsDir, "collections-*.json", MaxAutoBackups);
        }
        catch { /* backups are best-effort; never block the app */ }
    }

    private static void PruneOldFiles(string dir, string pattern, int keep)
    {
        try
        {
            foreach (var old in Directory.GetFiles(dir, pattern).OrderByDescending(f => f).Skip(Math.Max(1, keep)))
                TryDeleteFile(old);
        }
        catch { }
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
        ReapplyList();
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
                ReapplyList();
                return new AgentCommandResult(true, $"Favorited \"{s.DisplayTitle}\".", inputId, s.Id, Persisted: true);
            }

            case "addselftoproject":
            {
                if (string.IsNullOrWhiteSpace(cmd.project)) return new AgentCommandResult(false, "addToProject needs 'project'.");
                var s = await ResolveOrIndexTargetAsync(cmd);
                var inputId = ExplicitId(cmd);
                if (s is null) return new AgentCommandResult(false, ResolveFailureHelp("addToCollection"), inputId, Project: cmd.project);
                var deck = ResolveDeckId(cmd.deck);
                await AddToCollectionAsync(s, cmd.project!, deck);
                var named = ApplyOptionalName(s, cmd);  // optional app-local name in the same call
                if (named) await SaveAsync();
                ReapplyList();
                var colId = DeckCollectionId(cmd.project!, deck);
                var persisted = Store.Collections.TryGetValue(colId, out var collection)
                                && collection.SessionIds.Contains(s.Id);
                var deckName = Store.Decks.FirstOrDefault(d => string.Equals(d.Id, deck, StringComparison.OrdinalIgnoreCase))?.Name ?? "Main";
                return new AgentCommandResult(
                    true,
                    $"Added \"{s.DisplayTitle}\" to project \"{cmd.project}\" on deck \"{deckName}\".{(named ? " Named it in the app." : "")}",
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
                ReapplyList();
                return new AgentCommandResult(true, $"Renamed to \"{s.DisplayTitle}\".{(canonical is null ? "" : " " + canonical)}", inputId, s.Id, Persisted: true);
            }

            case "bump":
            {
                var s = await ResolveOrIndexTargetAsync(cmd);
                var inputId = ExplicitId(cmd);
                if (s is null) return new AgentCommandResult(false, ResolveFailureHelp("bump"), inputId);
                var (native, recovered) = await BumpSessionAsync(s);
                var picker = string.Equals(s.Tool, "codex", StringComparison.OrdinalIgnoreCase) ? "codex resume" : "claude --resume";
                var fileWord = string.Equals(s.Tool, "codex", StringComparison.OrdinalIgnoreCase) ? "rollout file" : "transcript";
                var msg = !native
                    ? $"Bumped \"{s.DisplayTitle}\" in the app, but its {fileWord} wasn't found, so {picker} is unchanged."
                    : recovered
                        ? $"Recovered \"{s.DisplayTitle}\" (it was hidden as an SDK session) and bumped it to the top of {picker}."
                        : $"Bumped \"{s.DisplayTitle}\" to the top of {picker}.";
                return new AgentCommandResult(true, msg, inputId, s.Id, Persisted: native);
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
                if (changed > 0) { await SaveAsync(); ReapplyList(); }
                var verb = op == "tag" ? "Tagged" : "Untagged";
                return new AgentCommandResult(true, $"{verb} \"{s.DisplayTitle}\" ({changed} change{(changed == 1 ? "" : "s")}).", inputId, s.Id, Persisted: changed > 0);
            }

            case "handoff":
            {
                // Return the collection's brain as a compact, sparse handoff bundle (current state +
                // canonical truths + open loops + working notes, each with a source link). No LLM, no
                // key — reads the canonical vault. Optional cmd.name carries the task to orient on.
                if (string.IsNullOrWhiteSpace(cmd.project))
                    return new AgentCommandResult(false, "handoff needs 'project' (the collection name).");
                var colId = ResolveCollectionIdByName(cmd.project!, cmd.deck);
                if (colId is null) return new AgentCommandResult(false, $"No collection named \"{cmd.project}\".", Project: cmd.project);
                var brain = new BrainService(this);
                if (!brain.PathsForBuilt(colId))
                    return new AgentCommandResult(false, $"No brain built yet for \"{cmd.project}\" — build it in the app first.", Project: cmd.project);
                var bundle = brain.GetAgentContext(colId, cmd.name ?? "");
                return new AgentCommandResult(true, bundle, Project: cmd.project, Persisted: true);
            }

            case "stash":
            {
                // /stashme: one call that (optionally) names the chat, files it into a collection/deck,
                // AND tags it with a searchable codename ("special phrase"). Any subset is allowed — a bare
                // phrase groups chats under a codename with no collection. App-only; never touches the transcript.
                var s = await ResolveOrIndexTargetAsync(cmd);
                var inputId = ExplicitId(cmd);
                if (s is null) return new AgentCommandResult(false, ResolveFailureHelp("stash"), inputId);

                var did = new List<string>();
                if (ApplyOptionalName(s, cmd)) did.Add($"named \"{s.DisplayTitle}\"");

                var phrase = (cmd.phrase ?? "").Trim();
                if (phrase.Length > 0 && AddSpecialPhrase(s, phrase)) did.Add($"codename \"{phrase}\"");

                // Deck: resolve, or CREATE it if a brand-new name was given (ResolveDeckId alone silently
                // falls back to Main for an unknown name — /stashme should make the deck the user asked for).
                string? deckId = null, deckDisplay = null;
                if (!string.IsNullOrWhiteSpace(cmd.deck))
                {
                    var d = Store.Decks.FirstOrDefault(x =>
                                string.Equals(x.Id, cmd.deck, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(x.Name, cmd.deck, StringComparison.OrdinalIgnoreCase))
                            ?? await CreateDeckAsync(cmd.deck!);
                    deckId = d.Id; deckDisplay = d.Name;
                }

                // Collection: explicit, else DEFAULT to the deck's name when only a deck was given
                // (a deck-only stash lands in a same-named collection on that deck). Both blank => no filing.
                var project = !string.IsNullOrWhiteSpace(cmd.project) ? cmd.project : cmd.collection;
                if (string.IsNullOrWhiteSpace(project) && deckDisplay != null) project = deckDisplay;

                string? proj = null; var persisted = false;
                if (!string.IsNullOrWhiteSpace(project))
                {
                    var deck = deckId ?? MainDeckId;
                    await AddToCollectionAsync(s, project!, deck);
                    proj = project;
                    var colId = DeckCollectionId(project!, deck);
                    persisted = Store.Collections.TryGetValue(colId, out var col) && col.SessionIds.Contains(s.Id);
                    var deckName = Store.Decks.FirstOrDefault(d => string.Equals(d.Id, deck, StringComparison.OrdinalIgnoreCase))?.Name ?? "Main";
                    did.Add($"filed into \"{project}\" on deck \"{deckName}\"");
                }

                if (did.Count == 0)
                    return new AgentCommandResult(false, "stash needs at least one of: name, collection, or phrase.", inputId, s.Id);
                await SaveAsync();
                ReapplyList();
                return new AgentCommandResult(true, $"Stashed \"{s.DisplayTitle}\": {string.Join("; ", did)}.", inputId, s.Id, proj, persisted);
            }

            default:
                return new AgentCommandResult(false, $"Unknown op: '{cmd.op}'.");
        }
    }

    // Add a searchable codename ("special phrase") to a chat (case-insensitive dedup). Returns true if new.
    private static bool AddSpecialPhrase(ArchiveSession s, string phrase)
    {
        phrase = (phrase ?? "").Trim();
        if (phrase.Length == 0) return false;
        if (s.SpecialPhrases.Any(p => string.Equals(p, phrase, StringComparison.OrdinalIgnoreCase))) return false;
        s.SpecialPhrases.Add(phrase);
        return true;
    }

    // Resolve a collection by the agent-supplied project name: first the deck-scoped id, then a
    // case-insensitive name match across decks. Null if no such collection exists.
    private string? ResolveCollectionIdByName(string name, string? deckRef)
    {
        var deck = ResolveDeckId(deckRef);
        var colId = DeckCollectionId(name, deck);
        if (Store.Collections.ContainsKey(colId)) return colId;
        var match = Store.Collections.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return match?.Id;
    }

    public static string NormalizeAgentOp(string? op)
    {
        var value = (op ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "addtoproject" or "addtocollection" or "addselftoproject" or "addselftocollection" => "addselftoproject",
            "stash" or "stashme" or "stashself" => "stash",
            "setname" or "name" or "label" or "setlabel" => "rename",
            "handoff" or "agentcontext" or "brainhandoff" or "context" => "handoff",
            "tomux" or "tomultiplex" or "to-mux" or "to_mux" or "muxhandoff" or "movetomux" => "tomux",
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
    public async Task<ArchiveSession?> ResolveOrIndexTargetAsync(AgentCommand cmd)
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

    public ArchiveSession? ResolveSessionByIdOrAlias(string id, string? tool = null)
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

    public static bool SessionMatchesIdOrAlias(ArchiveSession session, string id) => SessionHasIdOrAlias(session, id);

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

    private static bool SamePath(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    // L11: write the canonical name back to the agent's own store so the rename shows up in
    // Codex/Claude's native resume picker too — not just inside this app.
    // Write the chat's NATIVE/canonical title — the tool's OWN name (what `codex resume` / Claude's
    // recent list show), distinct from the app-only CustomTitle. Codex: update threads.title in its
    // state DB. Claude: append a `custom-title` record to the transcript (exactly how Claude Code itself
    // renames — its resume picker reads customTitle first). Also updates session.Title so the app +
    // remote views reflect the new native name immediately. Returns a human status string.
    public async Task<string?> TryWriteCanonicalNameAsync(ArchiveSession session, string? canonicalName)
    {
        if (string.IsNullOrWhiteSpace(canonicalName)) return null;
        var clean = CleanTitle(canonicalName!);
        string status;
        if (string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase))
            status = await Task.Run(() => WriteCodexThreadTitle(session.Id, clean));
        else
            status = await Task.Run(() => WriteClaudeCustomTitle(session, clean));
        // Reflect the new native name in the app's model (Title = native; DisplayTitle falls back to it).
        if (!string.IsNullOrWhiteSpace(clean)) session.Title = clean;
        return status;
    }

    // Append a `custom-title` line to a Claude transcript — the same record Claude Code writes on rename,
    // and the first title its resume picker reads (customTitle wins over aiTitle). Returns a status string.
    private static string WriteClaudeCustomTitle(ArchiveSession session, string title)
    {
        try
        {
            var path = session.SourcePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "Claude transcript not found; local name updated.";
            // Don't append into a transcript a live agent is writing — a concurrent append can interleave
            // and corrupt its record. Defer the native rename until the session is idle.
            if (IsSessionProcessLive(session)) return "Session is running — native rename deferred (app name updated); rename again once it's idle.";
            var id = string.IsNullOrEmpty(session.Id) ? Path.GetFileNameWithoutExtension(path) : session.Id;
            var rec = JsonSerializer.Serialize(new { type = "custom-title", sessionId = id, customTitle = title });
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.Write(rec + "\n");
            return "Native name written to Claude (shows in claude --resume).";
        }
        catch (Exception ex)
        {
            return "Claude native rename failed (" + ex.Message + "); local name updated.";
        }
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
            // NB: this sets threads.title (what THIS app + the remote views read). `codex resume` shows
            // its OWN auto-generated title, which Codex computes from the conversation and exposes no
            // settable field for — so we don't claim it changes there.
            return command.ExecuteNonQuery() > 0 ? "Renamed in Codex (app + remote views)." : "No matching Codex thread; local name updated.";
        }
        catch (Exception ex)
        {
            return "Codex canonical rename failed (" + ex.Message + "); local name updated.";
        }
    }

    // "Bump": float a chat to the top of Codex/Claude's OWN resume picker (and RECOVER a chat that
    // fell off the picker entirely) without sending a message. ★ VERIFIED by driving the real pickers
    // (pyte over a pty) AND by disassembling claude.exe's enumeration:
    //   - CODEX: `codex resume` orders the rollout .jsonl files under ~/.codex/sessions by FILE mtime.
    //     It does NOT read threads.updated_at_ms in state_5.sqlite (changing any DB column, even
    //     `archived`, does not move the picker). Touching the rollout file mtime floats it to "0s ago"
    //     at #1 — confirmed on chats of any age (a May thread jumped to the top). No window cutoff.
    //   - CLAUDE: `claude --resume` enumerates EVERY .jsonl in the project dir (no time window),
    //     sorts survivors by transcript FILE mtime DESC, and shows a page. Touching the transcript
    //     mtime floats a *surfaced* chat to #1. BUT a session is silently DROPPED from the picker when
    //     its `entrypoint` is one of {sdk-cli, sdk-ts, sdk-py} (claude.exe treats SDK/headless sessions
    //     as third-party). Chats created by the Agent SDK — i.e. how Claude Code runs in automation —
    //     carry "sdk-cli", so they NEVER appear no matter how recent. ★ FIX/RECOVER (proven): rewrite
    //     the transcript's `entrypoint` to an interactive value ("cli") and the fallen-away chat
    //     surfaces in `claude --resume` (then mtime sorts it to #1). A session is also dropped if its
    //     first line is `isSidechain:true`, sessionKind is daemon/daemon-worker, or it has no summary
    //     (title || lastPrompt || summary || firstPrompt) in the 64KB head/tail window — those are
    //     genuine sub-agent/daemon transcripts and are left alone.
    // Codex also gets its state-DB updated_at_ms refreshed for the separate app-server thread/list path.
    // Returns (Touched: transcript/rollout mtime was bumped, Recovered: a hidden Claude chat's
    // entrypoint was rewritten so it re-appears in the picker).
    public async Task<(bool Touched, bool Recovered)> BumpSessionAsync(ArchiveSession session)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = await Task.Run(() =>
        {
            var touched = false;
            var recovered = false;
            try
            {
                var path = string.IsNullOrEmpty(session.SourcePath) ? ""
                    : Path.IsPathRooted(session.SourcePath) ? session.SourcePath : Path.Combine(_rootPath, session.SourcePath);
                if (path.Length > 0 && File.Exists(path))
                {
                    // Whether a live agent owns this transcript RIGHT NOW (ground truth: Claude's own
                    // registry / codex's open rollout handle / the resume id on a command line).
                    var live = IsSessionProcessLive(session);

                    // For Claude, un-hide an SDK-entrypoint chat so the picker shows it — but NEVER rewrite a
                    // LIVE agent's transcript (that clobbered whole conversations); back up any idle rewrite.
                    if (string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase))
                        recovered = RecoverClaudeEntrypoint(path, sessionMayBeLive: live, backupDir: TranscriptBackupsDir);

                    // The app must NEVER touch a LIVE session's transcript — not even its mtime. Claude/Codex
                    // can treat an externally-modified transcript as a conflicting writer and STOP persisting
                    // the session (the session keeps running in memory but its turns never hit disk → lost on
                    // exit/fork). Only bump picker-recency when the agent is IDLE; a live one is already recent.
                    if (!live) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); touched = true; }
                    else touched = true;   // reported as "found", but we deliberately left the live file untouched
                }
            }
            catch { }
            if (string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");
                    var ids = new List<string> { session.Id };
                    ids.AddRange(session.Aliases);
                    BumpCodexThreadUpdatedAt(dbPath, ids, nowMs);   // app-server thread/list path (not the TUI picker)
                }
                catch { }
            }
            return (touched, recovered);
        });

        session.UpdatedAt = DateTime.UtcNow.ToString("O");   // top of our list too
        await SaveAsync();
        ReapplyList();
        return result;
    }

    // Entrypoints claude.exe treats as third-party/headless and HIDES from the interactive
    // `claude --resume` picker (function BTs in the binary checks against this set).
    private static readonly string[] HiddenClaudeEntrypoints = { "sdk-cli", "sdk-ts", "sdk-py" };

    // True if this Claude transcript would be hidden from `claude --resume` because of its entrypoint.
    // Mirrors claude.exe's BTs/jV: it reads the FIRST 64KB and takes the FIRST `entrypoint` value it
    // finds (the first transcript line is often a meta/summary line with no entrypoint, so a line-1
    // check misses it). The session is hidden iff that first entrypoint is one of the SDK values.
    public static bool IsHiddenClaudeEntrypoint(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            const int headBytes = 65536;   // Bw in the binary
            var buf = new char[headBytes];
            int read;
            // shared read (ReadWrite|Delete): this runs during indexing on EVERY claude transcript, incl.
            // live ones — the default deny-write share would block the agent's append and silently drop it.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                read = reader.Read(buf, 0, headBytes);
            var head = new string(buf, 0, read);
            var ep = FirstEntrypoint(head);
            return ep != null && Array.IndexOf(HiddenClaudeEntrypoints, ep) >= 0;
        }
        catch { return false; }
    }

    // Find the value of the first "entrypoint":"..." in the text (both compact and spaced spellings),
    // honoring backslash escapes — the same scan jV does in the binary.
    private static string? FirstEntrypoint(string s)
    {
        var best = -1; string? val = null;
        foreach (var pat in new[] { "\"entrypoint\":\"", "\"entrypoint\": \"" })
        {
            var a = s.IndexOf(pat, StringComparison.Ordinal);
            if (a < 0) continue;
            var l = a + pat.Length; var c = l;
            while (c < s.Length)
            {
                if (s[c] == '\\') { c += 2; continue; }
                if (s[c] == '"') break;
                c++;
            }
            if (best < 0 || a < best) { best = a; val = s.Substring(l, Math.Min(c, s.Length) - l); }
        }
        return val;
    }

    // Rewrite a Claude transcript's `entrypoint` from an SDK value ({sdk-cli,sdk-ts,sdk-py}) to the
    // interactive "cli" so the chat is no longer filtered out of `claude --resume`. Streams line by line
    // so multi-MB transcripts don't blow memory; only rewrites when a change is needed.
    //
    // A transcript is a LIVE, append-only file a running agent owns. Rewriting it destructively is what
    // silently ate whole conversations. This is now safe on THREE independent levels:
    //   1. sessionMayBeLive -> refuse outright (never touch a running agent's transcript).
    //   2. unchanged-file guard -> if the file grew/changed while we were rewriting (a writer appended),
    //      abort and keep the original — never clobber another process's writes.
    //   3. backup + atomic File.Replace -> keep a restorable copy, and NEVER File.Delete the original
    //      (deleting a file another process holds open orphans its future writes). File.Replace fails
    //      safely if the file is locked, leaving the original intact.
    // Returns true only if it actually rewrote the file.
    public static bool RecoverClaudeEntrypoint(string path, bool sessionMayBeLive = false, string? backupDir = null)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        if (sessionMayBeLive) return false;   // LEVEL 1: a live agent owns this file — hands off.

        // (needle -> replacement) for both compact and spaced JSON spellings.
        var swaps = new List<(string From, string To)>();
        foreach (var ep in HiddenClaudeEntrypoints)
        {
            swaps.Add(("\"entrypoint\":\"" + ep + "\"", "\"entrypoint\":\"cli\""));
            swaps.Add(("\"entrypoint\": \"" + ep + "\"", "\"entrypoint\": \"cli\""));
        }

        long len0; DateTime mt0;
        FileInfo info;
        try { info = new FileInfo(path); len0 = info.Length; mt0 = info.LastWriteTimeUtc; }
        catch { return false; }

        var tmp = path + ".bumptmp";
        try
        {
            var changed = false;
            using (var rfs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(rfs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var outLine = line;
                    foreach (var (from, to) in swaps)
                        if (outLine.IndexOf(from, StringComparison.Ordinal) >= 0)
                            outLine = outLine.Replace(from, to);
                    if (!string.Equals(outLine, line, StringComparison.Ordinal)) changed = true;
                    writer.Write(outLine);
                    writer.Write('\n');
                }
            }
            if (!changed) { TryDeleteFile(tmp); return false; }

            // LEVEL 2: someone wrote to the transcript while we were rewriting -> abort, don't clobber.
            info.Refresh();
            if (info.Length != len0 || info.LastWriteTimeUtc != mt0) { TryDeleteFile(tmp); return false; }

            // LEVEL 3a: keep a restorable backup of the original before we swap it out.
            if (!string.IsNullOrEmpty(backupDir))
            {
                try
                {
                    Directory.CreateDirectory(backupDir);
                    var dest = Path.Combine(backupDir,
                        $"{Path.GetFileNameWithoutExtension(path)}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.entrypoint.bak.jsonl");
                    File.Copy(path, dest, overwrite: true);
                }
                catch { /* backup is best-effort; the swap below is already non-destructive */ }
            }

            // LEVEL 3b: atomic replace. NO File.Delete on the original (never unlink a file another
            // process may hold open). If the destination is locked, File.Replace throws -> we abort safely.
            File.Replace(tmp, path, null);
            return true;
        }
        catch
        {
            TryDeleteFile(tmp);
            return false;
        }
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
        ReapplyList();
        return true;
    }

    public async Task<bool> RemoveChatTagAsync(ArchiveSession session, string tag)
    {
        if (!RemoveTagFrom(session.Tags, tag)) return false;
        await SaveAsync();
        ReapplyList();
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

    // Back-compat thin wrapper: text + an ANY/ALL include-tag filter.
    public IReadOnlyList<ArchiveSession> Filter(string query, IReadOnlyCollection<string>? tags, bool matchAll = false)
        => FilterChats(new ChatFilter { Query = query ?? "", IncludeTags = (tags ?? Array.Empty<string>()).ToList(), MatchAllIncludes = matchAll });

    private static bool SessionHasTag(ArchiveSession s, string tag) =>
        s.Tags.Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));

    // The compound filter: text search, then restrict to a collection (if set), then keep chats that
    // satisfy the include set (ANY or ALL) and carry NONE of the exclude set.
    // A "one-off" / spam chat: exactly one (or zero) user prompt AND a tiny transcript — the hundreds
    // of spawned judge/probe/render sessions that each fire a single message. Pinned, tagged, and
    // collection-member chats are NEVER treated as spam (the user deliberately kept them). The tiny-
    // transcript guard (MessageCount) protects any genuinely large chat whose user-count hasn't been
    // backfilled yet from being hidden by accident. Reveal these with the "Show hidden chats" toggle.
    public static bool IsLowSignalChat(ArchiveSession s)
        => !s.Pinned
           && !s.Archived
           && UserTags(s).Count == 0   // reserved auto-tags ("archive"/"code") are on EVERY chat — only a DELIBERATE user tag counts as "kept"
           && s.SpecialPhrases.Count == 0   // a codename ("special phrase") is a deliberate keep — never auto-hide a stashed chat
           && s.UserMessageCount <= 1
           && s.MessageCount <= 8;

    // How many chats are currently auto-hidden as one-offs (for the "Show hidden (N)" label).
    public int HiddenChatCount() => Store.Sessions.Values.Count(IsLowSignalChat);

    public IReadOnlyList<ArchiveSession> FilterChats(ChatFilter f)
    {
        IEnumerable<ArchiveSession> baseSet = string.IsNullOrWhiteSpace(f.Query)
            ? OrderedVisibleSessions(Store.Sessions.Values)
            : Search(f.Query);

        if (!string.IsNullOrEmpty(f.CollectionId) && Store.Collections.TryGetValue(f.CollectionId, out var col))
        {
            var ids = new HashSet<string>(col.SessionIds, StringComparer.OrdinalIgnoreCase);
            baseSet = baseSet.Where(s => ids.Contains(s.Id));
        }

        // Auto-hide one-off / spam chats unless the user asked to see them, or is browsing a specific
        // collection (those are deliberately-kept chats). Applies to the plain list AND text search.
        bool hideSpam = !f.ShowHidden && string.IsNullOrEmpty(f.CollectionId);

        var result = baseSet
            .Where(s => string.IsNullOrEmpty(f.Tool) || string.Equals(s.Tool, f.Tool, StringComparison.OrdinalIgnoreCase))
            .Where(s => !hideSpam || !IsLowSignalChat(s))
            .Where(s => f.MinUserMessages <= 0 || s.UserMessageCount >= f.MinUserMessages)
            .Where(s => f.IncludeTags.Count == 0
                        || (f.MatchAllIncludes
                            ? f.IncludeTags.All(t => SessionHasTag(s, t))
                            : f.IncludeTags.Any(t => SessionHasTag(s, t))))
            .Where(s => f.ExcludeTags.Count == 0 || !f.ExcludeTags.Any(t => SessionHasTag(s, t)));

        static DateTimeOffset CreatedOf(ArchiveSession s) =>
            DateTimeOffset.TryParse(s.CreatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var c) ? c : DateTimeOffset.MinValue;

        // DATE-RANGE filter (combines with any sort): restrict to chats CREATED within the window.
        var range = (f.DateRange ?? "").ToLowerInvariant();
        if (range.Length > 0)
        {
            var today = DateTimeOffset.Now.Date;
            result = range switch
            {
                "today" => result.Where(s => CreatedOf(s).ToLocalTime().Date == today),
                "week"  => result.Where(s => CreatedOf(s).ToLocalTime().Date >= today.AddDays(-7)),
                "month" => result.Where(s => CreatedOf(s).ToLocalTime().Date >= today.AddDays(-30)),
                _ => result,
            };
        }

        // SORT (combines with the range filter above). "" keeps the incoming order (recent-activity when no
        // query; relevance when searching). last-user/first-user order by recency but flip the row TITLE.
        var mode = (f.DateMode ?? "").ToLowerInvariant();
        result = mode switch
        {
            "created-oldest" => result.OrderBy(s => CreatedOf(s) == DateTimeOffset.MinValue ? DateTimeOffset.MaxValue : CreatedOf(s)),  // unknown dates sink either way
            "created-newest" => result.OrderByDescending(CreatedOf),
            "last-user" or "first-user" => result.OrderByDescending(s => s.Pinned).ThenByDescending(s => s.UpdatedAt, StringComparer.Ordinal),
            _ => result,   // "" recent-activity / search relevance — leave as-is
        };
        return result.ToList();
    }

    // ---- Collection (project) filtering + demote ordering --------------------------------------
    private static bool CollectionHasTag(ArchiveCollection c, string tag) =>
        c.Tags.Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));

    // Filter collections by their own tags (compound): keep those satisfying the include set (ANY or
    // ALL) and carrying NONE of the exclude set. "active + graphics, not web-dev" -> include both, exclude web-dev.
    public IReadOnlyList<ArchiveCollection> FilterCollections(IReadOnlyCollection<string> include, IReadOnlyCollection<string> exclude, bool matchAll = false)
    {
        return Store.Collections.Values
            .Where(c => include.Count == 0
                        || (matchAll ? include.All(t => CollectionHasTag(c, t)) : include.Any(t => CollectionHasTag(c, t))))
            .Where(c => exclude.Count == 0 || !exclude.Any(t => CollectionHasTag(c, t)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Layer rules: a tag can be assigned a layer (LOWER number = HIGHER in the list). Unassigned tags
    // use DefaultLayer, so layered tags can float above (e.g. active=1) or sink below (e.g. context=900).
    public const int DefaultLayer = 100;

    public int TagLayer(string tag) =>
        Store.TagLayers.TryGetValue(TagColorKey(tag), out var n) ? n : DefaultLayer;

    // A chat's layer = the strongest (lowest) EXPLICITLY-assigned layer among its tags; DefaultLayer
    // if none of its tags has a layer rule. (Unlayered tags don't pull a context chat back to the middle.)
    public int SessionLayer(ArchiveSession s)
    {
        int? best = null;
        foreach (var t in UserTags(s))
            if (Store.TagLayers.TryGetValue(TagColorKey(t), out var l))
                best = best is null ? l : Math.Min(best.Value, l);
        return best ?? DefaultLayer;
    }

    public async Task SetTagLayerAsync(string tag, int? layer)
    {
        var key = TagColorKey(tag);
        if (key.Length == 0) return;
        if (layer is null || layer == DefaultLayer) Store.TagLayers.Remove(key);
        else Store.TagLayers[key] = layer.Value;
        await SaveAsync();
        ReapplyList();
    }

    // Order a collection's chats: pinned first, then by layer (active up / context down), then by the
    // collection's MANUAL order (its SessionIds sequence), then recency. So manual drags persist and
    // layer rules group on top of them.
    public IReadOnlyList<ArchiveSession> OrderCollectionChats(ArchiveCollection col, IEnumerable<ArchiveSession> sessions)
    {
        var manual = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < col.SessionIds.Count; i++) manual.TryAdd(col.SessionIds[i], i);
        return sessions
            .OrderByDescending(s => s.Pinned)
            .ThenBy(s => SessionLayer(s))
            .ThenBy(s => manual.TryGetValue(s.Id, out var i) ? i : int.MaxValue)
            .ThenByDescending(s => s.UpdatedAt)
            .ToList();
    }

    // Move a chat within a collection's manual order by delta (negative = up). toEnd jumps to top/bottom.
    public async Task ReorderInCollectionAsync(string collectionId, string sessionId, int delta, bool toEnd = false)
    {
        if (!Store.Collections.TryGetValue(collectionId, out var col)) return;
        var i = col.SessionIds.FindIndex(x => string.Equals(x, sessionId, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        var target = toEnd ? (delta < 0 ? 0 : col.SessionIds.Count - 1) : Math.Clamp(i + delta, 0, col.SessionIds.Count - 1);
        if (target == i) return;
        col.SessionIds.RemoveAt(i);
        col.SessionIds.Insert(target, sessionId);
        await SaveAsync();
    }

    // ---- Tag colors ----------------------------------------------------------------------------
    // A curated palette that reads on pure-black AMOLED and coexists with the rose accent (Tailwind-400
    // family). Tags get a deterministic auto-color from this by name hash; users can override per tag.
    // Rose (#FB7185) is deliberately excluded - it's reserved for the accent / active-filter state.
    public static readonly IReadOnlyList<string> TagPalette = new[]
    {
        "#60A5FA", "#38BDF8", "#22D3EE", "#2DD4BF", "#34D399",
        "#A3E635", "#FBBF24", "#FB923C", "#F87171", "#A78BFA"
    };

    private static string TagColorKey(string tag) => NormalizeTag(tag).ToLowerInvariant();

    // Stable across runs (string.GetHashCode is randomized in .NET, so use FNV-1a).
    private static uint Fnv1a(string s)
    {
        uint hash = 2166136261;
        foreach (var ch in s) { hash ^= ch; hash *= 16777619; }
        return hash;
    }

    public static string AutoTagColor(string tag)
    {
        var key = NormalizeTag(tag).ToLowerInvariant();
        if (key.Length == 0) return TagPalette[0];
        return TagPalette[(int)(Fnv1a(key) % (uint)TagPalette.Count)];
    }

    // The effective color for a tag: a user override if set, else the deterministic auto-color.
    public string TagColor(string tag)
    {
        var key = TagColorKey(tag);
        return Store.TagColors.TryGetValue(key, out var hex) && !string.IsNullOrWhiteSpace(hex)
            ? hex
            : AutoTagColor(tag);
    }

    public bool HasCustomTagColor(string tag) => Store.TagColors.ContainsKey(TagColorKey(tag));

    // Set (or clear, when hex is null/blank) a tag's color override; clearing returns it to auto.
    public async Task SetTagColorAsync(string tag, string? hex)
    {
        var key = TagColorKey(tag);
        if (key.Length == 0) return;
        if (string.IsNullOrWhiteSpace(hex)) Store.TagColors.Remove(key);
        else Store.TagColors[key] = hex.Trim();
        await SaveAsync();
        ReapplyList();
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
            "command" => ResumeCommandText(session),   // the actual CLI resume command (codex resume <id> / claude --resume <id>)
            _ => ResumePrompt(session)
        };
    }

    // The actual CLI resume command for a chat — `codex resume <id>` / `claude --resume <id>` (with any
    // configured launch args). What you paste into a terminal to bring the exact chat back.
    public string ResumeCommandText(ArchiveSession session)
    {
        var cmd = BuildMultiplexCommand(session);
        if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
        var launch = BuildResumeLaunch(session);
        return string.IsNullOrWhiteSpace(launch.DisplayCommand)
            ? "No resume command for this chat (shell-only, or its session id couldn't be resolved)."
            : launch.DisplayCommand;
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
        // Auto-detect per file so a root holding Claude transcripts (or a mix) is routed correctly
        // instead of being force-parsed as Codex — otherwise a Claude .jsonl yields zero messages and
        // is mis-tagged tool=codex (which then breaks ParseFullAsync / the brain source layer).
        var (parsed, stamps) = await ParseSourceAsync(new SessionSource { Tool = "auto", Root = rootPath },
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

    // The shared, FULL-history result of parsing one transcript, BEFORE the recent-message window is
    // applied. The normal indexing path (ParseJsonlAsync/ParseClaudeSessionAsync) wraps this and trims
    // to the window; the brain wants the untrimmed list for stable anchoring. ONE source of truth for
    // message extraction — a drift-guard test asserts the windowed tail equals this list's tail.
    private sealed record ParsedTranscript(
        ObservableCollection<ArchiveMessage> Messages,
        ObservableCollection<CodeBlock> CodeBlocks,
        string Id, string Title, string Created, string Updated,
        string Cwd, HashSet<string> Aliases, int Total, string Tool,
        // The first user prompt, captured from the transcript HEAD before any tail swap — so a huge chat
        // whose reader window shows only recent turns still reports the correct opening prompt / count.
        string FirstUser = "");

    // Brain source layer: re-parse a session's raw transcript WITHOUT the 600-message window, yielding
    // the full ordered message list with a stable per-message Index (0-based over the whole transcript)
    // — the basis for source blocks and anchors. Reuses the exact same extraction core as indexing.
    public async Task<IReadOnlyList<FullMessage>> ParseFullAsync(ArchiveSession session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.SourcePath) || !File.Exists(session.SourcePath))
            return Array.Empty<FullMessage>();
        var tool = string.IsNullOrWhiteSpace(session.Tool) || session.Tool == "auto"
            ? DetectTool(session.SourcePath) : session.Tool;
        var core = tool == "claude"
            ? await ParseClaudeCoreAsync(session.SourcePath)
            : await ParseCodexCoreAsync(session.SourcePath);
        if (core is null) return Array.Empty<FullMessage>();
        var list = new List<FullMessage>(core.Messages.Count);
        for (var i = 0; i < core.Messages.Count; i++)
        {
            var m = core.Messages[i];
            list.Add(new FullMessage(i, m.Role, m.Text, m.Timestamp, m.EffectiveKind == "tool" ? m.ToolName : null));
        }
        return list;
    }

    // The FULL transcript as reader messages (no 600-message window). The reader uses this for the
    // "your messages" / "agent only" toggles, so a long agent run whose recent window has no user prompt
    // still shows every user message instead of "No messages of this kind" (the old window-only bug).
    public async Task<List<ArchiveMessage>> FullReaderMessagesAsync(ArchiveSession session)
    {
        var full = await ParseFullAsync(session);
        return full.Select(m => new ArchiveMessage
        {
            Role = m.Role,
            Text = m.Text,
            Timestamp = m.Timestamp,
            Kind = string.IsNullOrEmpty(m.ToolName) ? "" : "tool",
            ToolName = m.ToolName ?? "",
        }).ToList();
    }

    // Read the WHOLE transcript (UNCAPPED) but build ONLY the requested kind ("user" | "assistant"), so a
    // long chat's View:you / View:agent shows EVERY such message. ParseFull caps at 18000 lines, which hid
    // most of a huge chat's turns (a 67k-line chat showed 3 of 29 user prompts). "user" is sparse (cheap).
    public async Task<List<ArchiveMessage>> ExtractReaderMessagesAsync(ArchiveSession session, string kind)
    {
        var wantUser = string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase);
        var isClaude = string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase);
        var list = new List<ArchiveMessage>();
        var path = session.SourcePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;
        await Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length < 8 || line.Length > MaxLineChars) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                        string? text = null;
                        if (isClaude)
                        {
                            var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                            if (wantUser ? type != "user" : type != "assistant") continue;
                            if (!root.TryGetProperty("message", out var msg)) continue;
                            if (wantUser)
                            {
                                if (line.Contains("\"tool_result\"", StringComparison.Ordinal)) continue;   // tool results are role=user but not prompts
                                if (!ClaudeContentIsRealText(msg)) continue;
                            }
                            text = ClaudeMessageText(msg);
                        }
                        else
                        {
                            if (!root.TryGetProperty("payload", out var payload)) continue;
                            if ((root.TryGetProperty("type", out var rt) ? rt.GetString() : null) != "event_msg") continue;
                            var pt = payload.TryGetProperty("type", out var ptp) ? ptp.GetString() : null;
                            if (wantUser ? pt != "user_message" : pt != "agent_message") continue;
                            text = Field(payload, "message");
                        }
                        if (!string.IsNullOrWhiteSpace(text))
                            list.Add(new ArchiveMessage { Role = wantUser ? "user" : "assistant", Kind = wantUser ? "user" : "assistant", Text = text!, Timestamp = timestamp });
                    }
                    catch { }
                }
            }
            catch { }
        });
        return list;
    }

    private static string ClaudeMessageText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in c.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var it) && it.GetString() == "text"
                    && item.TryGetProperty("text", out var tx)) sb.AppendLine(tx.GetString());
            return sb.ToString().Trim();
        }
        return "";
    }

    // Brain "open source span": the messages of a transcript in the inclusive index range a card's
    // anchor points at (optionally with N messages of context on each side). Indices are over the FULL
    // history, matching the anchors SourceBlocker produced. Clamped, so a stale anchor never throws.
    public async Task<IReadOnlyList<FullMessage>> ParseFullRangeAsync(ArchiveSession session, int startIndex, int endIndex, int context = 0)
    {
        var full = await ParseFullAsync(session);
        if (full.Count == 0) return full;
        var lo = Math.Max(0, Math.Min(startIndex, endIndex) - Math.Max(0, context));
        var hi = Math.Min(full.Count - 1, Math.Max(startIndex, endIndex) + Math.Max(0, context));
        var slice = new List<FullMessage>(Math.Max(0, hi - lo + 1));
        for (var i = lo; i <= hi; i++) slice.Add(full[i]);
        return slice;
    }

    // Peek the first useful line: claude transcripts carry a sessionId; codex rollouts carry a payload.
    private static string DetectTool(string path)
    {
        try
        {
            foreach (var line in SafeReadLines(path))   // shared read: never block a live agent's append
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
        var sourceRekeys = FindSourcePathRekeys(scan.Disk);
        var idsRekeyedFrom = new HashSet<string>(sourceRekeys.Values.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in scan.Disk)
        {
            if (sourceRekeys.TryGetValue(incoming.Id, out var existingBySource))
            {
                PreserveAppFields(existingBySource, incoming);
                AddAlias(incoming.Aliases, existingBySource.Id);
                ReassignSessionReferences(existingBySource.Id, incoming.Id);
                if (Store.Sessions.TryGetValue(existingBySource.Id, out var current)
                    && SamePath(current.SourcePath, existingBySource.SourcePath))
                    Store.Sessions.Remove(existingBySource.Id);
            }
            else if (Store.Sessions.TryGetValue(incoming.Id, out var existing)
                     && (!idsRekeyedFrom.Contains(existing.Id) || SamePath(existing.SourcePath, incoming.SourcePath)))
            {
                PreserveAppFields(existing, incoming);
            }
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
        ReconcilePendingNewChats();   // file freshly-started chats into their target collection (folder-diff identity)
        Store.Settings.BundledHistoryAbsorbed = true;
        if (scan.FullRescan) await BackfillUserCountsAsync();   // recompute user counts the disk scan couldn't reach (backup-folder chats, previously-locked live files)
        Store.Settings.IndexVersion = CurrentIndexVersion;
        await SaveAsync();
        if (refreshList) ReapplyList();
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
            if (!string.Equals(alias, incoming.Id, StringComparison.OrdinalIgnoreCase)) AddAlias(incoming.Aliases, alias);
        }
        foreach (var tag in existing.Tags)
        {
            if (!incoming.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) incoming.Tags.Add(tag);
        }
        foreach (var phrase in existing.SpecialPhrases)
        {
            if (!incoming.SpecialPhrases.Contains(phrase, StringComparer.OrdinalIgnoreCase)) incoming.SpecialPhrases.Add(phrase);
        }
    }

    private Dictionary<string, ArchiveSession> FindSourcePathRekeys(IEnumerable<ArchiveSession> incoming)
    {
        var byPath = Store.Sessions.Values
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath))
            .GroupBy(s => NormalizePath(s.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ArchiveSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in incoming)
        {
            if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.SourcePath)) continue;
            if (!byPath.TryGetValue(NormalizePath(session.SourcePath), out var existing)) continue;
            if (string.Equals(existing.Id, session.Id, StringComparison.OrdinalIgnoreCase)) continue;
            result[session.Id] = existing;
        }
        return result;
    }

    private void ReassignSessionReferences(string oldId, string newId)
    {
        if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId)
            || string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)) return;
        void Replace(List<string> ids)
        {
            var hasNew = ids.Any(id => string.Equals(id, newId, StringComparison.OrdinalIgnoreCase));
            for (var i = ids.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(ids[i], oldId, StringComparison.OrdinalIgnoreCase)) continue;
                if (hasNew) ids.RemoveAt(i);
                else
                {
                    ids[i] = newId;
                    hasNew = true;
                }
            }
        }
        foreach (var collection in Store.Collections.Values) Replace(collection.SessionIds);
        foreach (var deleted in Store.DeletedCollections) Replace(deleted.Collection.SessionIds);
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

    public ResumeLaunch BuildResumeLaunch(ArchiveSession session, string? exeOverride = null, string? extraArgsOverride = null)
    {
        var id = ResumeSessionId(session);
        var isClaude = string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase);
        // Claude resume is directory-scoped (it only finds the session under the launch dir's encoded
        // project folder), so it needs the recovered launch dir — not the recorded workspace subdir.
        var cwd = isClaude ? ResolveClaudeResumeDirectory(session) : ResolveWorkingDirectory(session);
        if (!IsResumableId(id))
            return new ResumeLaunch("", "", cwd, "Refused: session id is not a safe token.");

        var exe = !string.IsNullOrWhiteSpace(exeOverride) ? exeOverride!
            : isClaude ? ResolveClaudeExe() : ResolveCodexExe();

        // The terminal opens in the chat's workspace, and cmd resolves a bare command against the
        // current directory before PATH — so a workspace that contains a planted codex.exe/claude.bat
        // could be run. Require a trusted ABSOLUTE existing exe (overrides are caller-trusted, e.g. tests).
        if (string.IsNullOrWhiteSpace(exeOverride) && (!Path.IsPathRooted(exe) || !File.Exists(exe)))
            return new ResumeLaunch("", "", cwd, $"Refused: the {(isClaude ? "claude" : "codex")} CLI was not found at a trusted path.");

        var baseArgs = isClaude ? $"--resume {id}" : $"resume --include-non-interactive {id}";
        // Optional user-configured launch args (e.g. "--profile http_sse" for Codex) go in the PREFIX
        // position — right after the exe, before the subcommand — so global flags apply (Codex requires
        // --profile before `resume`). Empty leaves the default command exactly as it was. Newlines are
        // stripped so a stray paste can't break the cmd line; this is the user's own local config.
        var extra = NormalizeLaunchArgs(extraArgsOverride ?? (isClaude ? Store.Settings.ClaudeLaunchArgs : Store.Settings.CodexLaunchArgs));
        if (!string.IsNullOrEmpty(ParseResumedSessionId(extra)))
            return new ResumeLaunch("", "", cwd, "Refused: configured launch args must not contain a resume/session id.");
        var args = string.IsNullOrEmpty(extra) ? baseArgs : $"{extra} {baseArgs}";
        return new ResumeLaunch(exe, args, cwd, $"\"{exe}\" {args}");
    }

    public bool CanBuildTrustedResumeLaunch(ArchiveSession session)
    {
        var id = ResumeSessionId(session);
        if (!IsResumableId(id)) return false;

        var isClaude = string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase);
        var exe = isClaude ? ResolveClaudeExe() : ResolveCodexExe();
        if (!Path.IsPathRooted(exe) || !File.Exists(exe)) return false;

        var extra = NormalizeLaunchArgs(isClaude ? Store.Settings.ClaudeLaunchArgs : Store.Settings.CodexLaunchArgs);
        return string.IsNullOrEmpty(ParseResumedSessionId(extra));
    }

    // Build a launch for a NEW (un-resumed) chat of `tool` in `cwd`. Resolves a trusted CLI exe exactly
    // like the resume path (so a planted codex.exe in the cwd can't run); "shell" opens a plain terminal.
    // Returns Exe="" with a human reason if the CLI isn't found. DisplayCommand is "" for a shell.
    public ResumeLaunch BuildStartLaunch(string tool, string cwd)
    {
        cwd = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(tool, "shell", StringComparison.OrdinalIgnoreCase))
            return new ResumeLaunch("cmd.exe", "", cwd, "");
        var isClaude = string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase);
        var extra = NormalizeLaunchArgs(isClaude ? Store.Settings.ClaudeLaunchArgs : Store.Settings.CodexLaunchArgs);
        if (!string.IsNullOrEmpty(ParseResumedSessionId(extra)))
            return new ResumeLaunch("", "", cwd, "Fresh chat launch args must not contain a resume/session id; use Resume for an existing chat.");
        var exe = isClaude ? ResolveClaudeExe() : ResolveCodexExe();
        if (!Path.IsPathRooted(exe) || !File.Exists(exe))
            return new ResumeLaunch("", "", cwd, $"The {(isClaude ? "claude" : "codex")} CLI was not found at a trusted path.");
        return new ResumeLaunch(exe, extra, cwd, string.IsNullOrEmpty(extra) ? $"\"{exe}\"" : $"\"{exe}\" {extra}");
    }

    // The command muxd types into the PC-local terminal session to resume THIS chat: cd into the
    // recovered launch dir, then invoke the trusted resolved CLI path. Forward slashes in the cd/exe
    // paths sidestep JSON/backslash escaping when this travels through the relay API. Returns "" if the
    // session id isn't a safe token or the CLI cannot be resolved to a trusted path.
    public string BuildMultiplexCommand(ArchiveSession session, string? exeOverride = null, string? extraArgsOverride = null)
    {
        var launch = BuildResumeLaunch(session, exeOverride, extraArgsOverride);
        if (string.IsNullOrWhiteSpace(launch.Exe) || string.IsNullOrWhiteSpace(launch.Arguments)) return "";
        return BuildMultiplexCommand(launch);
    }

    public string BuildMultiplexStartCommand(string tool, string? cwd = null)
    {
        var launch = BuildStartLaunch(
            tool,
            string.IsNullOrWhiteSpace(cwd)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : cwd);
        if (string.IsNullOrWhiteSpace(launch.Exe)) return "";
        return BuildMultiplexCommand(launch);
    }

    private static string BuildMultiplexCommand(ResumeLaunch launch)
    {
        var cwd = QuotePowerShellSingle(launch.WorkingDirectory.Replace('\\', '/'));
        var exe = QuotePowerShellSingle(launch.Exe.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(launch.Arguments)
            ? $"cd {cwd}; & {exe}"
            : $"cd {cwd}; & {exe} {launch.Arguments}";
    }

    private static string ResumeSessionId(ArchiveSession session)
        => string.IsNullOrWhiteSpace(session.Id) ? Path.GetFileNameWithoutExtension(session.SourcePath) : session.Id;

    private static string NormalizeLaunchArgs(string? args)
        => (args ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    // A readable, collision-resistant mux session name for a chat: a slug of its title plus a short
    // id tail so two chats that happen to share a title still get distinct sessions/tabs. The server
    // re-sanitizes to [A-Za-z0-9_.-]; we pre-shape it here so the tab label is legible on a phone.
    public static string MultiplexSessionName(ArchiveSession session)
    {
        var id = string.IsNullOrWhiteSpace(session.Id) ? Path.GetFileNameWithoutExtension(session.SourcePath) : session.Id;
        var slug = Regex.Replace((session.DisplayTitle ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 28) slug = slug.Substring(0, 28).Trim('-');
        var tail = Regex.Replace(id ?? "", "[^A-Za-z0-9]", "");
        tail = tail.Length > 8 ? tail.Substring(0, 4) + tail.Substring(tail.Length - 4, 4) : tail;
        var tool = string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase) ? "cl" : "cx";
        var name = string.IsNullOrEmpty(slug)
            ? (string.IsNullOrEmpty(tail) ? tool : $"{tool}-{tail}")
            : (string.IsNullOrEmpty(tail) ? slug : $"{slug}-{tail}");
        return string.IsNullOrEmpty(name) ? tool : name;
    }

    // Extract the resumed session id from a claude/codex process command line (or "" if it isn't a
    // resume). Mirrors BuildMultiplexCommand's shapes — `claude --resume <id>` and `codex [--profile
    // ..] resume [--include-non-interactive] <id>` — so we can detect which chats are already running
    // (locally OR inside a multiplex, which runs the agent on this PC too) before launching a second
    // copy. Two runs of one chat fight over the same transcript/rollout, so this is the guard.
    public static string ParseResumedSessionId(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        return ResumedSessionIds(commandLine).FirstOrDefault() ?? "";
    }

    public static bool TryParseSingleResumedSessionId(string commandLine, out string sessionId, out string detail)
    {
        sessionId = "";
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            detail = "command does not resume a stored session";
            return false;
        }
        if (ContainsUnsafeResumeCommandOperator(commandLine))
        {
            detail = "command contains shell control operators; refusing to treat it as a launch authority";
            return false;
        }
        var ids = ResumedSessionIds(commandLine).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            detail = "command does not resume a stored session";
            return false;
        }
        if (ids.Count > 1)
        {
            detail = "command references multiple resume ids; refusing ambiguous launch authority";
            return false;
        }
        sessionId = ids[0];
        detail = "ok";
        return true;
    }

    private static IReadOnlyList<string> ResumedSessionIds(string commandLine)
    {
        var ids = new List<string>();
        static string IdFrom(Match match)
        {
            foreach (var name in new[] { "dq", "sq", "bare" })
                if (match.Groups[name].Success) return match.Groups[name].Value;
            return "";
        }

        var token = @"(?:""(?<dq>[A-Za-z0-9._-]+)""|'(?<sq>[A-Za-z0-9._-]+)'|(?<bare>[A-Za-z0-9._-]+))";
        foreach (Match m in Regex.Matches(commandLine, @"--resume\s+" + token))
        {
            var id = IdFrom(m);
            if (!string.IsNullOrEmpty(id) && IsResumableId(id)) ids.Add(id);
        }
        foreach (Match m in Regex.Matches(commandLine, @"\bresume\b(?:\s+--[A-Za-z-]+)*\s+" + token))
        {
            var id = IdFrom(m);
            if (!string.IsNullOrEmpty(id) && IsResumableId(id)) ids.Add(id);
        }
        return ids;
    }

    private static bool ContainsUnsafeResumeCommandOperator(string commandLine)
        => commandLine.Contains('\r')
           || commandLine.Contains('\n')
           || commandLine.Contains("&&", StringComparison.Ordinal)
           || commandLine.Contains("||", StringComparison.Ordinal)
           || commandLine.Contains("$(", StringComparison.Ordinal)
           || commandLine.Contains('`');

    private static string QuotePowerShellSingle(string value)
        => "'" + (value ?? "").Replace("'", "''") + "'";

    public sealed record RemoteMuxLaunch(
        string SessionId,
        string Tool,
        string Command,
        IReadOnlyList<string> Aliases,
        string Title,
        string Workspace);

    public sealed record PendingMuxBinding(string MuxName, RemoteMuxLaunch Launch);

    public bool TryBuildRemoteMuxLaunch(
        string? sessionId,
        string? tool,
        out RemoteMuxLaunch? launch,
        out string detail,
        Func<ArchiveSession, string>? multiplexCommandFactory = null)
    {
        launch = null;
        var requestedId = (sessionId ?? "").Trim();
        var requestedTool = (tool ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            if (requestedTool is not ("claude" or "codex"))
            {
                detail = "remote mux start refused: tool must be claude or codex";
                return false;
            }
            var startCommand = BuildMultiplexStartCommand(requestedTool);
            if (string.IsNullOrWhiteSpace(startCommand))
            {
                detail = $"remote mux start refused: no trusted {requestedTool} launch is available";
                return false;
            }
            launch = new RemoteMuxLaunch(
                "",
                requestedTool,
                startCommand,
                Array.Empty<string>(),
                requestedTool,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            detail = "ok";
            return true;
        }

        var session = ResolveSessionByIdOrAlias(requestedId, requestedTool);
        if (session is null)
        {
            detail = "remote mux launch refused: session id is not in this app's archive";
            return false;
        }

        var buildCommand = multiplexCommandFactory ?? (s => BuildMultiplexCommand(s));
        var command = buildCommand(session);
        if (string.IsNullOrWhiteSpace(command))
        {
            detail = "remote mux launch refused: this chat has no trusted safe resume command";
            return false;
        }

        launch = new RemoteMuxLaunch(
            session.Id,
            session.Tool,
            command,
            session.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            session.DisplayTitle,
            session.Workspace);
        detail = "ok";
        return true;
    }

    // One live claude/codex agent process on this PC, as seen by the Native process scan. The web's
    // "Running on PC" view lists these so you can SEE what's actually running (incl. forgotten/background
    // VS Code sessions) and kill any of them remotely. SessionId is the chat it's resuming (if any);
    // Core enriches Title/Collection by matching it to the archive.
    public sealed record RunningSessionInfo(
        int Pid, string Tool, string SessionId, string Parent, string StartedAt, string Cwd,
        string RealTitle = "", string Preview = "");

    // The chat's REAL claude/codex name (and a one-line preview), read from the tool's own store — so a
    // session that isn't in our archive (no app title) still shows what it actually is. Codex keeps a
    // `title` + `first_user_message` per thread in state_5.sqlite; returns (title, firstUserMessage).
    public static (string Title, string Preview) ReadCodexThreadTitle(string dbPath, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !File.Exists(dbPath)) return ("", "");
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "select title, first_user_message from threads where id = $id limit 1";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                var title = r.IsDBNull(0) ? "" : r.GetString(0);
                var fum = r.IsDBNull(1) ? "" : r.GetString(1);
                return (title.Trim(), fum.Trim());
            }
        }
        catch { }
        return ("", "");
    }

    // The on-disk rollout (.jsonl) path Codex records for a thread — used to open its transcript.
    public static string? ReadCodexRolloutPath(string dbPath, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !File.Exists(dbPath)) return null;
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "select rollout_path from threads where id = $id limit 1";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    // A compact JSON projection of every collection + a bounded all-chat archive, pushed to the VPS so the
    // web (harmonizerlabs.cc/multiplex → Projects) can list your projects and request a PC-hosted mux
    // resume by id. Each chat carries intent only (id/tool/muxName) plus a `running` flag; the local app
    // rebuilds the trusted command from its archive before launching. Only chats with a safe, locally
    // launchable resume intent are included.
    // `runningSessions` is the full list of live agent processes (for the web's "Running on PC" view),
    // each enriched here with the matched chat title + collection name when the session id is known.
    public string BuildProjectsProjectionJson(
        ISet<string>? runningIds = null,
        IEnumerable<RunningSessionInfo>? runningSessions = null,
        bool runningVerified = true,
        string runningVerificationDetail = "")
    {
        EnsureDecks();
        var deckNames = Store.Decks.ToDictionary(d => d.Id, d => d.Name, StringComparer.OrdinalIgnoreCase);
        var deckOrder = Store.Decks.Select((d, i) => new { d.Id, Order = i })
            .ToDictionary(d => d.Id, d => d.Order, StringComparer.OrdinalIgnoreCase);
        var decks = Store.Decks
            .Select(d => new
            {
                id = d.Id,
                name = d.Name,
                collections = CollectionCountInDeck(d.Id),
            })
            .ToList();
        var collectionBySession = new Dictionary<string, (string Collection, string DeckId, string Deck)>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in Store.Collections.Values
                     .OrderBy(c => deckOrder.TryGetValue(CollectionDeck(c), out var order) ? order : int.MaxValue)
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var deckId = CollectionDeck(col);
            var deck = deckNames.TryGetValue(deckId, out var dn) ? dn : "Main";
            foreach (var sid in col.SessionIds)
                if (!string.IsNullOrWhiteSpace(sid) && !collectionBySession.ContainsKey(sid))
                    collectionBySession[sid] = (col.Name, deckId, deck);
        }
        object? ChatProjection(ArchiveSession s)
        {
            if (!CanBuildTrustedResumeLaunch(s)) return null;
            collectionBySession.TryGetValue(s.Id, out var col);
            return new
                {
                    id = s.Id,
                    aliases = s.Aliases
                        .Where(a => !string.IsNullOrWhiteSpace(a) && !string.Equals(a, s.Id, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    title = s.DisplayTitle,
                    nativeTitle = s.Title,       // the tool's own name (Codex thread title / Claude title)
                    appTitle = s.CustomTitle,    // this app's optional override (empty = falls back to nativeTitle)
                    tool = s.Tool,
                    muxName = MultiplexSessionName(s),
                    running = runningIds is not null
                        && new[] { s.Id }.Concat(s.Aliases)
                            .Any(id => !string.IsNullOrWhiteSpace(id) && runningIds.Contains(id)),
                    updatedAt = s.UpdatedAt,
                    workspaceLabel = s.WorkspaceName,
                collection = string.IsNullOrEmpty(col.Collection) ? null : col.Collection,
                collectionDeckId = string.IsNullOrEmpty(col.DeckId) ? null : col.DeckId,
                collectionDeck = string.IsNullOrEmpty(col.Deck) ? null : col.Deck,
            };
        }
        var collections = Store.Collections.Values
            .OrderBy(c => deckOrder.TryGetValue(CollectionDeck(c), out var order) ? order : int.MaxValue)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(col => new
            {
                id = col.Id,
                name = col.Name,
                deckId = CollectionDeck(col),
                deckName = deckNames.TryGetValue(CollectionDeck(col), out var deckName) ? deckName : "Main",
                chats = col.SessionIds
                    .Select(sid => Store.Sessions.TryGetValue(sid, out var s) ? s : null)
                    .Where(s => s is not null)
                    .Select(s => ChatProjection(s!))
                    .Where(c => c is not null)
                    .ToList(),
            })
            .Where(c => c.chats.Count > 0)
            .ToList();
        var allChats = OrderedVisibleSessions(Store.Sessions.Values)
            .Select(ChatProjection)
            .Where(c => c is not null)
            .Take(500)
            .ToList();

        // Map every known chat's id + aliases -> (title, collection, deck) so a running process can be named.
        var byId = new Dictionary<string, (string Title, string Collection, string DeckId, string Deck)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Store.Sessions.Values)
        {
            collectionBySession.TryGetValue(s.Id, out var col);
            if (!string.IsNullOrEmpty(s.Id)) byId[s.Id] = (s.DisplayTitle, col.Collection, col.DeckId, col.Deck);
            foreach (var a in s.Aliases)
                if (!string.IsNullOrEmpty(a)) byId[a] = (s.DisplayTitle, col.Collection, col.DeckId, col.Deck);
        }
        foreach (var col in Store.Collections.Values)
            foreach (var sid in col.SessionIds)
                if (Store.Sessions.TryGetValue(sid, out var s) && s is not null)
                {
                    var deckId = CollectionDeck(col);
                    var deck = deckNames.TryGetValue(deckId, out var dn) ? dn : "Main";
                    if (!string.IsNullOrEmpty(s.Id)) byId[s.Id] = (s.DisplayTitle, col.Name, deckId, deck);
                    foreach (var a in s.Aliases) if (!string.IsNullOrEmpty(a)) byId[a] = (s.DisplayTitle, col.Name, deckId, deck);
                }

        var running = (runningSessions ?? Enumerable.Empty<RunningSessionInfo>())
            .Select(r =>
            {
                byId.TryGetValue(r.SessionId ?? "", out var hit);
                return new
                {
                    pid = r.Pid,
                    tool = r.Tool,
                    sessionId = r.SessionId,
                    parent = r.Parent,
                    startedAt = r.StartedAt,
                    title = hit.Title,             // null when the running session isn't in any collection
                    collection = hit.Collection,
                    collectionDeckId = hit.DeckId,
                    collectionDeck = hit.Deck,
                    realTitle = string.IsNullOrEmpty(r.RealTitle) ? null : r.RealTitle,   // the tool's OWN name
                };
            })
            .OrderByDescending(r => r.startedAt, StringComparer.Ordinal)
            .ToList();

        var muxTabChats = ResolveMuxTabChats();
        var muxTabMeta = Store.MuxTabMeta.ToDictionary(
            kv => kv.Key, kv => (object)new { color = kv.Value.Color, kind = kv.Value.Kind }, StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            host = Environment.MachineName,
            decks,
            collections,
            allChats,
            runningSessions = running,
            runningVerified,
            runningVerificationDetail,
            muxTabChats,
            muxTabMeta
        });
    }

    // Set a tab's color (hex like "#e879f9", or "" to clear) — the web tab-color picker.
    public async Task SetTabColorAsync(string tabName, string? color)
    {
        if (string.IsNullOrWhiteSpace(tabName)) return;
        if (!Store.MuxTabMeta.TryGetValue(tabName, out var m)) { m = new MuxTabMeta(); Store.MuxTabMeta[tabName] = m; }
        m.Color = (color ?? "").Trim();
        if (string.IsNullOrEmpty(m.Color) && string.IsNullOrEmpty(m.Kind)) Store.MuxTabMeta.Remove(tabName);
        await SaveAsync();
    }

    // Flag a tab's kind (e.g. "remote-resumed") + optional default color. Set when /tomux hands a local
    // session off to multiplex, so the web tints it as a resumed-remote by default.
    public void SetTabKind(string tabName, string kind, string? defaultColor = null)
    {
        if (string.IsNullOrWhiteSpace(tabName)) return;
        if (!Store.MuxTabMeta.TryGetValue(tabName, out var m)) { m = new MuxTabMeta(); Store.MuxTabMeta[tabName] = m; }
        m.Kind = kind ?? "";
        if (!string.IsNullOrEmpty(defaultColor) && string.IsNullOrEmpty(m.Color)) m.Color = defaultColor;
    }

    private readonly object _resolvedMuxBindingsGate = new();
    private Dictionary<string, (string Id, string Tool, string Cwd)> _resolvedMuxBindings =
        new(StringComparer.OrdinalIgnoreCase);

    // Deterministically link each unresolved mux tab (a shell-started agent or a fresh trusted CLI launch)
    // to its live chat. Once a fresh launch is identified, callers bind the trusted resume command into muxd.
    // live chat, so it can be filed into a collection / relaunched by real id. muxd writes {tab:{pid,cwd}} to
    // live-tabs.json; we match a running claude/codex to its tab by walking the process's ancestry to that
    // shell pid, then take the resume id from its command line, or (fresh agent) the newest transcript in the
    // tab's folder — skipping any folder shared by 2+ tabs so we never mislabel. Refreshed each projection push.
    private Dictionary<string, object> ResolveMuxTabChats()
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "muxd", "live-tabs.json");
        Dictionary<string, JsonElement>? tabs = null;
        try { if (File.Exists(path)) tabs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)); }
        catch { }
        if (tabs is null || tabs.Count == 0)
        {
            lock (_resolvedMuxBindingsGate)
                _resolvedMuxBindings = new(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        var shellPidToTab = new Dictionary<int, string>();
        var tabCwd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, el) in tabs)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var alive = el.TryGetProperty("alive", out var a) && a.ValueKind == JsonValueKind.True;
                var hasCmd = el.TryGetProperty("hasCommand", out var h) && h.ValueKind == JsonValueKind.True;
                var pid = el.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                var cwd = el.TryGetProperty("cwd", out var c) ? (c.GetString() ?? "") : "";
                var sessionId = el.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "";
                var identityPending = el.TryGetProperty("identityPending", out var pending)
                                      && pending.ValueKind == JsonValueKind.True;
                if (alive && pid > 0 && (!hasCmd || identityPending || string.IsNullOrWhiteSpace(sessionId)))
                {
                    shellPidToTab[pid] = name;
                    tabCwd[name] = cwd;
                }
            }
            catch { }
        }
        if (shellPidToTab.Count == 0)
        {
            lock (_resolvedMuxBindingsGate)
                _resolvedMuxBindings = new(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        var ppid = RunningSessions.ProcessParentMap();
        var agents = RunningSessions.ScanAgentsWithPpid();
        var cwdShareCount = tabCwd.Values.Where(c => !string.IsNullOrEmpty(c))
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        bool AncestorTab(int startPid, out string tab)
        {
            tab = "";
            var cur = startPid;
            for (var i = 0; i < 6 && cur > 0; i++)
            {
                if (shellPidToTab.TryGetValue(cur, out var t)) { tab = t; return true; }
                if (!ppid.TryGetValue(cur, out var np) || np == cur) break;
                cur = np;
            }
            return false;
        }

        // PASS 1 — propose a (tab, id) for each live agent under a tab, with a CONFIDENCE (delta, smaller =
        // surer): an explicit --resume id is certain (-1); a fresh agent is correlated to the transcript
        // created nearest its process start (delta = seconds apart); the cwd fallback is last-resort.
        var proposals = new List<(string tab, string id, string tool, double delta)>();
        foreach (var ag in agents)
        {
            if (!AncestorTab(ag.Ppid, out var tab) && !AncestorTab(ag.Pid, out tab)) continue;
            if (!string.IsNullOrEmpty(ag.SessionId)) { proposals.Add((tab, ag.SessionId, ag.Tool, -1)); continue; }

            var cwd = tabCwd.TryGetValue(tab, out var cw) ? cw : "";
            var (fid, fdelta) = FreshAgentSessionIdByStart(ag.Tool, cwd, ag.StartedUtc);
            if (!string.IsNullOrEmpty(fid)) { proposals.Add((tab, fid!, ag.Tool, fdelta)); continue; }

            // Last resort (only when correlation found nothing AND the cwd is unambiguous): newest chat in cwd.
            if (!string.IsNullOrEmpty(cwd) && !(cwdShareCount.TryGetValue(cwd, out var n) && n > 1))
            {
                var cand = Store.Sessions.Values
                    .Where(s => string.Equals(s.Workspace, cwd, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(s.Tool, ag.Tool, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (cand is not null) proposals.Add((tab, cand.Id, ag.Tool, 1e9));   // huge delta = least confident
            }
        }

        // PASS 2 — greedy best-match assignment: no TAB or CHAT id is used twice, so two tabs can NEVER bind
        // the same chat (a contested fresh chat goes to the tab whose start-time matches best).
        var assignments = AssignTabChats(proposals).ToList();
        var resolvedBindings = new Dictionary<string, (string Id, string Tool, string Cwd)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tab, id, tool) in assignments)
        {
            Store.Sessions.TryGetValue(id, out var chat);
            var title = chat?.DisplayTitle ?? tab;
            RecordTabChat(tab, id, tool, title);   // rotate the tab's session history
            resolvedBindings[tab] = (id, tool, tabCwd.TryGetValue(tab, out var cwd) ? cwd : "");
        }
        lock (_resolvedMuxBindingsGate) _resolvedMuxBindings = resolvedBindings;

        // PER-SHELL, always: a tab's ledger is built ONLY from agents whose process tree traces to THAT
        // shell's pid (above) — never from the folder. So two shells in the same folder keep completely
        // independent ledgers, and a shell never picks up chats another shell or a background job created.

        // Emit a row for EVERY alive shell tab that has a ledger — built from the (now fully updated) record.
        // This includes tabs whose agent has EXITED (a bare shell now): without it the history vanished the
        // moment you closed claude/codex; now the tab keeps its full ordered session history until deleted.
        foreach (var tab in shellPidToTab.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Store.MuxTabHistory.TryGetValue(tab, out var rec) || (rec.Current is null && rec.History.Count == 0)) continue;
            var cur = rec.Current;
            result[tab] = new
            {
                id = cur?.Id ?? "", tool = cur?.Tool ?? "", title = cur?.Title ?? tab,
                history = rec.History.Select(h => new { id = h.Id, tool = h.Tool, title = h.Title, at = h.At }).ToList(),
            };
        }
        return result;
    }

    public IReadOnlyList<PendingMuxBinding> ResolvePendingMuxBindings()
    {
        ResolveMuxTabChats();
        Dictionary<string, (string Id, string Tool, string Cwd)> snapshot;
        lock (_resolvedMuxBindingsGate)
            snapshot = new(_resolvedMuxBindings, StringComparer.OrdinalIgnoreCase);

        var bindings = new List<PendingMuxBinding>();
        foreach (var (muxName, resolved) in snapshot)
        {
            var session = ResolveSessionByIdOrAlias(resolved.Id, resolved.Tool)
                          ?? new ArchiveSession
                          {
                              Id = resolved.Id,
                              Tool = resolved.Tool,
                              Title = muxName,
                              Workspace = resolved.Cwd,
                          };
            var command = BuildMultiplexCommand(session);
            if (string.IsNullOrWhiteSpace(command)) continue;
            bindings.Add(new PendingMuxBinding(
                muxName,
                new RemoteMuxLaunch(
                    session.Id,
                    session.Tool,
                    command,
                    session.Aliases.ToArray(),
                    session.DisplayTitle,
                    session.Workspace)));
        }
        return bindings;
    }

    // Greedy best-match assignment of live agents to their tabs: process proposals most-confident first
    // (smaller delta = surer; explicit --resume id = -1), and use each TAB and each CHAT id at most once.
    // GUARANTEE: two tabs can never be bound to the same chat, so flopping between 5 chats never confuses
    // them — a contested fresh chat goes to the tab whose process start-time matches its transcript best.
    public static IEnumerable<(string tab, string id, string tool)> AssignTabChats(
        IEnumerable<(string tab, string id, string tool, double delta)> proposals)
    {
        var winners = new List<(string, string, string)>();
        var usedTab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedId = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in proposals.OrderBy(p => p.delta))
        {
            if (string.IsNullOrEmpty(p.tab) || string.IsNullOrEmpty(p.id)) continue;
            if (usedTab.Contains(p.tab) || usedId.Contains(p.id)) continue;
            usedTab.Add(p.tab); usedId.Add(p.id);
            winners.Add((p.tab, p.id, p.tool));
        }
        return winners;
    }

    // The session id whose transcript was created CLOSEST to when a fresh agent (launched without an explicit
    // --resume id) started, within its cwd. Reads the transcript folder directly so a brand-new chat is caught
    // immediately, and correlates by process-start-time so it never relabels a fresh session as an old one.
    private (string? Id, double Delta) FreshAgentSessionIdByStart(string tool, string cwd, DateTime startUtc)
    {
        try
        {
            if (startUtc == default) return (null, double.MaxValue);
            const double windowSec = 240;   // a fresh session's transcript appears within a few minutes of launch
            if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase))
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
                if (!Directory.Exists(root)) return (null, double.MaxValue);
                string? best = null; var bestDelta = double.MaxValue;
                foreach (var dayDir in RecentCodexDayDirs(root, 2))          // only the newest day-dirs can hold a just-now session
                    foreach (var f in SafeEnumerateFiles(dayDir, "rollout-*.jsonl"))
                    {
                        DateTime ct; try { ct = File.GetCreationTimeUtc(f); } catch { continue; }
                        var delta = Math.Abs((ct - startUtc).TotalSeconds);
                        if (delta > windowSec || delta >= bestDelta) continue;
                        var uuid = CodexRolloutFileId(f);
                        if (!string.IsNullOrEmpty(uuid)) { best = uuid; bestDelta = delta; }
                    }
                return (best, bestDelta);
            }
            // Claude: the transcript in this cwd's project folder whose creation time is nearest the launch.
            var m = ClaudeFolderTranscripts(cwd)
                .Where(t => Math.Abs((t.created - startUtc).TotalSeconds) <= windowSec)
                .OrderBy(t => Math.Abs((t.created - startUtc).TotalSeconds))
                .Select(t => (id: (string?)t.id, delta: Math.Abs((t.created - startUtc).TotalSeconds)))
                .FirstOrDefault();
            return (m.id, m.id is null ? double.MaxValue : m.delta);
        }
        catch { return (null, double.MaxValue); }
    }

    // The most-recent N day-directories under ~/.codex/sessions (year/month/day), newest first — so a
    // fresh-session scan only walks today/yesterday, not the whole history.
    private static IEnumerable<string> RecentCodexDayDirs(string root, int n)
    {
        var days = new List<string>();
        foreach (var y in SafeEnumerateDirs(root))
            foreach (var m in SafeEnumerateDirs(y))
                foreach (var d in SafeEnumerateDirs(m))
                    days.Add(d);
        days.Sort(StringComparer.Ordinal);   // date-named dirs sort chronologically
        days.Reverse();
        return days.Take(n);
    }
    private static IEnumerable<string> SafeEnumerateDirs(string p) { try { return Directory.EnumerateDirectories(p); } catch { return Array.Empty<string>(); } }
    private static IEnumerable<string> SafeEnumerateFiles(string p, string pat) { try { return Directory.EnumerateFiles(p, pat); } catch { return Array.Empty<string>(); } }

    // Refresh tab→chat links + rotate each tab's session history NOW (for the fast tab-tracking tick), so a
    // brief `claude` → `codex` → exit is captured even between the slower projection pushes. In-memory only;
    // the next projects push persists it and sends it to the web.
    public void TrackMuxTabsNow()
    {
        try { ResolveMuxTabChats(); } catch { }
    }


    private long _muxHistoryVersion;
    private long _muxHistorySavedVersion;
    // Note the chat currently in a tab; when it CHANGES, the previous chat rotates into that tab's history
    // (dedup by id, most-recent first, capped) so no session a tab ever hosted is lost. Persisted lazily.
    private void RecordTabChat(string tab, string id, string tool, string title)
    {
        if (string.IsNullOrEmpty(tab) || string.IsNullOrEmpty(id)) return;
        if (!Store.MuxTabHistory.TryGetValue(tab, out var rec)) { rec = new MuxTabRecord { FirstSeen = DateTime.UtcNow.ToString("O") }; Store.MuxTabHistory[tab] = rec; }
        if (rec.Current is not null && string.Equals(rec.Current.Id, id, StringComparison.OrdinalIgnoreCase))
        {
            rec.Current.Title = title; rec.Current.At = DateTime.UtcNow.ToString("O");
            return;   // same chat still current — refresh, no rotation
        }
        if (rec.Current is not null)
        {
            rec.History.RemoveAll(h => string.Equals(h.Id, rec.Current.Id, StringComparison.OrdinalIgnoreCase));
            rec.History.Insert(0, rec.Current);
        }
        rec.History.RemoveAll(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));   // new current shouldn't also sit in history
        if (rec.History.Count > 30) rec.History.RemoveRange(30, rec.History.Count - 30);
        rec.Current = new MuxTabChat { Id = id, Tool = tool, Title = title, At = DateTime.UtcNow.ToString("O") };
        _muxHistoryVersion++;
    }

    // Clear a tab's session history — or ALL tabs' when tabName is empty (the web "delete all tabs / start
    // fresh"). Returns how many tab records were cleared. Persists immediately.
    public async Task<int> ClearMuxTabHistoryAsync(string? tabName = null)
    {
        int n;
        if (string.IsNullOrWhiteSpace(tabName)) { n = Store.MuxTabHistory.Count; Store.MuxTabHistory.Clear(); }
        else { n = Store.MuxTabHistory.Remove(tabName!) ? 1 : 0; }
        if (n > 0)
        {
            var version = ++_muxHistoryVersion;
            await SaveAsync();
            _muxHistorySavedVersion = Math.Max(_muxHistorySavedVersion, version);
        }
        return n;
    }

    // Persist tab history if the resolver rotated anything (called from the projects push, off the hot path).
    public async Task SaveMuxHistoryIfDirtyAsync()
    {
        var version = _muxHistoryVersion;
        if (version <= _muxHistorySavedVersion) return;
        await SaveAsync();
        _muxHistorySavedVersion = Math.Max(_muxHistorySavedVersion, version);
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

    // Claude Code files a transcript under a project folder named by DASH-ENCODING the directory that
    // `claude` was launched from (each \ / : . and whitespace becomes '-'). `claude --resume <id>`
    // only finds the session when run from THAT directory. The cwd recorded inside the transcript can
    // be a SUBDIR of the launch dir (e.g. launched in …\301, cwd later moved to …\301\graphics), so
    // resuming from the recorded workspace fails with "No conversation found with session ID". Recover
    // the real launch dir by climbing from the workspace until a directory's encoding matches the
    // transcript's project-folder name. Falls back to the recorded workspace if nothing matches.
    private static string ResolveClaudeResumeDirectory(ArchiveSession session)
    {
        var fallback = ResolveWorkingDirectory(session);
        var projectFolder = Path.GetFileName(Path.GetDirectoryName(session.SourcePath) ?? "");
        if (string.IsNullOrWhiteSpace(projectFolder)) return fallback;

        foreach (var start in new[] { session.Workspace, fallback })
        {
            var dir = string.IsNullOrWhiteSpace(start) ? null : start;
            for (var guard = 0; guard < 64 && !string.IsNullOrWhiteSpace(dir); guard++)
            {
                if (string.Equals(EncodeClaudeProjectFolder(dir!), projectFolder, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(dir!))
                    return dir!;
                dir = Path.GetDirectoryName(dir!.TrimEnd('\\', '/'));
            }
        }
        return fallback;
    }

    // Mirror Claude Code's project-folder encoding: every path separator, drive colon, dot, or
    // whitespace char becomes '-', preserving case and any literal dashes already in the path.
    private static string EncodeClaudeProjectFolder(string path) =>
        Regex.Replace(path.TrimEnd('\\', '/'), @"[\\/:.\s]", "-");

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

    private async Task<ParsedTranscript> ParseCodexCoreAsync(string filePath)
    {
        var messages = new ObservableCollection<ArchiveMessage>();
        var codeBlocks = new ObservableCollection<CodeBlock>();
        var fallbackMessages = new ObservableCollection<ArchiveMessage>();
        var fallbackCodeBlocks = new ObservableCollection<CodeBlock>();
        var fileId = CodexRolloutFileId(filePath);
        var id = fileId ?? Path.GetFileNameWithoutExtension(filePath);
        var cwd = "";
        var created = "";
        var updated = "";

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAlias(aliases, fileId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fallbackSeen = new HashSet<string>(StringComparer.Ordinal);
        var sawEventMessages = false;
        var sessionMetaAssigned = false;
        var info = new FileInfo(filePath);
        updated = info.LastWriteTimeUtc.ToString("O");
        ArchiveMessage? lastTool = null;   // the function_call awaiting its function_call_output

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
                    // The resumable Codex thread id comes from the first session_meta header. In
                    // fork/subagent rollouts, payload.id and the rollout filename can be the current
                    // child while session_id is the parent; later embedded parent metas are aliases only.
                    var rootType = root.TryGetProperty("type", out var rtProp) ? ElementString(rtProp) : null;
                    if (rootType == "session_meta")
                    {
                        var sid = payload.TryGetProperty("session_id", out var sidProp) ? ElementString(sidProp) : null;
                        var mid = payload.TryGetProperty("id", out var midProp) ? ElementString(midProp) : null;
                        AddAlias(aliases, sid);
                        AddAlias(aliases, mid);
                        if (!sessionMetaAssigned)
                        {
                            var canonical = CodexCanonicalSessionId(fileId, sid, mid);
                            if (!string.IsNullOrWhiteSpace(canonical))
                            {
                                id = canonical!;
                                sessionMetaAssigned = true;
                            }
                            if (sessionMetaAssigned
                                && payload.TryGetProperty("cwd", out var metaCwdProp)
                                && ElementString(metaCwdProp) is { Length: > 0 } metaCwd)
                                cwd = metaCwd;
                        }
                        if (payload.TryGetProperty("forked_from_id", out var forkProp) && ElementString(forkProp) is { Length: > 0 } fork)
                        {
                            AddAlias(aliases, fork);
                        }
                    }
                    if (string.IsNullOrWhiteSpace(cwd)
                        && payload.TryGetProperty("cwd", out var cwdProp)
                        && ElementString(cwdProp) is { Length: > 0 } cw) cwd = cw;

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

        // Title + first-user come from the HEAD (captured before any tail swap) so a long chat keeps its
        // real opening prompt even when we show its recent tail.
        var titleSeed = messages.FirstOrDefault(m => m.Role == "user" && IsTitleCandidate(m.Text))?.Text;
        var firstUser = FirstUserText(messages);
        var totalMessages = messages.Count;

        // A huge rollout was truncated at the head cap — the reader would then show the OLDEST turns, not
        // the recent ones (the "transcript stale from 3 days ago" bug). Re-read the FILE TAIL and swap in
        // the recent messages, exactly like the Claude parser does. Head-derived meta/title stay put.
        if (hitLineCap)
        {
            var tail = ParseCodexTail(filePath, info);
            if (tail.messages.Count > 0)
            {
                messages = tail.messages;
                codeBlocks = tail.codeBlocks;
                if (string.IsNullOrWhiteSpace(cwd) && !string.IsNullOrWhiteSpace(tail.cwd)) cwd = tail.cwd;
                if (!string.IsNullOrWhiteSpace(tail.updated)) updated = tail.updated;
            }
        }

        if (string.IsNullOrWhiteSpace(created)) created = info.CreationTimeUtc.ToString("O");
        if (string.IsNullOrWhiteSpace(updated)) updated = info.LastWriteTimeUtc.ToString("O");

        // Title comes from the first prompt, captured from the HEAD before any tail swap (so a long chat
        // keeps its real opening title).
        var title = CleanFallbackTitle(titleSeed ?? Path.GetFileNameWithoutExtension(filePath));
        return new ParsedTranscript(messages, codeBlocks, id, title, created, updated, cwd, aliases, totalMessages, "codex", firstUser);
    }

    // Thin wrapper: full Codex parse, then trim to the recent-message window for the in-app reader.
    private async Task<ArchiveSession> ParseJsonlAsync(string filePath)
    {
        var p = await ParseCodexCoreAsync(filePath);
        var (messages, codeBlocks) = KeepRecentWindow(p.Messages, p.CodeBlocks);
        return new ArchiveSession
        {
            Id = p.Id,
            Title = p.Title,
            SourcePath = filePath,
            Aliases = new ObservableCollection<string>(p.Aliases.Where(a => !string.Equals(a, p.Id, StringComparison.OrdinalIgnoreCase))),
            CreatedAt = p.Created,
            UpdatedAt = p.Updated,
            Workspace = string.IsNullOrWhiteSpace(p.Cwd) ? "Unknown workspace" : p.Cwd,
            WorkspaceName = string.IsNullOrWhiteSpace(p.Cwd) ? "Unknown" : Path.GetFileName(p.Cwd.TrimEnd('\\', '/')),
            Model = "codex",
            Tool = "codex",
            Messages = messages,
            CodeBlocks = codeBlocks,
            MessageCount = p.Total,
            ContentLoaded = true,
            Text = CapText(string.Join("\n\n", messages.Select(m => m.Text))),
            LastUserMessage = LastUserText(p.Messages),
            FirstUserMessage = p.FirstUser,
            UserMessageCount = CountUserPrompts(filePath, p.Tool),
            Tags = new ObservableCollection<string>(codeBlocks.Count > 0 ? new[] { "archive", "code" } : new[] { "archive" })
        };
    }

    private static void AddAlias(HashSet<string> aliases, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (IsAliasToken(trimmed)) aliases.Add(trimmed);
    }

    private static void AddAlias(ObservableCollection<string> aliases, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (IsAliasToken(trimmed) && !aliases.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) aliases.Add(trimmed);
    }

    private static string? CodexRolloutFileId(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var match = Regex.Match(stem, @"(?:^|-)(" + UuidPattern + @")$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private const string UuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    private static string? CodexCanonicalSessionId(string? fileId, string? sessionId, string? metaId)
    {
        if (!string.IsNullOrWhiteSpace(fileId))
        {
            if (string.Equals(metaId, fileId, StringComparison.OrdinalIgnoreCase)) return metaId;
            if (string.Equals(sessionId, fileId, StringComparison.OrdinalIgnoreCase)) return sessionId;
            return fileId;
        }
        return !string.IsNullOrWhiteSpace(metaId) ? metaId : sessionId;
    }

    // Claude Code transcript: one JSON object per line with sessionId/cwd/timestamp and a
    // message{role,content[]}. content is an array of {type:"text",text} blocks (plus tool_use/
    // tool_result we skip for the reader). Mirrors ParseJsonlAsync but for Claude's shape.
    private async Task<ParsedTranscript?> ParseClaudeCoreAsync(string filePath)
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
        var firstUserClaude = FirstUserText(messages);   // from the HEAD, before any tail swap
        var totalClaude = messages.Count;
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
        return new ParsedTranscript(messages, codeBlocks, id, title, created, updated, cwd,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), totalClaude, "claude", firstUserClaude);
    }

    // Thin wrapper: full Claude parse (null = sidechain/subagent file), then trim to the reader window.
    private async Task<ArchiveSession?> ParseClaudeSessionAsync(string filePath)
    {
        var p = await ParseClaudeCoreAsync(filePath);
        if (p is null) return null;
        var (messages, codeBlocks) = KeepRecentWindow(p.Messages, p.CodeBlocks);
        return new ArchiveSession
        {
            Id = p.Id,
            Title = p.Title,
            SourcePath = filePath,
            CreatedAt = p.Created,
            UpdatedAt = p.Updated,
            Workspace = string.IsNullOrWhiteSpace(p.Cwd) ? "Unknown workspace" : p.Cwd,
            WorkspaceName = string.IsNullOrWhiteSpace(p.Cwd) ? "Unknown" : Path.GetFileName(p.Cwd.TrimEnd('\\', '/')),
            Model = "claude",
            Tool = "claude",
            Messages = messages,
            CodeBlocks = codeBlocks,
            MessageCount = p.Total,
            ContentLoaded = true,
            Text = CapText(string.Join("\n\n", messages.Select(m => m.Text))),
            LastUserMessage = LastUserText(p.Messages),
            FirstUserMessage = p.FirstUser,
            UserMessageCount = CountUserPrompts(filePath, p.Tool),
            Tags = new ObservableCollection<string>(codeBlocks.Count > 0 ? new[] { "archive", "code" } : new[] { "archive" })
        };
    }

    // Recent-message window for a Codex rollout that overflowed the head line-cap: read the FILE TAIL and
    // rebuild the latest messages (event_msg turns + response_item tool steps), mirroring ParseClaudeTail.
    // Meta (id/cwd/created/title) is still taken from the head pass; this only supplies the recent messages.
    private static (ObservableCollection<ArchiveMessage> messages, ObservableCollection<CodeBlock> codeBlocks, string cwd, string updated)
        ParseCodexTail(string path, FileInfo info)
    {
        var messages = new ObservableCollection<ArchiveMessage>();
        var codeBlocks = new ObservableCollection<CodeBlock>();
        var fallbackMessages = new ObservableCollection<ArchiveMessage>();
        var fallbackCodeBlocks = new ObservableCollection<CodeBlock>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fallbackSeen = new HashSet<string>(StringComparer.Ordinal);
        var sawEventMessages = false;
        ArchiveMessage? lastTool = null;
        var cwd = "";
        var updated = info.LastWriteTimeUtc.ToString("O");

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var n = (int)Math.Min(fs.Length, CodexTailBytes);
            if (n <= 0) return (messages, codeBlocks, cwd, updated);
            var offset = fs.Length - n;
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[n];
            var read = fs.Read(buf, 0, n);
            var lines = Encoding.UTF8.GetString(buf, 0, read).Replace("\r\n", "\n").Split('\n');
            var usable = lines.Skip(offset > 0 ? 1 : 0).ToList();   // first line is partial when starting mid-file
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
                        var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(timestamp)) updated = timestamp;
                        if (!root.TryGetProperty("payload", out var payload)) continue;
                        var rootType = root.TryGetProperty("type", out var rtProp) ? ElementString(rtProp) : null;
                        if (string.IsNullOrWhiteSpace(cwd) && payload.TryGetProperty("cwd", out var cwdProp) && ElementString(cwdProp) is { Length: > 0 } cw) cwd = cw;
                        var pType = payload.TryGetProperty("type", out var typeProp) ? ElementString(typeProp) : null;
                        if (rootType == "event_msg")
                        {
                            if (pType is "user_message" or "agent_message")
                            {
                                sawEventMessages = true;
                                var role = pType == "agent_message" ? "assistant" : "user";
                                AddMessage(messages, codeBlocks, role, Field(payload, "message"), timestamp, seen);
                            }
                        }
                        else if (rootType == "response_item")
                        {
                            if (pType == "function_call")
                            {
                                var name = Field(payload, "name");
                                if (string.IsNullOrWhiteSpace(name)) name = "tool";
                                lastTool = AddToolStep(messages, name, ExtractToolCommand(payload), timestamp);
                            }
                            else if (pType == "function_call_output" && lastTool is not null)
                            {
                                lastTool.ToolOutput = CapDisplayText(Field(payload, "output"), 4000);
                            }
                            else if (pType == "message")
                            {
                                var role = Field(payload, "role");
                                if (string.IsNullOrWhiteSpace(role)) role = "message";
                                if (IsIndexedRole(role)) AddMessage(fallbackMessages, fallbackCodeBlocks, role, ExtractContent(payload), timestamp, fallbackSeen);
                            }
                        }
                    }
                    catch
                    {
                        // One bad line never discards the tail window.
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
        }
        catch
        {
            // Tail read is best-effort; the head parse already produced a usable (if older) transcript.
        }
        return (messages, codeBlocks, cwd, updated);
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
        return $"{session.Id}\n{session.DisplayTitle}\n{session.Title}\n{session.Text}\n{session.SourcePath}\n{session.Workspace}\n{string.Join(' ', session.Tags)}\n{string.Join(' ', session.SpecialPhrases)}";
    }

    private static IEnumerable<ArchiveSearchHit> DeepHitsForSession(ArchiveSession session, string[] terms)
    {
        var sessionScore = FuzzyScore($"{session.DisplayTitle}\n{session.WorkspaceName}\n{session.Workspace}\n{session.SourcePath}\n{string.Join(' ', session.Tags)}\n{string.Join(' ', session.SpecialPhrases)}", terms);
        if (sessionScore > 0)
        {
            yield return new ArchiveSearchHit
            {
                Session = session,
                SourceLabel = "title/path/tags/codename",
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
                foreach (var line in SafeReadLines(file))   // shared read: codex writes session_index live
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
