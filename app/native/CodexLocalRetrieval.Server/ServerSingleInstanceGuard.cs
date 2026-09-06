using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CodexLocalRetrieval.Server;

public sealed record ServerInstanceIdentity(string Scope, string Value)
{
    public string HealthPath => "/healthz?instance=" + Uri.EscapeDataString(Value);
}

public sealed record ServerSingleInstanceResult(
    bool Acquired,
    bool AlreadyRunning,
    bool ExistingHealthy,
    string Message,
    IDisposable? Lease);

// Owns one server per bind/port/profile. The profile is the configured archive store, not a
// process-global flag, so isolated stores may run concurrently on different ports.
public static class ServerSingleInstanceGuard
{
    private const string MutexPrefix = "Local\\CodexLocalRetrieval.Server.";

    public static ServerInstanceIdentity Identify(string bind, int port, string? storePath, bool bundled)
    {
        var normalizedBind = (bind ?? "").Trim().ToLowerInvariant();
        var normalizedStore = bundled
            ? "bundled"
            : string.IsNullOrWhiteSpace(storePath)
                ? "default"
                : Path.GetFullPath(storePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var scope = $"{normalizedBind}:{port}|{normalizedStore}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant()[..32];
        return new ServerInstanceIdentity(scope, hash);
    }

    public static ServerSingleInstanceResult TryAcquire(
        string bind,
        int port,
        string? storePath,
        bool bundled,
        Func<Uri, string, bool>? isHealthy = null,
        Func<string, Mutex>? mutexFactory = null)
    {
        var identity = Identify(bind, port, storePath, bundled);
        Mutex mutex;
        bool acquired;
        if (mutexFactory is null)
        {
            // A newly-created named mutex has no other server handle. Existing means another
            // process owns or is concurrently initializing this instance; do not WaitOne here,
            // because named mutexes are re-entrant within one process and would hide duplicate
            // starts from same-process fixtures.
            mutex = new Mutex(false, MutexPrefix + identity.Value, out var createdNew);
            acquired = createdNew;
        }
        else
        {
            mutex = mutexFactory(MutexPrefix + identity.Value);
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
        }

        if (acquired)
            return new(true, false, false, "server instance acquired", mutex);

        var healthCheck = isHealthy ?? IsHealthy;
        var healthy = healthCheck(new Uri($"http://{bind}:{port}/healthz"), identity.Value);
        mutex.Dispose();
        return new(
            false,
            true,
            healthy,
            healthy
                ? "remote server is already running for this profile"
                : "remote server instance is already owned but its health endpoint is not ready",
            null);
    }

    private static bool IsHealthy(Uri endpoint, string instance)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = client.GetAsync(endpoint).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return false;
            using var document = System.Text.Json.JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True
                && document.RootElement.TryGetProperty("instance", out var actual)
                && string.Equals(actual.GetString(), instance, StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
