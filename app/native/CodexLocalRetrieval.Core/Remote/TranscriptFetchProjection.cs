using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Remote;

// Projects ONE archived chat into the paged wire form the relay stores under
// POST /api/transcripts/:sessionId. This is an archive.read: the web asked for one explicit opaque
// session id and gets exactly that chat's clean transcript back — never a bulk mirror of history.
//
// Everything here is pure so it can be unit-tested without the WinUI shell (the test project
// references Core, not Native). MainPage.Remote.cs is only the wire: lease -> project -> push -> ack.
//
// Page contract (pinned IDENTICALLY on the relay side):
//   { "schemaVersion":1, "sessionId":"<opaque>", "page":<1-based>, "pages":<total>,
//     "messages":[ { "role":"user"|"assistant", "text":"...", "ts":"..." }, ... ] }
// Page 1 carries the NEWEST slice; messages stay chronological INSIDE a page so a page reads
// forwards. Oldest overflow past MaxPages is dropped, not truncated mid-page.
public static class TranscriptFetchProjection
{
    public const int SchemaVersion = 1;

    // Hard wire limits. A page must fit a relay body comfortably; 32 pages of 200KB is already an
    // enormous chat, and the cap is what stops one fetch from becoming a full-history mirror.
    public const int MaxPageBytes = 200 * 1024;
    public const int MaxPages = 32;

    // Opaque ids only (PLAN items 5-6): the id goes into a URL path that is built into a shell
    // command line on the far side of ssh, and the credential goes into an HTTP header. Anything
    // outside this alphabet — a quote, a space, a slash, a CR/LF — is refused rather than escaped.
    private static readonly Regex OpaqueId = new(@"^[A-Za-z0-9._-]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex Credential = new(@"^[A-Za-z0-9._~+/=-]{8,512}$", RegexOptions.Compiled);

    public static bool IsOpaqueId(string? id) => !string.IsNullOrEmpty(id) && OpaqueId.IsMatch(id);

    public static bool IsCredential(string? token) => !string.IsNullOrEmpty(token) && Credential.IsMatch(token);

    // Redaction honours app/REMOTE.md: direct reads ship raw over the user's own authenticated
    // channel unless CLR_REMOTE_REDACT_READS=1, which scrubs secret shapes out of message bodies.
    public static bool RedactReadsEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("CLR_REMOTE_REDACT_READS"), "1", StringComparison.Ordinal);

    // ExtractReaderMessagesAsync filters to ONE role per call, so a whole chat is two reads merged.
    // Both lists arrive in file order; OrderBy is a STABLE sort in LINQ, so equal/blank timestamps
    // keep their per-role file order instead of shuffling. ISO-8601 sorts correctly as text.
    public static List<ArchiveMessage> MergeChronological(
        IEnumerable<ArchiveMessage>? user,
        IEnumerable<ArchiveMessage>? assistant)
        => (user ?? Array.Empty<ArchiveMessage>())
            .Concat(assistant ?? Array.Empty<ArchiveMessage>())
            .OrderBy(m => m.Timestamp ?? "", StringComparer.Ordinal)
            .ToList();

    public sealed record TranscriptPage(int Page, int Pages, int MessageCount, string Json);

    // Pack newest-first into <=MaxPageBytes pages, at most MaxPages of them.
    public static IReadOnlyList<TranscriptPage> BuildPages(
        string sessionId,
        IReadOnlyList<ArchiveMessage>? messages,
        bool redact)
    {
        var clean = new List<WireMessage>();
        foreach (var m in messages ?? (IReadOnlyList<ArchiveMessage>)Array.Empty<ArchiveMessage>())
        {
            var text = m?.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (redact) text = SecretRedactor.Scrub(text);
            clean.Add(new WireMessage(NormalizeRole(m!.Role), text, m.Timestamp ?? ""));
        }

        // Walk oldest->newest is wrong for a newest-first page 1: fill from the END backwards, so the
        // last page we fill is the oldest and it is the one that gets dropped at the cap.
        var slices = new List<List<WireMessage>>();
        var index = clean.Count - 1;
        while (index >= 0 && slices.Count < MaxPages)
        {
            var slice = new List<WireMessage>();
            var budget = MaxPageBytes - EnvelopeOverhead(sessionId);
            while (index >= 0)
            {
                var candidate = Fit(clean[index], budget, slice.Count == 0);
                if (candidate is null) break;                  // no room left in this page
                budget -= MessageBytes(candidate) + 1;         // +1 for the array comma
                slice.Add(candidate);
                index--;
            }
            if (slice.Count == 0) break;                       // defensive: never spin on a message we can't fit
            slice.Reverse();                                   // chronological inside the page
            slices.Add(slice);
        }

        var pages = new List<TranscriptPage>(slices.Count);
        for (var i = 0; i < slices.Count; i++)
        {
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = SchemaVersion,
                sessionId,
                page = i + 1,
                pages = slices.Count,
                messages = slices[i],
            });
            pages.Add(new TranscriptPage(i + 1, slices.Count, slices[i].Count, json));
        }
        return pages;
    }

    // The curl the app runs over its owner-only ssh. The page rides stdin so no transcript byte ever
    // touches a shell command line. The bridge credential is scoped and short-lived — it is minted by
    // the relay when it queues THIS command, so the app holds no standing authority to write history.
    public static string PushCommand(int port, string sessionId, string bridgeToken)
    {
        if (!IsOpaqueId(sessionId)) throw new ArgumentException("session id is not opaque", nameof(sessionId));
        if (!IsCredential(bridgeToken)) throw new ArgumentException("bridge credential is not well formed", nameof(bridgeToken));
        return $"curl -s -X POST http://127.0.0.1:{port}/api/transcripts/{sessionId} "
             + $"-H 'Content-Type: application/json' -H 'Authorization: Bearer {bridgeToken}' --data-binary @-";
    }

    private sealed record WireMessage(
        [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
        [property: System.Text.Json.Serialization.JsonPropertyName("ts")] string Ts);

    private static string NormalizeRole(string? role)
        => string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";

    // Serialized cost of one message inside the array, measured on the real encoder so escaping
    // (newlines, quotes, non-ASCII) is counted rather than estimated.
    private static int MessageBytes(WireMessage m)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(m));

    private static int EnvelopeOverhead(string sessionId)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            sessionId,
            page = MaxPages,
            pages = MaxPages,
            messages = Array.Empty<WireMessage>(),
        })) + 16;   // slack for the growing digits/brackets

    // Fit a message into the remaining budget. A message that is simply too big for an EMPTY page
    // gets its text truncated (with a visible marker) so a page can always close — otherwise one
    // 5MB paste would stall the walk forever.
    private static WireMessage? Fit(WireMessage m, int budget, bool pageIsEmpty)
    {
        if (MessageBytes(m) + 1 <= budget) return m;
        if (!pageIsEmpty) return null;

        const string Marker = "\n[...truncated for transfer]";
        var text = m.Text;
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var probe = m with { Text = text[..CutAt(text, mid)] + Marker };
            if (MessageBytes(probe) + 1 <= budget) low = mid; else high = mid - 1;
        }
        return m with { Text = text[..CutAt(text, low)] + Marker };
    }

    // Never split a surrogate pair — a lone half would be re-encoded as U+FFFD, silently corrupting
    // the last emoji/CJK character of a truncated message.
    private static int CutAt(string text, int index)
        => index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) ? index - 1 : index;
    }
}
