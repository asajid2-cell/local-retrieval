using System.Text.Json.Serialization;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Core.Models;

public enum ReclaimOperationStatus
{
    Applied,
    Refused,
    Uncertain,
}

public sealed record ReclaimOperationResult(
    ReclaimOperationStatus Status,
    string Detail,
    ReclaimReport? Report = null,
    bool Replay = false)
{
    public bool Ok => Status == ReclaimOperationStatus.Applied;
}

// Versioned payload stored inside ManagementOperation.ResultId. Keeping the reclaim-specific shape here avoids
// extending the shared ManagementOperation model while another lane owns it.
public sealed class ReclaimOperationReceiptPayload
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("canonicalSessionId")] public string CanonicalSessionId { get; set; } = "";
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = new();
    [JsonPropertyName("muxFence")] public ReclaimMuxFence? MuxFence { get; set; }
    [JsonPropertyName("report")] public ReclaimReport? Report { get; set; }
}
