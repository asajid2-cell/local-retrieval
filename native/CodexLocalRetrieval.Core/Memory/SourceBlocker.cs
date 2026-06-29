using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexLocalRetrieval.Core.Memory;

// Turns a full-history transcript into deterministic, source-anchored blocks — the NSA "compressed
// local representation" layer. Determinism is the contract: identical transcript bytes ⇒ identical
// block ids, message ranges, and hashes, every run. No LLM, no randomness, no clock.
public static class SourceBlocker
{
    // Target block size in approx tokens (~4 chars/token). Blocks close when adding the next message
    // would exceed the target, so boundaries fall on message edges and stay stable.
    public const int TargetTokens = 2000;
    public const int HardMaxTokens = 6000;   // a single very long message still gets its own block

    public static string ComputeFileStamp(string sourcePath)
    {
        try
        {
            var fi = new FileInfo(sourcePath);
            return fi.Exists ? $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length}" : "";
        }
        catch { return ""; }
    }

    public static List<SourceBlock> Build(string sessionId, string sourcePath, string tool, IReadOnlyList<FullMessage> messages)
        => Build(sessionId, sourcePath, tool, messages, ComputeFileStamp(sourcePath));

    public static List<SourceBlock> Build(string sessionId, string sourcePath, string tool,
        IReadOnlyList<FullMessage> messages, string fileStamp)
    {
        var blocks = new List<SourceBlock>();
        if (messages is null || messages.Count == 0) return blocks;

        var start = 0;
        var tokens = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var t = ApproxTokens(messages[i].Text);
            // If this single message alone is bigger than the hard max, it's its own block.
            if (i == start)
            {
                tokens = t;
                if (t >= HardMaxTokens)
                {
                    blocks.Add(MakeBlock(sessionId, sourcePath, tool, fileStamp, blocks.Count, messages, start, i));
                    start = i + 1;
                    tokens = 0;
                }
                continue;
            }
            if (tokens + t > TargetTokens)
            {
                blocks.Add(MakeBlock(sessionId, sourcePath, tool, fileStamp, blocks.Count, messages, start, i - 1));
                start = i;
                tokens = t;
            }
            else
            {
                tokens += t;
            }
        }
        if (start < messages.Count)
            blocks.Add(MakeBlock(sessionId, sourcePath, tool, fileStamp, blocks.Count, messages, start, messages.Count - 1));
        return blocks;
    }

    private static SourceBlock MakeBlock(string sessionId, string sourcePath, string tool, string fileStamp,
        int blockIndex, IReadOnlyList<FullMessage> messages, int startIdx, int endIdx)
    {
        var slice = new List<FullMessage>();
        for (var i = startIdx; i <= endIdx; i++) slice.Add(messages[i]);

        // Canonical stream the hash is computed over. role|ts|text per message, newline-joined. The
        // hash binds the block to the EXACT content, so a silently-rewritten transcript won't validate.
        var sb = new StringBuilder();
        foreach (var m in slice) sb.Append(m.Role).Append('|').Append(m.Timestamp).Append('|').Append(m.Text).Append('\n');
        var hash = Sha256(sb.ToString());

        var roles = slice.Select(m => m.Role).Distinct().ToList();
        var excerpt = SecretRedactor.Clean(BuildExcerpt(slice));
        var approx = slice.Sum(m => ApproxTokens(m.Text));

        return new SourceBlock
        {
            Id = $"chat_{sessionId}:block_{blockIndex}",
            SessionId = sessionId,
            SourcePath = sourcePath,
            Tool = tool,
            Index = blockIndex,
            MsgStartIndex = startIdx,
            MsgEndIndex = endIdx,
            TsStart = slice[0].Timestamp,
            TsEnd = slice[^1].Timestamp,
            MessageCount = slice.Count,
            ApproxTokens = approx,
            SourceHash = hash,
            QuoteHash = hash,
            FileStamp = fileStamp,
            Excerpt = excerpt,
            Roles = roles,
        };
    }

    // A compact human/agent-readable preview: first non-tool user + assistant lines, capped.
    private static string BuildExcerpt(IReadOnlyList<FullMessage> slice)
    {
        var sb = new StringBuilder();
        foreach (var m in slice)
        {
            if (m.IsTool) continue;
            var line = m.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (line.Length == 0) continue;
            if (line.Length > 200) line = line[..200] + "…";
            sb.Append(m.Role).Append(": ").Append(line).Append('\n');
            if (sb.Length > 600) break;
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static int ApproxTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
