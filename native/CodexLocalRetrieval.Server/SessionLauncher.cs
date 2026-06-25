using System.Diagnostics;

namespace CodexLocalRetrieval.Server;

// Reopens a stored chat where the user actually works — in VS Code (the same editor session) or a fresh
// terminal — so the app is a home base you return to, not 15 tabs you're afraid to close. Everything is
// launched ON this PC (that's where the editor/CLI live); gated by CLR_REMOTE_ALLOW_LAUNCH.
public sealed class SessionLauncher
{
    private readonly string _claudeExe;
    private readonly string _codexExe;
    private readonly bool _allow;
    private readonly string? _wt;

    public SessionLauncher(string claudeExe, string codexExe, bool allowLaunch)
    {
        _claudeExe = claudeExe;
        _codexExe = codexExe;
        _allow = allowLaunch;
        _wt = Which("wt.exe");
    }

    public bool Enabled => _allow;

    public (bool ok, string message) Open(string source, string id, string target, string? cwd)
    {
        if (!_allow) return (false, "Reopening is disabled on this server (set CLR_REMOTE_ALLOW_LAUNCH=1).");
        if (string.IsNullOrWhiteSpace(id)) return (false, "missing session id");
        var dir = !string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd)
            ? cwd!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            if (target == "vscode")
            {
                // Claude has a deep-link straight to the session; for Codex, open the workspace folder so
                // its sidebar lists the session.
                var uri = source == "claude"
                    ? $"vscode://anthropic.claude-code/open?session={Uri.EscapeDataString(id)}"
                    : $"vscode://file/{dir.Replace('\\', '/')}";
                OpenUri(uri);
                return (true, source == "claude" ? "Opening the chat in VS Code…" : "Opening the workspace in VS Code…");
            }
            // terminal
            var exe = source == "claude" ? _claudeExe : _codexExe;
            var args = source == "claude" ? $"--resume {id}" : $"resume {id}";
            OpenTerminal(dir, exe, args);
            return (true, "Opening a terminal…");
        }
        catch (Exception ex) { return (false, "couldn't launch: " + ex.Message); }
    }

    private static void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });

    private void OpenTerminal(string dir, string exe, string args)
    {
        if (_wt is not null)
        {
            // Windows Terminal opens in the folder running the resume command directly.
            Process.Start(new ProcessStartInfo
            {
                FileName = _wt,
                Arguments = $"-d \"{dir}\" \"{exe}\" {args}",
                UseShellExecute = true
            });
            return;
        }
        // Fallback: a PowerShell window in the folder, kept open.
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -NoProfile -Command \"Set-Location -LiteralPath '{dir}'; & '{exe}' {args}\"",
            UseShellExecute = true
        });
    }

    private static string? Which(string exe)
    {
        try
        {
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var full = Path.Combine(p, exe);
                if (File.Exists(full)) return full;
            }
        }
        catch { }
        return null;
    }
}
