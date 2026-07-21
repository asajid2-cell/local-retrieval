using System.Text.Json;

namespace CodexLocalRetrieval.Core.Remote;

public enum GateOutcome { Allow, Login, Forbid }

// Optional gate that defers authentication to an hl-auth SSO instance (the harmonizerlabs.cc account
// system) instead of the built-in bearer token. It reads the visitor's hl_session cookie, asks
// hl-auth's PUBLIC, cookie-based /auth/api/access endpoint what that session can open, and decides
// allow / send-to-login / forbid. The HTTP call + cookie read live in the host (Program.cs); the
// DECISION is here so it can be unit-tested. Fails CLOSED: no cookie -> login; auth service
// unreachable or malformed -> forbid.
public sealed class HlAuthGate
{
    private readonly Func<string, CancellationToken, Task<string?>> _fetchAccess; // cookie value -> access JSON, null on failure
    private readonly string? _requiredPage;

    public HlAuthGate(Func<string, CancellationToken, Task<string?>> fetchAccess, string? requiredPage)
    {
        _fetchAccess = fetchAccess;
        _requiredPage = string.IsNullOrWhiteSpace(requiredPage) ? null : requiredPage.Trim();
    }

    public async Task<GateOutcome> CheckAsync(string? sessionCookie, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionCookie)) return GateOutcome.Login; // not signed in
        string? json;
        try { json = await _fetchAccess(sessionCookie, ct); }
        catch { json = null; }
        if (json is null) return GateOutcome.Forbid; // auth service unreachable -> deny
        return Evaluate(json, _requiredPage);
    }

    // Pure decision over hl-auth's /auth/api/access response:
    //   { "authenticated": bool, "pages": [ { "id","path_prefix","access","canOpen" }, ... ] }
    public static GateOutcome Evaluate(string accessJson, string? requiredPage)
    {
        try
        {
            using var doc = JsonDocument.Parse(accessJson);
            var root = doc.RootElement;
            var authed = root.TryGetProperty("authenticated", out var a) && a.ValueKind == JsonValueKind.True;
            if (!authed) return GateOutcome.Login;
            if (string.IsNullOrWhiteSpace(requiredPage)) return GateOutcome.Allow; // any signed-in session is enough

            if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pages.EnumerateArray())
                {
                    if (p.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        && string.Equals(id.GetString(), requiredPage, StringComparison.OrdinalIgnoreCase))
                    {
                        return p.TryGetProperty("canOpen", out var c) && c.ValueKind == JsonValueKind.True
                            ? GateOutcome.Allow : GateOutcome.Forbid;
                    }
                }
            }
            return GateOutcome.Forbid; // page not registered / not openable by this account
        }
        catch { return GateOutcome.Forbid; } // malformed -> fail closed
    }
}
