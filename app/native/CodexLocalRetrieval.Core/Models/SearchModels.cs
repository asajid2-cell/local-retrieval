namespace CodexLocalRetrieval.Core.Models;

public sealed record SearchCoverage(
    bool Complete,
    int IndexedFiles,
    int TotalFiles,
    long IndexedBytes,
    long TotalBytes,
    bool ExhaustiveFallbackComplete,
    string Message);

public sealed record UnifiedSearchResult(
    IReadOnlyList<ArchiveSearchHit> Hits,
    SearchCoverage Coverage);

public sealed record TranscriptSearchIndexStatus(
    bool Building,
    bool Complete,
    int IndexedFiles,
    int TotalFiles,
    long IndexedBytes,
    long TotalBytes,
    string CurrentFile,
    string LastError,
    DateTimeOffset UpdatedAt);

public sealed record TranscriptSearchSyncResult(
    int IndexedFiles,
    int RebuiltFiles,
    int AppendedFiles,
    int UnchangedFiles,
    long IndexedBytes,
    TimeSpan Elapsed);
