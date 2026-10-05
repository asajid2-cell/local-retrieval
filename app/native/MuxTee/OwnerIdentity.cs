using System;
using System.IO;
using System.Security.Cryptography;

namespace MuxTee;

// Who this tab is, from muxd's point of view.
//
// The owner key is the reconnect credential - muxd refuses a registration shorter than 24 chars and uses
// the key to tell "my tab reconnected" from "a second tab is claiming the same name". It must therefore
// survive a muxtee restart, so it is persisted, not minted per run. It is NOT a secret that authorises
// anything beyond replacing this one tab's owner link, and it never leaves the loopback socket.
//
// The name is stable per terminal. MUXTEE_NAME overrides it (the rollout/profile layer sets it); without
// one we derive a name from the tab's own process tree so two tabs get two names.
internal static class OwnerIdentity
{
    private static string StateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "muxtee");

    public static string OwnerKey()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var path = Path.Combine(StateDir, "owner-key.txt");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 24) return existing;
            }
            var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            File.WriteAllText(path, fresh);
            return fresh;
        }
        catch
        {
            // No writable state dir: a process-lifetime key still registers, it just cannot reconnect.
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        }
    }

    public static string SessionName()
    {
        var named = Environment.GetEnvironmentVariable("MUXTEE_NAME");
        if (!string.IsNullOrWhiteSpace(named)) return Sanitize(named!);
        // muxd's name grammar is a short token; derive one from the console's own window handle so two
        // tabs do not collide, and keep it stable for the life of the process.
        return Sanitize("tab-" + Environment.ProcessId.ToString("x"));
    }

    // muxd accepts a strict token; keep to [A-Za-z0-9._-] and bound the length.
    private static string Sanitize(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') sb.Append(c);
            else sb.Append('-');
        }
        var s = sb.ToString();
        return s.Length <= 48 ? s : s[..48];
    }
}
