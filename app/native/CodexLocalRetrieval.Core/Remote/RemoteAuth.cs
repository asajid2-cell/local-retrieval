using System.Security.Cryptography;
using System.Text;

namespace CodexLocalRetrieval.Core.Remote;

// Bearer-token gate for the remote API. The token is a shared secret from the environment
// (CLR_REMOTE_TOKEN); the server fails closed if it isn't set. Comparison is constant-time over
// SHA-256 digests so neither the value nor the length leaks through timing.
public static class RemoteAuth
{
    public const int MinTokenLength = 24;

    public static bool IsValidConfiguredToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Trim().Length >= MinTokenLength;

    // True only if the presented token matches the expected one. Empty/missing => false.
    public static bool Matches(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
        Span<byte> p = stackalloc byte[32];
        Span<byte> e = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), p);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), e);
        return CryptographicOperations.FixedTimeEquals(p, e);
    }

    // Pull the token from an Authorization: Bearer header, or an X-Auth-Token header. (No query-string
    // fallback — tokens in URLs end up in proxy/access logs.)
    public static string? Extract(string? authorizationHeader, string? xAuthTokenHeader)
    {
        if (!string.IsNullOrWhiteSpace(authorizationHeader))
        {
            var h = authorizationHeader.Trim();
            const string prefix = "Bearer ";
            if (h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return h[prefix.Length..].Trim();
        }
        return string.IsNullOrWhiteSpace(xAuthTokenHeader) ? null : xAuthTokenHeader.Trim();
    }
}
