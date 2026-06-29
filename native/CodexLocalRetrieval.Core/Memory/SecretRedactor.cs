using System.Text.RegularExpressions;

namespace CodexLocalRetrieval.Core.Memory;

// Transcripts can contain live secrets (the user has had a key burned this way). Anything that gets
// written into a card excerpt, a source-cache block, or the vault is redacted FIRST. The raw
// transcript itself is never touched and never committed — this only guards what leaves the archive.
public static class SecretRedactor
{
    private static readonly (Regex rx, string label)[] Patterns =
    {
        // OpenAI / DeepSeek style: sk-... and sk-proj-...
        (new Regex(@"\bsk-[A-Za-z0-9_\-]{12,}\b", RegexOptions.Compiled), "[redacted-key]"),
        // Anthropic
        (new Regex(@"\bsk-ant-[A-Za-z0-9_\-]{12,}\b", RegexOptions.Compiled), "[redacted-key]"),
        // AWS access key id
        (new Regex(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.Compiled), "[redacted-aws-key]"),
        // GitHub tokens
        (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled), "[redacted-token]"),
        // Bearer header values
        (new Regex(@"(?i)\bBearer\s+[A-Za-z0-9._\-]{12,}", RegexOptions.Compiled), "Bearer [redacted-token]"),
        // Generic xxx_api_key = "...." / api-key: ....
        (new Regex(@"(?i)(api[_\-]?key|secret|password|token)\s*[:=]\s*[""']?[A-Za-z0-9._\-]{12,}[""']?", RegexOptions.Compiled), "$1=[redacted]"),
        // JWTs
        (new Regex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\b", RegexOptions.Compiled), "[redacted-jwt]"),
    };

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;
        foreach (var (rx, label) in Patterns) s = rx.Replace(s, label);
        return s;
    }

    public static bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var (rx, _) in Patterns) if (rx.IsMatch(text)) return true;
        return false;
    }

    // Drop lone/orphaned surrogate halves. The app caps message text at a fixed char count, which can
    // split an emoji's surrogate PAIR — leaving an invalid lone surrogate that throws when the text is
    // later serialized (JSON patch / git commit). Removing it keeps stored text valid UTF-16/UTF-8.
    public static string StripLoneSurrogates(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c); sb.Append(text[i + 1]); i++; }
                // else: lone high surrogate -> drop
            }
            else if (char.IsLowSurrogate(c)) { /* lone low surrogate -> drop */ }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // Convenience: redact secrets AND strip invalid surrogates in one pass for anything stored.
    public static string Clean(string? text) => StripLoneSurrogates(Redact(text));
}
