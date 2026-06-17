using System.Text.RegularExpressions;

namespace CodexLocalRetrieval.Core.Chat;

// Scrubs high-confidence secret shapes out of any archive text BEFORE it leaves the machine for the
// model API. The co-pilot ships chat snippets to a third-party model, so a key pasted into an old
// chat would otherwise be exfiltrated on the user's behalf. Conservative by design: only well-known
// credential formats are masked (prefixed API keys, cloud key ids, bearer tokens, PEM private-key
// blocks, and explicit key=value secrets), so ordinary prose is left intact and the model stays
// useful. This is defence-in-depth, not a guarantee — novel secret formats can still slip through.
public static class SecretRedactor
{
    // ASCII so it survives JSON serialization to the model verbatim (no « escaping noise).
    public const string Mask = "[redacted-secret]";

    // (pattern, replacement). Most fully mask the match; the key=value rule keeps the field NAME so
    // the model still sees "there was an api_key here", just not its value.
    private static readonly (Regex Re, string Replacement)[] Rules =
    {
        // OpenAI / Anthropic / DeepSeek-style keys: sk-..., sk-ant-...
        (new Regex(@"\bsk-(?:ant-)?[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), Mask),
        // Stripe (and similar) keys use underscores: sk_live_, sk_test_, rk_live_, pk_live_
        (new Regex(@"\b[a-z]{2}_(?:live|test)_[A-Za-z0-9]{16,}", RegexOptions.Compiled), Mask),
        // JWTs: three base64url segments; eyJ is base64 of '{"' so this is high-confidence
        (new Regex(@"\beyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}", RegexOptions.Compiled), Mask),
        // GitHub tokens (classic PAT / OAuth / app) and fine-grained PATs
        (new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}", RegexOptions.Compiled), Mask),
        (new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled), Mask),
        // AWS access key id
        (new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled), Mask),
        // Google API key (real ones are AIza + 35 chars; allow some slack rather than an exact count)
        (new Regex(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled), Mask),
        // Slack tokens
        (new Regex(@"\bxox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled), Mask),
        // npm automation/access tokens
        (new Regex(@"\bnpm_[A-Za-z0-9]{30,}", RegexOptions.Compiled), Mask),
        // Password embedded in a URL/connection string: scheme://user:PASSWORD@host (mask just the secret)
        (new Regex(@"(://[^/\s:@]+:)[^/\s:@]{3,}(@)", RegexOptions.Compiled), "$1" + Mask + "$2"),
        // OpenAI project/service keys with the newer sk-proj prefix are already caught by the sk- rule.
        // Bearer tokens in headers / log lines
        (new Regex(@"\bBearer\s+[A-Za-z0-9._\-]{20,}", RegexOptions.Compiled), "Bearer " + Mask),
        // PEM private-key blocks (any line breaks inside)
        (new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----",
            RegexOptions.Compiled), Mask),
        // Explicit secret assignments: api_key = "...", password: p@ss..., aws_secret_access_key=...
        // The field name is matched as a snake/kebab identifier whose *segment* is a secret keyword
        // (so aws_secret_access_key is caught, but "secretary" is not). Field name + separator are kept
        // so the model still has context. Value is either a QUOTED run (which may contain spaces) or a
        // bare run of 8+ non-space chars (covers password symbols).
        (new Regex(@"(?i)(?<![A-Za-z0-9_\-])((?:[A-Za-z0-9]+[_\-])*(?:api[_-]?key|secret|password|passwd|access[_-]?token|auth[_-]?token|client[_-]?secret)(?:[_\-][A-Za-z0-9]+)*)(\s*[=:]\s*)(?:""[^""\r\n]{4,}""|'[^'\r\n]{4,}'|[""']?\S{8,})",
            RegexOptions.Compiled), "$1$2" + Mask),
    };

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var (re, replacement) in Rules) text = re.Replace(text, replacement);
        return text;
    }
}
