using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.SearchBenchmark;

internal static partial class Program
{
    private const int CasesPerStratum = 2;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(
                "SearchBenchmark prepare|build-index|run " +
                "[--manifest path] [--output path] [--index path] [--surface name] " +
                "[--fresh] [--legacy]\n" +
                "SearchBenchmark inspect-index|repair-manifest " +
                "[--manifest path] [--output path] [--index path]");
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var artifactRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLocalRetrieval",
            "search-benchmark");
        Directory.CreateDirectory(artifactRoot);
        var manifestPath = Arg(args, "--manifest")
            ?? Path.Combine(artifactRoot, "real-corpus-manifest.json");
        var outputPath = Arg(args, "--output")
            ?? Path.Combine(artifactRoot, $"results-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var indexPath = Arg(args, "--index") ?? ProductionSearchIndexPath();
        var surface = Arg(args, "--surface");
        var fresh = args.Any(arg =>
            string.Equals(arg, "--fresh", StringComparison.OrdinalIgnoreCase));
        var legacy = args.Any(arg =>
            string.Equals(arg, "--legacy", StringComparison.OrdinalIgnoreCase));

        return command switch
        {
            "prepare" => await PrepareAsync(manifestPath),
            "build-index" => await BuildIndexAsync(
                manifestPath,
                outputPath,
                indexPath,
                fresh),
            "run" => await RunAsync(manifestPath, outputPath, indexPath, surface, legacy),
            "inspect-index" => await InspectIndexAsync(manifestPath, outputPath, indexPath),
            "repair-manifest" => await RepairManifestAsync(manifestPath, outputPath, indexPath),
            _ => throw new ArgumentException($"Unknown command '{command}'."),
        };
    }

    private static async Task<int> PrepareAsync(string manifestPath)
    {
        var archive = await OpenSnapshotArchiveAsync(Path.GetDirectoryName(manifestPath)!);
        var sessions = archive.Store.Sessions.Values
            .Where(session => !session.Archived && File.Exists(session.SourcePath))
            .ToList();
        var byPath = sessions
            .GroupBy(session => Path.GetFullPath(session.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var cases = new List<QueryCase>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddSessionRankCases(
            cases,
            used,
            "beyond-3000-query-cap",
            sessions.OrderByDescending(session => session.UpdatedAt, StringComparer.Ordinal).ToList(),
            new[] { 3200, 4600 },
            OffsetMode.Middle);

        var claudeFiles = EnumerateFiles(ArchiveService.DefaultClaudeSessionsRoot)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        var beyondIndexCap = claudeFiles
            .Skip(4000)
            .Select(file => byPath.GetValueOrDefault(file.FullName))
            .Where(session => session is not null)
            .Cast<ArchiveSession>()
            .ToList();
        AddSessionRankCases(
            cases,
            used,
            "beyond-4000-file-cap",
            beyondIndexCap,
            new[] { 0, Math.Min(500, Math.Max(0, beyondIndexCap.Count - 1)) },
            OffsetMode.Middle);

        var largest = sessions
            .Select(session => (Session: session, Length: SafeLength(session.SourcePath)))
            .OrderByDescending(item => item.Length)
            .FirstOrDefault(item => item.Length > 128L * 1024 * 1024);
        if (largest.Session is not null)
        {
            foreach (var (label, mode) in new[]
            {
                ("start", OffsetMode.Start),
                ("middle", OffsetMode.Middle),
                ("tail", OffsetMode.Tail),
            })
            {
                AddCase(cases, used, "oversized-middle", largest.Session, mode, label);
            }
        }

        var recent = sessions
            .OrderByDescending(session => session.UpdatedAt, StringComparer.Ordinal)
            .ToList();
        AddMatchingCases(
            cases,
            used,
            "web-6kb-tail",
            recent,
            OffsetMode.Tail,
            candidate => candidate.Session.SearchText.Contains(
                candidate.Phrase,
                StringComparison.OrdinalIgnoreCase));

        var live = sessions
            .OrderByDescending(session => SafeLastWrite(session.SourcePath))
            .Take(8)
            .ToList();
        AddSessionRankCases(
            cases,
            used,
            "live-append-tail",
            live,
            new[] { 0, Math.Min(1, Math.Max(0, live.Count - 1)) },
            OffsetMode.Tail);

        AddEncodingCases(cases, used, recent);
        AddProvenanceCases(cases, used, recent);

        var random = sessions
            .OrderBy(session => StableHash(session.SourcePath))
            .ToList();
        AddSessionRankCases(
            cases,
            used,
            "proportional-random",
            random,
            new[] { random.Count / 3, random.Count * 2 / 3 },
            OffsetMode.Fractional);

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["beyond-3000-query-cap"] = CasesPerStratum,
            ["beyond-4000-file-cap"] = CasesPerStratum,
            ["oversized-middle"] = 3,
            ["web-6kb-tail"] = CasesPerStratum,
            ["live-append-tail"] = CasesPerStratum,
            ["encoding-trap"] = CasesPerStratum,
            ["provenance-user-vs-tool"] = CasesPerStratum,
            ["proportional-random"] = CasesPerStratum,
        };
        foreach (var pair in expected)
        {
            var actual = cases.Count(item => item.Stratum == pair.Key);
            if (actual != pair.Value)
                throw new InvalidOperationException(
                    $"Prepared {actual}/{pair.Value} cases for {pair.Key}; refusing an unstratified run.");
        }

        var manifest = new BenchmarkManifest(
            DateTime.UtcNow,
            Environment.MachineName,
            ProductionStorePath(),
            ArchiveService.DefaultCodexSessionsRoot,
            ArchiveService.DefaultClaudeSessionsRoot,
            cases);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, Json));
        Console.WriteLine($"Prepared {cases.Count} real-corpus queries at {manifestPath}");
        foreach (var group in cases.GroupBy(item => item.Stratum))
            Console.WriteLine($"{group.Key}: {group.Count()}");
        return 0;
    }

    private static async Task<int> RunAsync(
        string manifestPath,
        string outputPath,
        string indexPath,
        string? selectedSurface,
        bool legacy)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Run prepare first.", manifestPath);
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(
            await File.ReadAllTextAsync(manifestPath),
            Json) ?? throw new InvalidDataException("Invalid benchmark manifest.");
        ValidateManifest(manifest);
        var indexRequired = !legacy && !string.Equals(
            selectedSurface,
            "rg-literal-oracle",
            StringComparison.OrdinalIgnoreCase);
        var archive = await OpenSnapshotArchiveAsync(
            Path.GetDirectoryName(outputPath)!,
            enableTranscriptSearchIndex: indexRequired,
            indexPath);
        if (indexRequired)
        {
            if (!File.Exists(indexPath))
                throw new FileNotFoundException(
                    "Build the transcript index before running GREEN.",
                    indexPath);
            var sync = await archive.WaitForTranscriptSearchIndexAsync();
            Console.WriteLine(
                $"Index ready: files={sync.IndexedFiles:N0}, rebuilt={sync.RebuiltFiles:N0}, " +
                $"appended={sync.AppendedFiles:N0}, unchanged={sync.UnchangedFiles:N0}, " +
                $"sync={sync.Elapsed.TotalMilliseconds:F1} ms");
        }
        var discovery = new DiscoveryApi(archive, _ => true);
        var searchTool = new ArchiveToolService(archive).Tools()
            .Single(tool => tool.Spec.Name == "search_chats");
        var surfaces = new List<SurfaceRun>();

        bool Include(string name) =>
            string.IsNullOrWhiteSpace(selectedSurface)
            || string.Equals(selectedSurface, name, StringComparison.OrdinalIgnoreCase);

        if (Include("native-deep-content"))
            surfaces.Add(await MeasureAsync(
                "native-deep-content",
                manifest.Cases,
                async item =>
                {
                    var hits = await archive.DeepSearchContentAsync(item.Query, 10);
                    return new SurfaceSearchResult(
                        hits.Select(hit => hit.Session.Id).ToList(),
                        hits.Where(hit => hit.Navigable)
                            .Select(hit => hit.Session.Id)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase),
                        archive.LastSearchCoverage);
                }));
        if (Include("web-discovery"))
            surfaces.Add(await MeasureAsync(
                "web-discovery",
                manifest.Cases,
                item =>
                {
                    var page = discovery.Chats(new DiscoveryQuery(
                        Q: item.Query,
                        Limit: 10,
                        ShowHidden: true));
                    return Task.FromResult(new SurfaceSearchResult(
                        page.Rows.Select(row => row.Id).ToList(),
                        page.Rows.Where(row => row.Navigable)
                            .Select(row => row.Id)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase),
                        page.Coverage));
                }));
        if (Include("agent-search-tool"))
            surfaces.Add(await MeasureAsync(
                "agent-search-tool",
                manifest.Cases,
                async item =>
                {
                    using var args = JsonDocument.Parse(JsonSerializer.Serialize(
                        new { query = item.Query, limit = 10 }));
                    var result = await searchTool.Execute(args.RootElement, CancellationToken.None);
                    using var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(result));
                    var rows = resultJson.RootElement.GetProperty("results")
                        .EnumerateArray()
                        .ToList();
                    var ids = rows
                        .Select(row => row.GetProperty("id").GetString() ?? "")
                        .Where(id => id.Length > 0)
                        .ToList();
                    var navigable = rows
                        .Where(row => row.TryGetProperty("navigable", out var value)
                            && value.ValueKind == JsonValueKind.True)
                        .Select(row => row.GetProperty("id").GetString() ?? "")
                        .Where(id => id.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var coverage = resultJson.RootElement.TryGetProperty(
                        "coverage",
                        out var coverageElement)
                        ? coverageElement.Deserialize<SearchCoverage>(Json)
                        : null;
                    return new SurfaceSearchResult(ids, navigable, coverage);
                }));
        if (Include("rg-literal-oracle"))
            surfaces.Add(await MeasureAsync(
                "rg-literal-oracle",
                manifest.Cases,
                async item =>
                {
                    var ids = await RunRipgrepAsync(item, manifest);
                    return new SurfaceSearchResult(
                        ids,
                        ids.ToHashSet(StringComparer.OrdinalIgnoreCase),
                        null);
                }));

        var result = new BenchmarkResult(
            DateTime.UtcNow,
            manifestPath,
            manifest.Cases.Count,
            archive.Store.Sessions.Count,
            surfaces);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, Json));
        PrintSummary(result);
        Console.WriteLine($"Wrote measured results to {outputPath}");
        return 0;
    }

    private static void ValidateManifest(BenchmarkManifest manifest)
    {
        var appended = 0;
        foreach (var item in manifest.Cases)
        {
            if (!File.Exists(item.SourcePath))
                throw new FileNotFoundException(
                    $"Benchmark target disappeared: {item.Label}",
                    item.SourcePath);
            var length = SafeLength(item.SourcePath);
            if (length < item.SourceLength)
                throw new InvalidDataException(
                    $"Benchmark target shrank: {item.Label} " +
                    $"{item.SourceLength:N0} -> {length:N0} bytes.");
            if (length > item.SourceLength) appended++;
            if (item.ByteOffset < 0 || item.ByteOffset >= length)
                throw new InvalidDataException(
                    $"Benchmark target offset is no longer readable: {item.Label}.");
            _ = OracleLiteral(item);
        }
        Console.WriteLine(
            $"Validated {manifest.Cases.Count} fixed target anchors; " +
            $"{appended} source files grew append-only.");
    }

    private static async Task<SurfaceRun> MeasureAsync(
        string name,
        IReadOnlyList<QueryCase> cases,
        Func<QueryCase, Task<SurfaceSearchResult>> search)
    {
        var observations = new List<QueryObservation>();
        foreach (var pass in new[] { "cold", "warm" })
        {
            foreach (var item in cases)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = await search(item);
                stopwatch.Stop();
                var rank = result.Ids
                    .Select((id, index) => (id, rank: index + 1))
                    .FirstOrDefault(pair => string.Equals(
                        pair.id,
                        item.TargetSessionId,
                        StringComparison.OrdinalIgnoreCase))
                    .rank;
                observations.Add(new QueryObservation(
                    item.Label,
                    item.Stratum,
                    pass,
                    stopwatch.Elapsed.TotalMilliseconds,
                    rank is > 0 and <= 10,
                    rank > 0 ? rank : null,
                    result.Ids.Take(10).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    result.NavigableIds.Contains(item.TargetSessionId),
                    result.Coverage?.Complete,
                    result.Coverage is { Complete: false } coverage
                        ? coverage.Message.Contains(
                            "partial",
                            StringComparison.OrdinalIgnoreCase)
                        : null));
                Console.WriteLine(
                    $"{name} {pass} {item.Label}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms, rank={rank}");
            }
        }
        return new SurfaceRun(name, observations);
    }

    private static async Task<IReadOnlyList<string>> RunRipgrepAsync(
        QueryCase item,
        BenchmarkManifest manifest)
    {
        var start = new ProcessStartInfo
        {
            FileName = "rg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--fixed-strings");
        start.ArgumentList.Add("--files-with-matches");
        start.ArgumentList.Add("--no-messages");
        start.ArgumentList.Add("--glob");
        start.ArgumentList.Add("*.jsonl");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(OracleLiteral(item));
        start.ArgumentList.Add(manifest.CodexRoot);
        start.ArgumentList.Add(manifest.ClaudeRoot);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start rg.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        _ = await errorTask;
        if (process.ExitCode is not 0 and not 1)
            throw new InvalidOperationException($"rg exited {process.ExitCode}.");
        var ids = output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Select(path => manifest.Cases.FirstOrDefault(item =>
                string.Equals(
                    Path.GetFullPath(item.SourcePath),
                    path,
                    StringComparison.OrdinalIgnoreCase))?.TargetSessionId ?? path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var targetIndex = ids.FindIndex(id => string.Equals(
            id,
            item.TargetSessionId,
            StringComparison.OrdinalIgnoreCase));
        if (targetIndex > 0)
        {
            ids.RemoveAt(targetIndex);
            ids.Insert(0, item.TargetSessionId);
        }
        return ids;
    }

    private static string OracleLiteral(QueryCase item)
    {
        using var stream = new FileStream(
            item.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.RandomAccess);
        stream.Position = Math.Clamp(item.ByteOffset, 0, stream.Length);
        using var line = new MemoryStream();
        while (stream.Position < stream.Length && line.Length < 8L * 1024 * 1024)
        {
            var value = stream.ReadByte();
            if (value < 0 || value == '\n') break;
            if (value != '\r') line.WriteByte((byte)value);
        }
        var raw = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        var anchor = Words().Matches(item.Query)
            .Select(match => match.Value)
            .OrderByDescending(word => word.Length)
            .FirstOrDefault(word => raw.Contains(word, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"No raw oracle anchor for {item.Label}.");
        var anchorIndex = raw.IndexOf(anchor, StringComparison.Ordinal);
        var start = Math.Max(0, anchorIndex - 48);
        var length = Math.Min(192, raw.Length - start);
        var literal = raw.Substring(start, length);
        if (literal.Length < anchor.Length)
            throw new InvalidDataException($"Oracle literal too short for {item.Label}.");
        return literal;
    }

    private static void PrintSummary(BenchmarkResult result)
    {
        foreach (var surface in result.Surfaces)
        {
            foreach (var pass in new[] { "cold", "warm" })
            {
                var rows = surface.Observations.Where(row => row.Pass == pass).ToList();
                Console.WriteLine(
                    $"{surface.Name} {pass}: success@10={rows.Count(row => row.SuccessAt10)}/{rows.Count}, " +
                    $"p50={Percentile(rows.Select(row => row.LatencyMs), 0.50):F1} ms, " +
                    $"p95={Percentile(rows.Select(row => row.LatencyMs), 0.95):F1} ms");
            }
        }
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static void AddSessionRankCases(
        List<QueryCase> cases,
        HashSet<string> used,
        string stratum,
        IReadOnlyList<ArchiveSession> sessions,
        IReadOnlyList<int> indexes,
        OffsetMode mode)
    {
        foreach (var index in indexes.Distinct())
        {
            if (sessions.Count == 0) break;
            var selected = Math.Clamp(index, 0, sessions.Count - 1);
            var added = false;
            for (var distance = 0; distance < sessions.Count && !added; distance++)
            {
                foreach (var candidateIndex in new[] { selected + distance, selected - distance })
                {
                    if (candidateIndex < 0 || candidateIndex >= sessions.Count) continue;
                    var candidate = Extract(sessions[candidateIndex], mode);
                    if (candidate is null || !used.Add(candidate.Phrase)) continue;
                    cases.Add(ToCase(
                        stratum,
                        sessions[candidateIndex],
                        candidate,
                        $"rank-{candidateIndex}"));
                    added = true;
                    break;
                }
            }
            if (!added)
                throw new InvalidOperationException(
                    $"Could not extract a distinct query for {stratum} near rank {index}.");
        }
    }

    private static void AddMatchingCases(
        List<QueryCase> cases,
        HashSet<string> used,
        string stratum,
        IEnumerable<ArchiveSession> sessions,
        OffsetMode mode,
        Func<ExtractedCandidate, bool> predicate)
    {
        foreach (var session in sessions)
        {
            if (cases.Count(item => item.Stratum == stratum) >= CasesPerStratum) return;
            var candidate = Extract(session, mode);
            if (candidate is null || used.Contains(candidate.Phrase) || !predicate(candidate)) continue;
            used.Add(candidate.Phrase);
            cases.Add(ToCase(stratum, session, candidate, $"match-{cases.Count(item => item.Stratum == stratum)}"));
        }
    }

    private static void AddCase(
        List<QueryCase> cases,
        HashSet<string> used,
        string stratum,
        ArchiveSession session,
        OffsetMode mode,
        string suffix)
    {
        var candidate = Extract(session, mode);
        if (candidate is null || !used.Add(candidate.Phrase))
            throw new InvalidOperationException($"Could not extract a distinct query for {stratum}/{suffix}.");
        cases.Add(ToCase(stratum, session, candidate, suffix));
    }

    private static QueryCase ToCase(
        string stratum,
        ArchiveSession session,
        ExtractedCandidate candidate,
        string suffix) => new(
            $"{stratum}-{suffix}",
            stratum,
            candidate.Phrase,
            session.Id,
            session.Tool,
            Path.GetFullPath(session.SourcePath),
            candidate.ByteOffset,
            candidate.Role,
            SafeLength(session.SourcePath),
            SafeLastWrite(session.SourcePath));

    private static void AddEncodingCases(
        List<QueryCase> cases,
        HashSet<string> used,
        IReadOnlyList<ArchiveSession> sessions)
    {
        foreach (var session in sessions.Take(400))
        {
            if (cases.Count(item => item.Stratum == "encoding-trap") >= CasesPerStratum) return;
            foreach (var candidate in ExtractCandidates(session, OffsetMode.Fractional, requireEscapes: true))
            {
                if (!used.Add(candidate.Phrase)) continue;
                cases.Add(ToCase(
                    "encoding-trap",
                    session,
                    candidate,
                    $"escaped-{cases.Count(item => item.Stratum == "encoding-trap")}"));
                break;
            }
        }
    }

    private static void AddProvenanceCases(
        List<QueryCase> cases,
        HashSet<string> used,
        IReadOnlyList<ArchiveSession> sessions)
    {
        foreach (var session in sessions.Take(600))
        {
            if (cases.Count(item => item.Stratum == "provenance-user-vs-tool") >= CasesPerStratum) return;
            foreach (var candidate in ExtractCandidates(session, OffsetMode.Fractional)
                         .Where(candidate => candidate.Role == "user"))
            {
                if (!used.Add(candidate.Phrase)) continue;
                cases.Add(ToCase(
                    "provenance-user-vs-tool",
                    session,
                    candidate,
                    $"user-{cases.Count(item => item.Stratum == "provenance-user-vs-tool")}"));
                break;
            }
        }
    }

    private static ExtractedCandidate? Extract(ArchiveSession session, OffsetMode mode) =>
        ExtractCandidates(session, mode).FirstOrDefault();

    private static IEnumerable<ExtractedCandidate> ExtractCandidates(
        ArchiveSession session,
        OffsetMode mode,
        bool requireEscapes = false)
    {
        var path = session.SourcePath;
        var length = SafeLength(path);
        if (length <= 0) yield break;
        var offsets = mode switch
        {
            OffsetMode.Start => new[] { 0L },
            OffsetMode.Middle => new[] { length / 2 },
            OffsetMode.Tail => new[] { Math.Max(0, length - 4L * 1024 * 1024) },
            _ => new[] { length / 3, length * 2 / 3 },
        };
        foreach (var offset in offsets)
        {
            foreach (var line in ReadLinesAt(path, offset, 8L * 1024 * 1024))
            {
                if (requireEscapes
                    && !line.Raw.Contains("\\n", StringComparison.Ordinal)
                    && !line.Raw.Contains("\\\"", StringComparison.Ordinal))
                    continue;
                foreach (var (role, text) in ExtractText(line.Raw, session.Tool))
                {
                    var phrase = DistinctivePhrase(text);
                    if (phrase.Length == 0) continue;
                    yield return new ExtractedCandidate(session, phrase, line.Offset, role);
                }
            }
        }
    }

    private static IEnumerable<RawLine> ReadLinesAt(string path, long requestedOffset, long byteBudget)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.SequentialScan);
        var start = Math.Clamp(requestedOffset, 0, stream.Length);
        stream.Position = start;
        if (start > 0)
        {
            while (stream.Position < stream.Length)
            {
                if (stream.ReadByte() == '\n') break;
            }
        }
        var lineStart = stream.Position;
        var consumed = 0L;
        using var line = new MemoryStream();
        while (stream.Position < stream.Length && consumed < byteBudget)
        {
            var value = stream.ReadByte();
            if (value < 0) break;
            consumed++;
            if (value == '\n')
            {
                if (line.Length > 0 && line.Length <= 32L * 1024 * 1024)
                    yield return new RawLine(
                        lineStart,
                        Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r'));
                line.SetLength(0);
                lineStart = stream.Position;
            }
            else if (line.Length <= 32L * 1024 * 1024)
            {
                line.WriteByte((byte)value);
            }
        }
    }

    private static IEnumerable<(string Role, string Text)> ExtractText(string raw, string tool)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(raw); }
        catch { yield break; }
        using (document)
        {
            var root = document.RootElement;
            if (string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("message", out var message)) yield break;
                var role = message.TryGetProperty("role", out var roleElement)
                    ? roleElement.GetString() ?? root.GetPropertyOrDefault("type")
                    : root.GetPropertyOrDefault("type");
                if (!message.TryGetProperty("content", out var content)) yield break;
                foreach (var item in ContentStrings(content))
                {
                    var itemRole = item.Kind == "tool" ? "tool" : NormalizeRole(role);
                    yield return (itemRole, item.Text);
                }
                yield break;
            }

            if (!root.TryGetProperty("payload", out var payload)) yield break;
            var type = payload.GetPropertyOrDefault("type");
            if (type is "user_message" or "agent_message")
            {
                var role = type == "user_message" ? "user" : "assistant";
                var text = payload.GetPropertyOrDefault("message");
                if (text.Length > 0) yield return (role, text);
                yield break;
            }
            if (type is "function_call_output")
            {
                var text = payload.GetPropertyOrDefault("output");
                if (text.Length > 0) yield return ("tool", text);
                yield break;
            }
            if (type is "message" && payload.TryGetProperty("content", out var responseContent))
            {
                var role = NormalizeRole(payload.GetPropertyOrDefault("role"));
                foreach (var item in ContentStrings(responseContent))
                    yield return (item.Kind == "tool" ? "tool" : role, item.Text);
            }
        }
    }

    private static IEnumerable<(string Kind, string Text)> ContentStrings(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            yield return ("text", content.GetString() ?? "");
            yield break;
        }
        if (content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                yield return ("text", block.GetString() ?? "");
                continue;
            }
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = block.GetPropertyOrDefault("type");
            if (type == "tool_result")
            {
                if (block.TryGetProperty("content", out var nested))
                    foreach (var item in ContentStrings(nested))
                        yield return ("tool", item.Text);
                continue;
            }
            var text = block.GetPropertyOrDefault("text");
            if (text.Length == 0) text = block.GetPropertyOrDefault("output_text");
            if (text.Length > 0) yield return ("text", text);
        }
    }

    private static string NormalizeRole(string role) =>
        role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "user"
        : role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant"
        : "tool";

    private static string DistinctivePhrase(string text)
    {
        var clean = Whitespace().Replace(text, " ").Trim();
        if (clean.Length < 40) return "";
        var words = Words().Matches(clean)
            .Select(match => match.Value)
            .Where(word => word.Length >= 4)
            .ToList();
        if (words.Count < 7) return "";
        var start = Math.Min(words.Count / 3, Math.Max(0, words.Count - 9));
        return string.Join(" ", words.Skip(start).Take(9));
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(
                     root,
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
                     }))
        {
            FileInfo? file = null;
            try { file = new FileInfo(path); }
            catch { }
            if (file is not null) yield return file;
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in value.ToUpperInvariant())
                hash = hash * 31 + ch;
            return hash;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }

    private static string ProductionStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexLocalRetrieval",
        "app-store.json");

    private static string ProductionSearchIndexPath() => Path.Combine(
        Path.GetDirectoryName(ProductionStorePath())!,
        "search-index.sqlite");

    private static async Task<int> BuildIndexAsync(
        string manifestPath,
        string outputPath,
        string indexPath,
        bool fresh)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Run prepare first.", manifestPath);
        if (fresh)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = indexPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }
        else if (File.Exists(indexPath))
        {
            throw new InvalidOperationException(
                $"Index already exists at {indexPath}. Pass --fresh to measure a first build.");
        }

        var outer = Stopwatch.StartNew();
        var archive = await OpenSnapshotArchiveAsync(
            Path.GetDirectoryName(outputPath)!,
            enableTranscriptSearchIndex: true,
            indexPath);
        var buildTask = archive.WaitForTranscriptSearchIndexAsync();
        while (!buildTask.IsCompleted)
        {
            await Task.WhenAny(buildTask, Task.Delay(TimeSpan.FromSeconds(30)));
            if (buildTask.IsCompleted) break;
            var status = archive.TranscriptSearchStatus;
            Console.WriteLine(
                $"Index progress: files={status.IndexedFiles:N0}/{status.TotalFiles:N0}, " +
                $"bytes={status.IndexedBytes:N0}/{status.TotalBytes:N0}, " +
                $"current={status.CurrentFile}");
        }
        var first = await buildTask;
        outer.Stop();

        var incremental = await archive.SyncTranscriptSearchIndexAsync();
        var measurement = new IndexBuildMeasurement(
            DateTime.UtcNow,
            manifestPath,
            indexPath,
            archive.Store.Sessions.Count,
            outer.Elapsed.TotalMilliseconds,
            ToMeasurement(first),
            ToMeasurement(incremental),
            FileBytes(indexPath),
            FileBytes(indexPath + "-wal"),
            FileBytes(indexPath + "-shm"),
            archive.TranscriptSearchStatus);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(measurement, Json));
        Console.WriteLine(
            $"First index: files={first.IndexedFiles:N0}, rebuilt={first.RebuiltFiles:N0}, " +
            $"appended={first.AppendedFiles:N0}, unchanged={first.UnchangedFiles:N0}, " +
            $"indexed={first.IndexedBytes:N0} bytes, sync={first.Elapsed.TotalSeconds:F1} s, " +
            $"initialization={outer.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine(
            $"Immediate sync: rebuilt={incremental.RebuiltFiles:N0}, " +
            $"appended={incremental.AppendedFiles:N0}, unchanged={incremental.UnchangedFiles:N0}, " +
            $"elapsed={incremental.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine(
            $"Index disk: db={measurement.DatabaseBytes:N0}, wal={measurement.WalBytes:N0}, " +
            $"shm={measurement.ShmBytes:N0}, total=" +
            $"{measurement.DatabaseBytes + measurement.WalBytes + measurement.ShmBytes:N0} bytes");
        Console.WriteLine($"Wrote measured index results to {outputPath}");
        return 0;
    }

    private static IndexSyncMeasurement ToMeasurement(TranscriptSearchSyncResult result) => new(
        result.IndexedFiles,
        result.RebuiltFiles,
        result.AppendedFiles,
        result.UnchangedFiles,
        result.IndexedBytes,
        result.Elapsed.TotalMilliseconds);

    private static long FileBytes(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static async Task<int> InspectIndexAsync(
        string manifestPath,
        string outputPath,
        string indexPath)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Run prepare first.", manifestPath);
        if (!File.Exists(indexPath))
            throw new FileNotFoundException("Build the transcript index first.", indexPath);
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(
            await File.ReadAllTextAsync(manifestPath),
            Json) ?? throw new InvalidDataException("Invalid benchmark manifest.");
        var rows = new List<IndexInspection>();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = indexPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        connection.Open();
        foreach (var item in manifest.Cases)
        {
            var inspection = InspectCase(connection, item);
            rows.Add(inspection);
            Console.WriteLine(
                $"{item.Label}: rows={inspection.MatchingRows:N0}, " +
                $"sessions={inspection.MatchingSessions:N0}, " +
                $"targetRows={inspection.TargetRows:N0}, " +
                $"elapsed={inspection.ElapsedMs:F1} ms");
        }
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(rows, Json));
        Console.WriteLine($"Wrote index inspection to {outputPath}");
        return 0;
    }

    private static async Task<int> RepairManifestAsync(
        string manifestPath,
        string outputPath,
        string indexPath)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Run prepare first.", manifestPath);
        if (!File.Exists(indexPath))
            throw new FileNotFoundException("Build the transcript index first.", indexPath);
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(
            await File.ReadAllTextAsync(manifestPath),
            Json) ?? throw new InvalidDataException("Invalid benchmark manifest.");
        var used = manifest.Cases
            .Select(item => item.Query)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repaired = new List<QueryCase>();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = indexPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        connection.Open();

        foreach (var item in manifest.Cases)
        {
            used.Remove(item.Query);
            var original = InspectCase(connection, item);
            var selected = item;
            var selectedInspection = original;
            if (original.MatchingSessions > 1)
            {
                var session = new ArchiveSession
                {
                    Id = item.TargetSessionId,
                    Tool = item.Tool,
                    SourcePath = item.SourcePath,
                    UpdatedAt = item.SourceLastWriteUtc.ToString("O"),
                };
                foreach (var candidate in ExtractCandidates(
                             session,
                             RepairOffsetMode(item)))
                {
                    if (used.Contains(candidate.Phrase)) continue;
                    var proposed = item with
                    {
                        Query = candidate.Phrase,
                        ByteOffset = candidate.ByteOffset,
                        Provenance = candidate.Role,
                        SourceLength = SafeLength(item.SourcePath),
                        SourceLastWriteUtc = SafeLastWrite(item.SourcePath),
                    };
                    var inspection = InspectCase(connection, proposed);
                    if (inspection.TargetRows == 0
                        || inspection.MatchingSessions >= selectedInspection.MatchingSessions)
                        continue;
                    selected = proposed;
                    selectedInspection = inspection;
                    if (inspection.MatchingSessions == 1) break;
                }
            }
            used.Add(selected.Query);
            repaired.Add(selected);
            Console.WriteLine(
                $"{item.Label}: sessions={original.MatchingSessions:N0} -> " +
                $"{selectedInspection.MatchingSessions:N0}, targetRows=" +
                $"{selectedInspection.TargetRows:N0}, changed=" +
                $"{!string.Equals(item.Query, selected.Query, StringComparison.Ordinal)}");
        }

        var result = manifest with
        {
            PreparedAtUtc = DateTime.UtcNow,
            Cases = repaired,
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"Wrote repaired manifest to {outputPath}");
        return 0;
    }

    private static OffsetMode RepairOffsetMode(QueryCase item)
    {
        if (item.Stratum == "oversized-middle")
        {
            if (item.Label.EndsWith("-start", StringComparison.Ordinal)) return OffsetMode.Start;
            if (item.Label.EndsWith("-tail", StringComparison.Ordinal)) return OffsetMode.Tail;
            return OffsetMode.Middle;
        }
        if (item.Stratum is "web-6kb-tail" or "live-append-tail")
            return OffsetMode.Tail;
        return OffsetMode.Fractional;
    }

    private static IndexInspection InspectCase(
        SqliteConnection connection,
        QueryCase item)
    {
        var tokens = IndexTokens(item.Query);
        var ftsQuery = string.Join(
            " AND ",
            tokens.Select(token => "\"" + token.Replace("\"", "\"\"") + "\""));
        var stopwatch = Stopwatch.StartNew();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COUNT(DISTINCT t.session_id),
                SUM(CASE WHEN t.session_id = $target THEN 1 ELSE 0 END)
            FROM turn_fts
            JOIN turns t ON t.id = turn_fts.rowid
            WHERE turn_fts MATCH $query;
            """;
        command.Parameters.AddWithValue("$target", item.TargetSessionId);
        command.Parameters.AddWithValue("$query", ftsQuery);
        using var reader = command.ExecuteReader();
        _ = reader.Read();
        stopwatch.Stop();
        return new IndexInspection(
            item.Label,
            item.Stratum,
            tokens.Count,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private static IReadOnlyList<string> IndexTokens(string query) =>
        IndexTokenRegex().Matches((query ?? "").ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();

    private static async Task<ArchiveService> OpenSnapshotArchiveAsync(
        string artifactRoot,
        bool enableTranscriptSearchIndex = false,
        string? transcriptSearchIndexPath = null)
    {
        Directory.CreateDirectory(artifactRoot);
        var snapshot = Path.Combine(
            artifactRoot,
            $"app-store-snapshot-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        await using (var source = new FileStream(
                         ProductionStorePath(),
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         1024 * 1024,
                         FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         snapshot,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination);
        }
        var archive = new ArchiveService(
            storePath: snapshot,
            transcriptSearchIndexPath: transcriptSearchIndexPath,
            enableTranscriptSearchIndex: enableTranscriptSearchIndex);
        await archive.LoadAsync();
        return archive;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9_.:/\\-]*")]
    private static partial Regex Words();

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex IndexTokenRegex();

    private enum OffsetMode
    {
        Start,
        Middle,
        Tail,
        Fractional,
    }

    private sealed record RawLine(long Offset, string Raw);
    private sealed record ExtractedCandidate(
        ArchiveSession Session,
        string Phrase,
        long ByteOffset,
        string Role);
}

internal static class JsonElementExtensions
{
    public static string GetPropertyOrDefault(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

internal sealed record BenchmarkManifest(
    DateTime PreparedAtUtc,
    string Machine,
    string StorePath,
    string CodexRoot,
    string ClaudeRoot,
    List<QueryCase> Cases);

internal sealed record QueryCase(
    string Label,
    string Stratum,
    string Query,
    string TargetSessionId,
    string Tool,
    string SourcePath,
    long ByteOffset,
    string Provenance,
    long SourceLength,
    DateTime SourceLastWriteUtc);

internal sealed record BenchmarkResult(
    DateTime MeasuredAtUtc,
    string ManifestPath,
    int QueryCount,
    int StoreSessionCount,
    List<SurfaceRun> Surfaces);

internal sealed record SurfaceRun(
    string Name,
    List<QueryObservation> Observations);

internal sealed record SurfaceSearchResult(
    IReadOnlyList<string> Ids,
    IReadOnlySet<string> NavigableIds,
    SearchCoverage? Coverage);

internal sealed record QueryObservation(
    string Label,
    string Stratum,
    string Pass,
    double LatencyMs,
    bool SuccessAt10,
    int? TargetRank,
    int DistinctConversationsTop10,
    bool NavigableTarget,
    bool? CoverageComplete,
    bool? PartialCoverageDisclosed);

internal sealed record IndexBuildMeasurement(
    DateTime MeasuredAtUtc,
    string ManifestPath,
    string IndexPath,
    int StoreSessionCount,
    double InitializationElapsedMs,
    IndexSyncMeasurement FirstSync,
    IndexSyncMeasurement ImmediateIncrementalSync,
    long DatabaseBytes,
    long WalBytes,
    long ShmBytes,
    TranscriptSearchIndexStatus FinalStatus);

internal sealed record IndexSyncMeasurement(
    int IndexedFiles,
    int RebuiltFiles,
    int AppendedFiles,
    int UnchangedFiles,
    long IndexedBytes,
    double ElapsedMs);

internal sealed record IndexInspection(
    string Label,
    string Stratum,
    int QueryTokens,
    long MatchingRows,
    long MatchingSessions,
    long TargetRows,
    double ElapsedMs);
