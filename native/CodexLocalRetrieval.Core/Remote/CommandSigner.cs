using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CodexLocalRetrieval.Core.Remote;

// Authorizes "auto" (no-approval) agent commands by proving they came from the owner, not an injector.
// The owner holds a pre-shared signing key (never sent over the wire — only HMACs are). Every auto
// command carries {ts, nonce, sig=HMAC-SHA256(key, canonical)}; the server verifies the MAC, that the
// timestamp is fresh (±2 min), and that the nonce hasn't been used (replay). Tamper any field and the
// MAC breaks; replay a captured command and the nonce is already burned. This makes no-approval
// execution contingent on the owner's secret rather than mere reachability of the socket — the same
// intent you give by typing into the local CLI, but injection-proof end to end (incl. the LAN leg).
public sealed class CommandSigner
{
    private const long WindowMs = 120_000;
    private readonly byte[]? _key;
    private readonly ConcurrentDictionary<string, long> _seen = new(); // nonce -> expiry (unix ms)

    public bool Enabled => _key is not null;

    public CommandSigner(string? key) =>
        _key = string.IsNullOrWhiteSpace(key) || key.Trim().Length < 24 ? null : Encoding.UTF8.GetBytes(key.Trim());

    // The exact string both client (JS) and server hash. Built from fields (never parsed), so newlines
    // inside `text` are harmless. Order/format MUST match the browser's signCommand().
    public static string Canonical(string op, string source, string threadId, string mode, long tsMs, string nonce, string text) =>
        $"clrsig-v1\n{op}\n{source}\n{threadId}\n{mode}\n{tsMs}\n{nonce}\n{text}";

    public bool Verify(string canonical, string nonce, long tsMs, string sigHex, long nowMs, out string reason)
    {
        reason = "";
        if (_key is null) { reason = "signing not configured"; return false; }
        if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(sigHex)) { reason = "missing signature"; return false; }
        if (Math.Abs(nowMs - tsMs) > WindowMs) { reason = "stale or future timestamp"; return false; }

        using var h = new HMACSHA256(_key);
        var expect = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var given = sigHex.Trim().ToLowerInvariant();
        if (expect.Length != given.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expect), Encoding.ASCII.GetBytes(given)))
        { reason = "bad signature"; return false; }

        // Only burn the nonce AFTER the MAC checks out, so junk can't flush the replay cache.
        Evict(nowMs);
        if (!_seen.TryAdd(nonce, nowMs + WindowMs)) { reason = "replayed command"; return false; }
        return true;
    }

    private void Evict(long nowMs)
    {
        foreach (var kv in _seen) if (kv.Value < nowMs) _seen.TryRemove(kv.Key, out _);
    }

    // Key source: explicit env value, else a persisted per-machine key, else freshly generated + saved.
    // The key is printed once via `log` so the owner can read it OFF THE PC (out-of-band) and enter it in
    // the browser — it is never delivered over the network.
    public static CommandSigner LoadOrCreate(string? envKey, string keyFilePath, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(envKey) && envKey.Trim().Length >= 24)
            return new CommandSigner(envKey);
        try
        {
            if (File.Exists(keyFilePath))
            {
                var existing = File.ReadAllText(keyFilePath).Trim();
                if (existing.Length >= 24) return new CommandSigner(existing);
            }
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); // 48 hex chars
            Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
            File.WriteAllText(keyFilePath, key);
            log($"owner signing key (enter this in the browser to enable auto mode): {key}");
            log($"  (saved to {keyFilePath}; set CLR_REMOTE_SIGNING_KEY to override)");
            return new CommandSigner(key);
        }
        catch (Exception ex)
        {
            log("could not establish a signing key, auto mode disabled: " + ex.Message);
            return new CommandSigner(null);
        }
    }
}
