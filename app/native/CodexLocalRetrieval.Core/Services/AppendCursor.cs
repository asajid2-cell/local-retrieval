using System.Text;

namespace CodexLocalRetrieval.Core.Services;

/// Where an append-only reader has got to. <see cref="Offset"/> is the authority — it is a BYTE
/// position, so reading "what's new" costs the size of the append rather than the size of the file.
/// <see cref="Line"/> rides along purely so acks can keep quoting absolute line numbers.
public readonly record struct AppendCursorState(long Offset, long Line)
{
    public static readonly AppendCursorState Zero = new(0, 0);
}

/// Complete lines found past the cursor. <see cref="Lines"/> never contains a half-written tail: a
/// writer that is mid-append leaves its partial line for the next read.
public sealed record AppendReadResult(long NextOffset, long NextLine, IReadOnlyList<string> Lines, long BytesRead, bool Restarted);

/// Byte-offset cursor over an append-only text file (the agent inbox), with a one-time migration from
/// the original line-count cursor.
///
/// The old format was a bare integer = "number of lines already processed", which forced a full
/// File.ReadAllText of an unbounded file on every poll just to count newlines. The new format is
/// `offset:&lt;bytes&gt; line:&lt;n&gt;`. Bare-integer content is therefore unambiguously v1, and migrating it
/// means finding the byte just past the Nth newline — so the N lines already acked are not replayed
/// and the lines after them are not skipped.
public static class AppendCursor
{
    private const string OffsetKey = "offset:";
    private const string LineKey = "line:";

    /// Default read ceiling per call. A backlog larger than this is drained across successive polls
    /// rather than in one allocation.
    public const int DefaultMaxBytes = 1 << 20;

    /// Read the cursor, migrating a v1 line-count in place. <paramref name="dataPath"/> is only touched
    /// when a migration is actually needed.
    public static AppendCursorState Load(string cursorPath, string dataPath)
    {
        string text;
        try
        {
            if (!File.Exists(cursorPath)) return AppendCursorState.Zero;
            text = File.ReadAllText(cursorPath).Trim();
        }
        catch { return AppendCursorState.Zero; }
        if (text.Length == 0) return AppendCursorState.Zero;

        if (text.StartsWith(OffsetKey, StringComparison.Ordinal))
        {
            long offset = 0, line = 0;
            foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith(OffsetKey, StringComparison.Ordinal)) long.TryParse(token[OffsetKey.Length..], out offset);
                else if (token.StartsWith(LineKey, StringComparison.Ordinal)) long.TryParse(token[LineKey.Length..], out line);
            }
            return new AppendCursorState(Math.Max(0, offset), Math.Max(0, line));
        }

        // v1: a bare line count. Translate once, persist in the new format, never look back.
        if (!long.TryParse(text, out var processedLines) || processedLines < 0) return AppendCursorState.Zero;
        var migrated = MigrateFromLineCount(dataPath, processedLines);
        Save(cursorPath, migrated);
        return migrated;
    }

    public static void Save(string cursorPath, AppendCursorState state)
    {
        try { File.WriteAllText(cursorPath, OffsetKey + state.Offset.ToString() + " " + LineKey + state.Line.ToString()); }
        catch { }
    }

    /// Byte position just past the <paramref name="processedLines"/>th newline. If the file holds fewer
    /// complete lines than that (truncated/reset between runs), stop at the last complete line — the
    /// remaining lines are then genuinely new and get processed exactly once.
    public static AppendCursorState MigrateFromLineCount(string dataPath, long processedLines)
    {
        if (processedLines <= 0) return AppendCursorState.Zero;
        try
        {
            using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long position = 0, seen = 0, lastComplete = 0;
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    seen++;
                    lastComplete = position + i + 1;
                    if (seen == processedLines) return new AppendCursorState(lastComplete, seen);
                }
                position += read;
            }
            return new AppendCursorState(lastComplete, seen);
        }
        catch { return AppendCursorState.Zero; }
    }

    /// Read only the bytes appended past <paramref name="offset"/>. Returns complete lines and the
    /// offset to persist. If the file is now SHORTER than the cursor it was truncated or replaced, so
    /// the read restarts from zero and <c>Restarted</c> says so (the caller must also reset its line
    /// counter).
    public static AppendReadResult ReadNewLines(string dataPath, AppendCursorState cursor, int maxBytes = DefaultMaxBytes)
    {
        var empty = Array.Empty<string>();
        if (!File.Exists(dataPath)) return new AppendReadResult(cursor.Offset, cursor.Line, empty, 0, false);

        using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

        var offset = cursor.Offset;
        var line = cursor.Line;
        var restarted = false;
        if (offset > fs.Length) { offset = 0; line = 0; restarted = true; }

        var available = fs.Length - offset;
        if (available <= 0) return new AppendReadResult(offset, line, empty, 0, restarted);

        var want = (int)Math.Min(available, maxBytes);
        var buffer = new byte[want];
        fs.Seek(offset, SeekOrigin.Begin);
        var got = 0;
        while (got < want)
        {
            var n = fs.Read(buffer, got, want - got);
            if (n <= 0) break;
            got += n;
        }
        PerfCounters.InboxBytesRead(got);

        // Cut at the last newline. 0x0A never occurs inside a multi-byte UTF-8 sequence, so this can
        // never split a character, and it is what keeps a half-written tail out of the result.
        var lastNewline = -1;
        for (var i = got - 1; i >= 0; i--) { if (buffer[i] == (byte)'\n') { lastNewline = i; break; } }
        if (lastNewline < 0) return new AppendReadResult(offset, line, empty, got, restarted);

        var consumed = lastNewline + 1;
        var start = 0;
        if (offset == 0 && consumed >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) start = 3;
        var text = new UTF8Encoding(false).GetString(buffer, start, consumed - start);

        var lines = new List<string>();
        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i > from && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[from..end]);
            from = i + 1;
        }
        return new AppendReadResult(offset + consumed, line + lines.Count, lines, got, restarted);
    }
}
