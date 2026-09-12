using System.Net.Http;
using System.Text;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Server;

public static class LoopbackRelayTransport
{
    public static string ValidateFixtureProfile(string? storePath, string? claudeProjects, string bind, bool bundled)
    {
        if (Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_PROFILE") != "1") return "";
        if (!string.Equals(bind, "127.0.0.1", StringComparison.Ordinal))
            return "isolated remote test profile requires CLR_REMOTE_BIND=127.0.0.1";
        if (bundled) return "isolated remote test profile rejects CLR_REMOTE_BUNDLED=1";
        if (string.IsNullOrWhiteSpace(storePath) || !Path.IsPathFullyQualified(storePath) || !File.Exists(storePath))
            return "isolated remote test profile requires CLR_REMOTE_STORE to be an existing absolute fixture file";
        if (string.IsNullOrWhiteSpace(claudeProjects) || !Path.IsPathFullyQualified(claudeProjects) || !Directory.Exists(claudeProjects))
            return "isolated remote test profile requires CLR_CLAUDE_PROJECTS to be an existing absolute fixture directory";

        var fixtureRoot = Path.GetFullPath(Path.GetDirectoryName(storePath)!);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var sourceRoot = Path.GetFullPath(claudeProjects);
        if (!IsUnder(fixtureRoot, tempRoot) || !IsUnder(sourceRoot, fixtureRoot))
            return "isolated remote test profile requires store and Claude sources under the temporary fixture root";
        return "";
    }

    public static bool IsGuiFixtureRunning(string storePath)
    {
        if (Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_PROFILE") != "1") return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(storePath)!));
        if (!File.Exists(Path.Combine(root, "gui-fixture.marker"))) return false;
        var name = @"Local\MUX.GuiFixture." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        try
        {
            using var mutex = Mutex.OpenExisting(name);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static string ValidateFixturePort(int archivePort)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_RELAY_PORT"), out var fixturePort)
            || fixturePort is < 1 or > 65535)
            return "isolated remote test profile requires CLR_REMOTE_TEST_RELAY_PORT in 1..65535";
        return fixturePort == archivePort
            ? ""
            : $"isolated remote test profile requires CLR_REMOTE_TEST_RELAY_PORT={archivePort} to match the fixture archive setting";
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    public static Func<RemoteBridge.Settings, RemoteBridge.BridgeOperation, string?, string?, TimeSpan?, Task<(int code, string outText)>> Create()
    {
        if (Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_PROFILE") != "1")
            throw new InvalidOperationException("loopback relay transport requires CLR_REMOTE_TEST_PROFILE=1");
        return SendAsync;
    }

    public static Func<object, Task<string>>? CreateMuxFixture()
    {
        var configured = Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_MUX_PORT");
        if (string.IsNullOrEmpty(configured)) return null;
        if (Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_PROFILE") != "1"
            || !int.TryParse(configured, out var port) || port is < 1024 or > 65535 || port == 7699
            || !int.TryParse(Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_MUX_PID"), out var pid) || pid <= 0)
            throw new InvalidOperationException("fixture mux requires an isolated nonproduction port and process identity");
        var instance = Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_MUX_INSTANCE");
        if (string.IsNullOrWhiteSpace(instance))
            throw new InvalidOperationException("fixture mux instance identity required");

        async Task<string> Request(object frame)
        {
            using var socket = new System.Net.WebSockets.ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(frame);
            await socket.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token);
            using var response = new MemoryStream();
            var buffer = new byte[16384];
            System.Net.WebSockets.WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (part.MessageType != System.Net.WebSockets.WebSocketMessageType.Text || response.Length + part.Count > 4 * 1024 * 1024)
                    throw new InvalidDataException("invalid fixture mux response");
                response.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            return Encoding.UTF8.GetString(response.ToArray());
        }
        return async frame =>
        {
            using var info = System.Text.Json.JsonDocument.Parse(await Request(new { t = "info" }));
            var root = info.RootElement;
            if (root.GetProperty("t").GetString() != "info" || root.GetProperty("pid").GetInt32() != pid
                || root.GetProperty("instanceId").GetString() != instance)
                throw new InvalidOperationException("fixture mux identity changed; dispatch refused");
            var result = await Request(frame);
            using var sent = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(frame));
            if (sent.RootElement.TryGetProperty("t", out var operation) && operation.GetString() == "kill")
            {
                using var reply = System.Text.Json.JsonDocument.Parse(result);
                var message = reply.RootElement.TryGetProperty("m", out var detail) ? detail.GetString() : "";
                var classification = message switch
                {
                    "ConPTY host cleanup deadline expired" => "conpty-deadline",
                    "ConPTY host process is still alive after termination" => "conpty-alive",
                    "process termination deadline expired" => "process-deadline",
                    _ => reply.RootElement.TryGetProperty("t", out var type) && type.GetString() == "killed" ? "killed" : "other-refusal"
                };
                Console.Error.WriteLine("fixture-mux-kill-result=" + classification);
            }
            return result;
        };
    }

    public static async Task<bool> PushTranscriptAsync(RemoteBridge.Settings settings, string sessionId,
        string credential, string body, TimeSpan timeout, bool isolated)
    {
        var command = TranscriptFetchProjection.PushCommand(settings.Port, sessionId, credential);
        if (isolated)
        {
            if (Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_PROFILE") != "1"
                || !IsFixturePort(settings.Port) || settings.Target != "loopback") return false;
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout };
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"http://127.0.0.1:{settings.Port}/api/transcripts/{sessionId}");
            request.Headers.Add("X-Mux-Transcript-Bridge", credential);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        var start = new System.Diagnostics.ProcessStartInfo("ssh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", settings.Target, command })
            start.ArgumentList.Add(arg);
        var result = await ContainedProcessRunner.RunAsync(start, timeout, body,
            maxStdoutChars: 4096, maxStderrChars: 4096);
        return !result.TimedOut && !result.StdoutTruncated && result.ExitCode == 0;
    }

    public static async Task<bool> DownloadUploadAsync(RemoteBridge.Settings settings, string fixtureRoot, string uploadId, string destination)
    {
        if (Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_PROFILE") != "1"
            || settings.Target != "loopback" || !IsFixturePort(settings.Port)
            || !IsUnder(Path.GetFullPath(fixtureRoot), Path.GetFullPath(Path.GetTempPath()))
            || !IsUnder(Path.GetFullPath(destination), Path.GetFullPath(fixtureRoot))) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await client.GetAsync($"http://127.0.0.1:{settings.Port}/api/uploads/{Uri.EscapeDataString(uploadId)}/raw",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        const int limit = 20 * 1024 * 1024;
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit) return false;
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (bytes.Length + count > limit) return false;
            bytes.Write(buffer, 0, count);
        }
        await File.WriteAllBytesAsync(destination, bytes.ToArray(), timeout.Token);
        return true;
    }

    internal static bool IsFixturePort(int port) =>
        int.TryParse(Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_RELAY_PORT"), out var expected) && expected == port;

    private static async Task<(int code, string outText)> SendAsync(
        RemoteBridge.Settings settings, RemoteBridge.BridgeOperation operation, string? body, string? commandId, TimeSpan? timeout)
    {
        if (!string.Equals(settings.Target, "loopback", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(settings.Target, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return (-1, "loopback transport requires loopback target");
        if (!IsFixturePort(settings.Port))
            return (-1, "loopback transport requires the independently configured fixture relay port");
        if (operation == RemoteBridge.BridgeOperation.Ack
            && !RemoteCommandProtocol.IsWellFormedEnvelopeToken(commandId))
            return (-1, "ack command id invalid");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        var path = operation switch
        {
            RemoteBridge.BridgeOperation.Running => "/api/running",
            RemoteBridge.BridgeOperation.Lease => "/api/app-commands/lease",
            RemoteBridge.BridgeOperation.Ack when !string.IsNullOrWhiteSpace(commandId) => $"/api/app-commands/{Uri.EscapeDataString(commandId)}/ack",
            _ => ""
        };
        if (path.Length == 0) return (-1, "ack command id required");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{settings.Port}{path}");
        if (operation is RemoteBridge.BridgeOperation.Lease or RemoteBridge.BridgeOperation.Ack)
        {
            var token = Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_COMMAND_BRIDGE_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                return (-1, "loopback command transport requires a command bridge token");
            if (!request.Headers.TryAddWithoutValidation("X-Mux-Command-Bridge", token))
                return (-1, "loopback command bridge token is not a valid header value");
        }
        request.Content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode is >= 200 and < 300 ? 0 : (int)response.StatusCode, text);
        }
        catch (TaskCanceledException) { return (-1, "loopback relay request timed out"); }
        catch (HttpRequestException) { return (-1, "loopback relay HTTP request failed"); }
        catch (Exception ex) { return (-1, "loopback relay request failed (" + ex.GetType().Name + ")"); }
    }
}
