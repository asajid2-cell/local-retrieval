namespace CodexLocalRetrieval.Core.Remote;

public sealed record RemoteRuntimeProfile(
    string Name,
    int RelayPort,
    int MuxdControlPort,
    string MuxdTaskName,
    string PrincipalInstanceId,
    string StateRoot)
{
    public const int ProductionRelayPort = 7682;
    public const int ProductionMuxdControlPort = 7699;
    public const string ProductionMuxdTaskName = "MuxdSessionHost";

    public Uri MuxdControlUri => new($"ws://127.0.0.1:{MuxdControlPort}");

    public int RelayPortFor(int configuredPort)
    {
        if (Name != "production" && configuredPort != ProductionRelayPort && configuredPort != RelayPort)
            throw new InvalidOperationException("archive relay port conflicts with the active runtime profile");
        return Name == "production" ? configuredPort : RelayPort;
    }

    public static RemoteRuntimeProfile Load(
        int configuredRelayPort,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var processEnvironment = readEnvironment;
        var envFile = processEnvironment("MUXD_ENV_FILE");
        if (!string.IsNullOrWhiteSpace(envFile))
        {
            if (!Path.IsPathFullyQualified(envFile))
                throw new InvalidOperationException("MUXD_ENV_FILE must be an absolute path");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(envFile))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var separator = trimmed.IndexOf('=');
                if (separator < 0) continue;
                var key = trimmed[..separator].Trim();
                if (!values.TryAdd(key, trimmed[(separator + 1)..].Trim()))
                    throw new InvalidOperationException("duplicate runtime profile setting: " + key);
            }
            readEnvironment = key => processEnvironment(key) ?? (values.TryGetValue(key, out var value) ? value : null);
        }
        var name = (readEnvironment("MUXD_PROFILE") ?? "production").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9._-]+$"))
            throw new InvalidOperationException("MUXD_PROFILE has invalid characters");

        if (string.Equals(name, "production", StringComparison.Ordinal))
        {
            return new RemoteRuntimeProfile(
                name,
                configuredRelayPort,
                ProductionMuxdControlPort,
                ProductionMuxdTaskName,
                Environment.MachineName,
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexArchiveRemote"));
        }

        var relayPort = RequiredPort(readEnvironment, "CLR_RELAY_PORT");
        var muxdPort = RequiredPort(readEnvironment, "MUXD_CONTROL_PORT");
        var taskName = Required(readEnvironment, "MUXD_TASK_NAME");
        var principalInstanceId = Required(readEnvironment, "MUXD_PRINCIPAL_INSTANCE_ID");
        var stateRoot = RequiredAbsolutePath(readEnvironment, "MUXD_STATE_ROOT");
        var productionStateRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexArchiveRemote"));

        if (!RemoteCommandProtocol.IsWellFormedEnvelopeToken(principalInstanceId))
            throw new InvalidOperationException("MUXD_PRINCIPAL_INSTANCE_ID must be a valid identity token");
        if (relayPort == muxdPort)
            throw new InvalidOperationException("relay and muxd control ports must be distinct");
        if (relayPort is ProductionMuxdControlPort or 8765 || muxdPort is ProductionRelayPort or 8765)
            throw new InvalidOperationException("non-production ports overlap a production service");
        if (relayPort == ProductionRelayPort)
            throw new InvalidOperationException("non-production CLR_RELAY_PORT overlaps production");
        if (muxdPort == ProductionMuxdControlPort)
            throw new InvalidOperationException("non-production MUXD_CONTROL_PORT overlaps production");
        if (string.Equals(taskName, ProductionMuxdTaskName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("non-production MUXD_TASK_NAME overlaps production");
        if (string.Equals(principalInstanceId, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("non-production MUXD_PRINCIPAL_INSTANCE_ID overlaps production");
        if (PathsOverlap(stateRoot, productionStateRoot))
            throw new InvalidOperationException("non-production MUXD_STATE_ROOT overlaps production");
        return new RemoteRuntimeProfile(name, relayPort, muxdPort, taskName, principalInstanceId, stateRoot);
    }

    private static string Required(Func<string, string?> readEnvironment, string name)
    {
        var value = (readEnvironment(name) ?? "").Trim();
        if (value.Length == 0)
            throw new InvalidOperationException($"non-production profile requires {name}");
        return value;
    }

    private static int RequiredPort(Func<string, string?> readEnvironment, string name)
    {
        var value = Required(readEnvironment, name);
        if (!int.TryParse(value, out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException($"{name} must be between 1024 and 65535");
        return port;
    }

    private static string RequiredAbsolutePath(Func<string, string?> readEnvironment, string name)
    {
        var value = Required(readEnvironment, name);
        if (!Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{name} must be an absolute path");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static bool PathsOverlap(string left, string right)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
