using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Server;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class SessionOpenServiceTests
{
    [TestMethod]
    public async Task OpenAsync_UsesCanonicalArchiveIdentityToolWorkspaceAndAliases()
    {
        using var workspace = new TempDirectory();
        var starts = new List<ProcessStartInfo>();
        var launcher = Launcher(starts, windowsTerminal: @"C:\trusted\wt.exe");
        var service = Service(
            launcher,
            new ArchiveSession
            {
                Id = "canonical-id",
                Tool = "codex",
                Workspace = workspace.Path,
                Aliases = new() { "requested-alias", "child-id" },
            });

        var result = await service.OpenAsync("requested-alias", new OpenRequest("terminal"), CancellationToken.None);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.HasCount(1, starts);
        CollectionAssert.AreEqual(
            new[] { "-d", workspace.Path, @"C:\trusted\codex.exe", "resume", "canonical-id" },
            starts[0].ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task OpenAsync_RejectsUnknownOrAmbiguousIdentity()
    {
        using var workspace = new TempDirectory();
        var starts = new List<ProcessStartInfo>();
        var launcher = Launcher(starts);
        var service = Service(
            launcher,
            Session("one", "shared-alias", workspace.Path),
            Session("two", "shared-alias", workspace.Path));

        var unknown = await service.OpenAsync("missing", new OpenRequest("terminal"), CancellationToken.None);
        var ambiguous = await service.OpenAsync("shared-alias", new OpenRequest("terminal"), CancellationToken.None);

        Assert.IsFalse(unknown.Ok);
        StringAssert.Contains(unknown.Message, "not in");
        Assert.IsFalse(ambiguous.Ok);
        StringAssert.Contains(ambiguous.Message, "ambiguous");
        Assert.IsEmpty(starts);
    }

    [TestMethod]
    public async Task ResolveAsync_RejectsDuplicateCanonicalRecordsEvenWhenToolAndIdMatch()
    {
        using var firstWorkspace = new TempDirectory();
        using var secondWorkspace = new TempDirectory();
        var resolver = new CanonicalSessionResolver(_ =>
            Task.FromResult<IReadOnlyList<ArchiveSession>>(
                new[]
                {
                    new ArchiveSession { Id = "same-id", Tool = "codex", Workspace = firstWorkspace.Path },
                    new ArchiveSession { Id = "same-id", Tool = "codex", Workspace = secondWorkspace.Path },
                }));

        var result = await resolver.ResolveAsync("same-id", CancellationToken.None);

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Message, "ambiguous");
    }

    [TestMethod]
    public async Task OpenAsync_RejectsInvalidTargetToolAndMissingWorkspace()
    {
        var starts = new List<ProcessStartInfo>();
        var launcher = Launcher(starts);
        var service = Service(
            launcher,
            new ArchiveSession { Id = "bad-tool", Tool = "other", Workspace = Environment.CurrentDirectory },
            new ArchiveSession { Id = "--dangerously-skip-permissions", Tool = "claude", Workspace = Environment.CurrentDirectory },
            new ArchiveSession { Id = "missing-cwd", Tool = "codex", Workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });

        var target = await service.OpenAsync("bad-tool", new OpenRequest("powershell"), CancellationToken.None);
        var tool = await service.OpenAsync("bad-tool", new OpenRequest("terminal"), CancellationToken.None);
        var id = await service.OpenAsync("--dangerously-skip-permissions", new OpenRequest("terminal"), CancellationToken.None);
        var cwd = await service.OpenAsync("missing-cwd", new OpenRequest("terminal"), CancellationToken.None);

        Assert.IsFalse(target.Ok);
        StringAssert.Contains(target.Message, "target");
        Assert.IsFalse(tool.Ok);
        StringAssert.Contains(tool.Message, "tool");
        Assert.IsFalse(id.Ok);
        StringAssert.Contains(id.Message, "id");
        Assert.IsFalse(cwd.Ok);
        StringAssert.Contains(cwd.Message, "workspace");
        Assert.IsEmpty(starts);
    }

    [TestMethod]
    public void SessionLauncher_UsesStructuredArgumentsForMetacharacterPaths()
    {
        using var workspace = new TempDirectory("launch ';& workspace");
        var starts = new List<ProcessStartInfo>();
        var launcher = Launcher(starts, windowsTerminal: @"C:\Program Files\WindowsApps\wt.exe");
        var session = new TrustedSessionLaunch(
            "safe-session-id",
            SessionTool.Codex,
            workspace.Path,
            new[] { "safe-session-id" });

        var result = launcher.Open(session, SessionOpenTarget.Terminal);

        Assert.IsTrue(result.ok, result.message);
        Assert.HasCount(1, starts);
        Assert.AreEqual("", starts[0].Arguments);
        CollectionAssert.AreEqual(
            new[] { "-d", workspace.Path, @"C:\trusted\codex.exe", "resume", "safe-session-id" },
            starts[0].ArgumentList.ToArray());
    }

    [TestMethod]
    public void SessionLauncher_DirectFallbackUsesStructuredArgumentsWithoutShellText()
    {
        using var workspace = new TempDirectory("fallback ';& workspace");
        var starts = new List<ProcessStartInfo>();
        var launcher = Launcher(starts, windowsTerminal: null);
        var session = new TrustedSessionLaunch(
            "claude-session-id",
            SessionTool.Claude,
            workspace.Path,
            new[] { "claude-session-id" });

        var result = launcher.Open(session, SessionOpenTarget.Terminal);

        Assert.IsTrue(result.ok, result.message);
        Assert.HasCount(1, starts);
        Assert.AreEqual(@"C:\trusted\claude.exe", starts[0].FileName);
        Assert.AreEqual(workspace.Path, starts[0].WorkingDirectory);
        Assert.AreEqual("", starts[0].Arguments);
        CollectionAssert.AreEqual(new[] { "--resume", "claude-session-id" }, starts[0].ArgumentList.ToArray());
    }

    [TestMethod]
    public void SessionLauncher_AccountHomeCodexResumeSetsEnvironmentOnTerminal()
    {
        using var workspace = new TempDirectory("account workspace");
        using var accounts = new TempDirectory("account root");
        var accountHome = System.IO.Path.Combine(accounts.Path, "acct");
        var transcript = System.IO.Path.Combine(accountHome, "sessions", "rollout.jsonl");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, "{}");
        var starts = new List<ProcessStartInfo>();
        var launcher = Launcher(starts, windowsTerminal: null, codexAccountsRoot: accounts.Path);
        var session = new TrustedSessionLaunch(
            "codex-session-id",
            SessionTool.Codex,
            workspace.Path,
            new[] { "codex-session-id" })
        {
            SourcePath = transcript,
        };

        var result = launcher.Open(session, SessionOpenTarget.Terminal);

        Assert.IsTrue(result.ok, result.message);
        Assert.HasCount(1, starts);
        Assert.AreEqual(accountHome, starts[0].Environment["CODEX_HOME"]);
    }

    [TestMethod]
    public void SessionLauncher_DefaultHomeCodexResumeLeavesEnvironmentUnmodified()
    {
        using var workspace = new TempDirectory("default workspace");
        using var accounts = new TempDirectory("account root");
        var transcript = System.IO.Path.Combine(workspace.Path, "rollout.jsonl");
        File.WriteAllText(transcript, "{}");
        var starts = new List<ProcessStartInfo>();
        var launcher = Launcher(starts, windowsTerminal: null, codexAccountsRoot: accounts.Path);
        var session = new TrustedSessionLaunch(
            "codex-session-id",
            SessionTool.Codex,
            workspace.Path,
            new[] { "codex-session-id" })
        {
            SourcePath = transcript,
        };

        var result = launcher.Open(session, SessionOpenTarget.Terminal);

        Assert.IsTrue(result.ok, result.message);
        Assert.HasCount(1, starts);
        var ambientCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var hasLaunchCodexHome = starts[0].Environment.TryGetValue("CODEX_HOME", out var launchCodexHome);
        Assert.AreEqual(ambientCodexHome is not null, hasLaunchCodexHome);
        if (ambientCodexHome is not null)
            Assert.AreEqual(ambientCodexHome, launchCodexHome);
    }

    [TestMethod]
    public void SessionLauncher_GatewayMode_UsesCcResume()
    {
        using var workspace = new TempDirectory("gateway workspace");
        var starts = new List<ProcessStartInfo>();
        var launcher = new SessionLauncher(
            @"C:\trusted\claude.exe",
            @"C:\trusted\codex.exe",
            allowLaunch: true,
            isSessionLive: _ => false,
            claimOptions: new(
                RootDirectory: Path.Combine(Path.GetTempPath(), "clr-session-open-tests", Guid.NewGuid().ToString("N"))),
            startProcess: psi => { starts.Add(psi); return null; },
            windowsTerminal: null,
            discoverWindowsTerminal: false,
            ownerRecordOptions: new(
                RootDirectory: Path.Combine(Path.GetTempPath(), "clr-session-open-tests", Guid.NewGuid().ToString("N"))),
            gatewayCliScript: @"C:\trusted\cc.cmd",
            cmdExe: @"C:\Windows\System32\cmd.exe");
        var session = new TrustedSessionLaunch(
            "gateway-session-id",
            SessionTool.Claude,
            workspace.Path,
            new[] { "gateway-session-id" },
            ArchiveService.GatewayLaunchMode);

        var result = launcher.Open(session, SessionOpenTarget.Terminal);

        Assert.IsTrue(result.ok, result.message);
        Assert.HasCount(1, starts);
        Assert.AreEqual(@"C:\Windows\System32\cmd.exe", starts[0].FileName);
        CollectionAssert.AreEqual(
            new[] { "/c", @"C:\trusted\cc.cmd", "--resume", "gateway-session-id" },
            starts[0].ArgumentList.ToArray());
    }

    [TestMethod]
    public void SessionLauncher_GatewayMode_RefusesCodexTranscript()
    {
        using var workspace = new TempDirectory("gateway codex workspace");
        var starts = new List<ProcessStartInfo>();
        var launcher = new SessionLauncher(
            @"C:\trusted\claude.exe",
            @"C:\trusted\codex.exe",
            allowLaunch: true,
            isSessionLive: _ => false,
            claimOptions: new(
                RootDirectory: Path.Combine(Path.GetTempPath(), "clr-session-open-tests", Guid.NewGuid().ToString("N"))),
            startProcess: psi => { starts.Add(psi); return null; },
            windowsTerminal: null,
            discoverWindowsTerminal: false,
            ownerRecordOptions: new(
                RootDirectory: Path.Combine(Path.GetTempPath(), "clr-session-open-tests", Guid.NewGuid().ToString("N"))),
            gatewayCliScript: @"C:\trusted\cc.cmd",
            cmdExe: @"C:\Windows\System32\cmd.exe");
        var session = new TrustedSessionLaunch(
            "codex-session-id",
            SessionTool.Codex,
            workspace.Path,
            new[] { "codex-session-id" },
            ArchiveService.GatewayLaunchMode);

        var result = launcher.Open(session, SessionOpenTarget.Terminal);

        Assert.IsFalse(result.ok);
        StringAssert.Contains(result.message, "Claude transcripts only");
        Assert.IsEmpty(starts);
    }

    [TestMethod]
    public void BrowserAndRequestContract_DoNotAcceptToolOrWorkspaceOverrides()
    {
        CollectionAssert.AreEqual(
            new[] { "Target" },
            typeof(OpenRequest).GetProperties().Select(p => p.Name).OrderBy(x => x).ToArray());

        var root = FindRepoRoot();
        var html = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Server", "wwwroot", "index.html"));
        Assert.IsFalse(html.Contains("source:sess.source", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("cwd:sess.cwd", StringComparison.Ordinal));
        Assert.IsTrue(html.Contains("JSON.stringify({op:'open',id})", StringComparison.Ordinal));
        Assert.IsTrue(html.Contains("App.agentPending={id,source,title,cwd}", StringComparison.Ordinal));
        Assert.IsTrue(html.Contains("if(App.agentPending)return;", StringComparison.Ordinal));
        Assert.IsTrue(html.Contains("disabled:(App.agentBusy||App.agentPending)?'':null", StringComparison.Ordinal));
        Assert.IsTrue(html.Contains("case 'OpenFailed':", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("case 'Error': if(App.agentPending)", StringComparison.Ordinal));

        var webSocket = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Server", "AgentWebSocket.cs"));
        Assert.IsTrue(webSocket.Contains("resolveSession(id, ct)", StringComparison.Ordinal));
        Assert.IsTrue(webSocket.Contains("ClearOpenSession()", StringComparison.Ordinal));
        Assert.IsTrue(webSocket.Contains("claudeLaunchMode = trusted.LaunchMode", StringComparison.Ordinal));
        Assert.IsTrue(webSocket.Contains("launchMode: claudeLaunchMode", StringComparison.Ordinal));
        var openCase = webSocket[webSocket.IndexOf("case \"open\":", StringComparison.Ordinal)..];
        Assert.IsTrue(openCase.IndexOf("resolveSession(id, ct)", StringComparison.Ordinal)
            < openCase.IndexOf("ClearOpenSession();", StringComparison.Ordinal));
        Assert.IsTrue(webSocket.Contains("hub.IsTurnActive(openThread.ThreadId)", StringComparison.Ordinal));
        var clearStart = webSocket.IndexOf("void ClearOpenSession()", StringComparison.Ordinal);
        var activeStart = webSocket.IndexOf("bool HasActiveTurn()", clearStart, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, clearStart);
        Assert.IsGreaterThan(clearStart, activeStart);
        Assert.IsFalse(webSocket[clearStart..activeStart].Contains(".Kill(", StringComparison.Ordinal));
        Assert.IsTrue(webSocket.Contains("ownerStopping", StringComparison.Ordinal));
        Assert.IsFalse(webSocket.Contains("openSource = Str(root, \"source\")", StringComparison.Ordinal));
        Assert.IsFalse(webSocket.Contains("claudeCwd = Str(root, \"cwd\")", StringComparison.Ordinal));
        Assert.IsFalse(webSocket.Contains("hub.OpenThreadAsync(id, Str(root, \"cwd\")", StringComparison.Ordinal));
        Assert.IsFalse(webSocket.Contains("aliasesForSessionId", StringComparison.Ordinal));
        Assert.IsFalse(webSocket.Contains("claudeProc is { HasExited: false }", StringComparison.Ordinal));
        Assert.IsFalse(webSocket.Contains("generation == openGeneration", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AgentWebSocket_ResolveFailurePreservesPreviouslyOpenedSession()
    {
        using var workspace = new TempDirectory();
        using var claudeRoot = new TempDirectory();
        var socket = new ScriptedWebSocket(
            """{"op":"open","id":"session-one"}""",
            """{"op":"open","id":"session-two"}""",
            """{"op":"interrupt"}""");
        var hub = new CodexAgentHub(
            Path.Combine(workspace.Path, "missing-codex.exe"),
            isSessionLive: _ => false);
        var claudeDriver = new ClaudeLiveDriver(
            Path.Combine(workspace.Path, "missing-claude.exe"),
            isSessionLive: _ => false);

        await AgentWebSocket.HandleAsync(
            socket,
            hub,
            new ClaudeSessionStore(claudeRoot.Path),
            claudeDriver,
            new CommandSigner(null),
            workspace.Path,
            CancellationToken.None,
            CancellationToken.None,
            (id, _) => id == "session-one"
                ? Task.FromResult(new SessionResolutionResult(
                    true,
                    new TrustedSessionLaunch(
                        "session-one",
                        SessionTool.Claude,
                        workspace.Path,
                        new[] { "session-one" }),
                    "ok"))
                : throw new IOException("transient archive failure"));

        var payloads = socket.Sent.Select(payload => JsonDocument.Parse(payload)).ToList();
        try
        {
            Assert.IsTrue(payloads.Any(p =>
                p.RootElement.TryGetProperty("kind", out var kind)
                && kind.GetString() == "Opened"));
            Assert.IsTrue(payloads.Any(p =>
                p.RootElement.TryGetProperty("text", out var text)
                && text.GetString() == "open failed: transient archive failure"));
            Assert.IsTrue(payloads.Any(p =>
                p.RootElement.TryGetProperty("text", out var text)
                && text.GetString() == "idle"),
                "Interrupt should still target the previously opened Claude session after resolution fails.");
        }
        finally
        {
            foreach (var payload in payloads) payload.Dispose();
            await hub.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AgentWebSocket_NewThreadFailurePreservesPreviouslyOpenedSession()
    {
        using var workspace = new TempDirectory();
        using var claudeRoot = new TempDirectory();
        var socket = new ScriptedWebSocket(
            """{"op":"open","id":"session-one"}""",
            $$"""{"op":"new","cwd":{{JsonSerializer.Serialize(workspace.Path)}}}""",
            """{"op":"interrupt"}""");
        var hub = new CodexAgentHub(
            Path.Combine(workspace.Path, "missing-codex.exe"),
            isSessionLive: _ => false);

        await AgentWebSocket.HandleAsync(
            socket,
            hub,
            new ClaudeSessionStore(claudeRoot.Path),
            new ClaudeLiveDriver(
                Path.Combine(workspace.Path, "missing-claude.exe"),
                isSessionLive: _ => false),
            new CommandSigner(null),
            workspace.Path,
            CancellationToken.None,
            CancellationToken.None,
            (_, _) => Task.FromResult(new SessionResolutionResult(
                true,
                new TrustedSessionLaunch(
                    "session-one",
                    SessionTool.Claude,
                    workspace.Path,
                    new[] { "session-one" }),
                "ok")));

        var payloads = socket.Sent.Select(payload => JsonDocument.Parse(payload)).ToList();
        try
        {
            Assert.IsTrue(payloads.Any(p =>
                p.RootElement.TryGetProperty("text", out var text)
                && text.GetString() is { } message
                && message.StartsWith("new session failed:", StringComparison.Ordinal)));
            Assert.IsTrue(payloads.Any(p =>
                p.RootElement.TryGetProperty("text", out var text)
                && text.GetString() == "idle"),
                "Interrupt should still target the previously opened Claude session after new-thread creation fails.");
        }
        finally
        {
            foreach (var payload in payloads) payload.Dispose();
            await hub.DisposeAsync();
        }
    }

    private static SessionOpenService Service(SessionLauncher launcher, params ArchiveSession[] sessions) =>
        new(
            new CanonicalSessionResolver(_ => Task.FromResult<IReadOnlyList<ArchiveSession>>(sessions)),
            launcher);

    private static ArchiveSession Session(string id, string alias, string workspace) =>
        new() { Id = id, Tool = "codex", Workspace = workspace, Aliases = new() { alias } };

    private static SessionLauncher Launcher(
        List<ProcessStartInfo> starts,
        string? windowsTerminal = null,
        string? codexAccountsRoot = null) =>
        new(
            @"C:\trusted\claude.exe",
            @"C:\trusted\codex.exe",
            allowLaunch: true,
            isSessionLive: _ => false,
            claimOptions: new(
                RootDirectory: Path.Combine(Path.GetTempPath(), "clr-session-open-tests", Guid.NewGuid().ToString("N"))),
            startProcess: psi => { starts.Add(psi); return null; },
            windowsTerminal: windowsTerminal,
            discoverWindowsTerminal: false,
            ownerRecordOptions: new(
                RootDirectory: Path.Combine(Path.GetTempPath(), "clr-session-open-tests", Guid.NewGuid().ToString("N"))),
            codexAccountsRoot: codexAccountsRoot);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodexLocalRetrieval.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find CodexLocalRetrieval.sln from " + AppContext.BaseDirectory);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string? leaf = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), leaf ?? Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private sealed class ScriptedWebSocket(params string[] messages) : WebSocket
    {
        private readonly Queue<byte[]> _messages =
            new(messages.Select(Encoding.UTF8.GetBytes));
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private WebSocketState _state = WebSocketState.Open;

        public List<string> Sent { get; } = new();
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_messages.Count == 0)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true,
                    WebSocketCloseStatus.NormalClosure,
                    "done"));
            }

            var message = _messages.Dequeue();
            Assert.IsTrue(message.Length <= buffer.Count);
            message.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(
                message.Length,
                WebSocketMessageType.Text,
                true));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}
