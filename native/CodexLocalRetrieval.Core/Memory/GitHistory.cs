using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexLocalRetrieval.Core.Memory;

// Git history for ONE brain vault. Local only — never adds a remote, never pushes. Git is strongly
// recommended but NOT a hard blocker: if git is missing, every method degrades gracefully (Commit
// returns "" and the brain still works, just without history). Uses Process, consistent with the
// app's existing ResumeInTerminal / OpenBackupsFolder pattern (no NuGet git dependency).
public sealed class GitHistory
{
    public sealed record GitResult(bool Ok, int ExitCode, string StdOut, string StdErr);

    private static bool? _available;

    public bool IsAvailable()
    {
        if (_available.HasValue) return _available.Value;
        try
        {
            var r = Run(Environment.CurrentDirectory, "--version");
            _available = r.Ok && r.StdOut.Contains("git", StringComparison.OrdinalIgnoreCase);
        }
        catch { _available = false; }
        return _available.Value;
    }

    public bool IsRepo(string vaultDir) => Directory.Exists(Path.Combine(vaultDir, ".git"));

    // Initializes a local repo in the vault if one isn't there. No-op (false) if git is unavailable.
    public bool InitIfNeeded(string vaultDir)
    {
        if (!IsAvailable()) return false;
        if (IsRepo(vaultDir)) return true;
        Directory.CreateDirectory(vaultDir);
        var init = Run(vaultDir, "init");
        if (!init.Ok) return false;
        // Make sure commits never fail for lack of identity, without touching global config.
        Run(vaultDir, "config user.name \"codex-local-retrieval\"");
        Run(vaultDir, "config user.email \"brain@codex-local-retrieval.local\"");
        Run(vaultDir, "config commit.gpgsign false");
        return true;
    }

    // Stages everything and commits. Returns the new commit hash, or "" if git is absent or there was
    // nothing to commit. Local commit only — no remotes are ever configured or contacted.
    public string Commit(string vaultDir, string message)
    {
        if (!IsAvailable()) return "";
        if (!InitIfNeeded(vaultDir)) return "";
        Run(vaultDir, "add -A");
        // -c on the commit guards the rare case init-time config didn't take.
        var commit = Run(vaultDir,
            "-c user.name=codex-local-retrieval -c user.email=brain@codex-local-retrieval.local " +
            "commit --no-gpg-sign -m " + Quote(message));
        if (!commit.Ok && !commit.StdOut.Contains("nothing to commit") && !commit.StdErr.Contains("nothing to commit"))
        {
            // nothing staged or commit failed — surface "" (callers treat history as best-effort).
            if (commit.StdOut.Contains("nothing to commit") || commit.StdErr.Contains("nothing to commit")) return "";
        }
        var head = Run(vaultDir, "rev-parse HEAD");
        return head.Ok ? head.StdOut.Trim() : "";
    }

    public IReadOnlyList<string> Log(string vaultDir, int max = 50)
    {
        if (!IsAvailable() || !IsRepo(vaultDir)) return Array.Empty<string>();
        var r = Run(vaultDir, $"log --oneline -n {max}");
        if (!r.Ok) return Array.Empty<string>();
        return r.StdOut.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // The files git is actually tracking — the basis of the GG8 "no raw transcript committed" check.
    public IReadOnlyList<string> LsFiles(string vaultDir)
    {
        if (!IsAvailable() || !IsRepo(vaultDir)) return Array.Empty<string>();
        var r = Run(vaultDir, "ls-files");
        if (!r.Ok) return Array.Empty<string>();
        return r.StdOut.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public string Diff(string vaultDir, string args = "HEAD~1 HEAD")
    {
        if (!IsAvailable() || !IsRepo(vaultDir)) return "";
        var r = Run(vaultDir, "diff " + args);
        return r.Ok ? r.StdOut : "";
    }

    private static GitResult Run(string workingDir, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return new GitResult(false, -1, "", "could not start git");
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } return new GitResult(false, -1, so, "git timed out"); }
            return new GitResult(p.ExitCode == 0, p.ExitCode, so, se);
        }
        catch (Exception ex)
        {
            return new GitResult(false, -1, "", ex.Message);
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
