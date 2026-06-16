using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace CodexLocalRetrieval_Native;

// Agent self-service bridge. An outside Claude/Codex chat appends JSON commands to agent-inbox.jsonl
// (documented in AGENTS.md, which the app keeps current next to the inbox). The app polls the inbox,
// re-scans so the agent's own session is indexed, applies each command, and writes acks to the
// outbox. This is what lets a user say "set yourself up in my app / favorite yourself / file
// yourself into project X" from any chat. See Core ArchiveService.ApplyAgentCommandAsync.
public sealed partial class MainPage
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _agentTimer;
    private int _agentProcessed;
    private bool _agentBusy;

    private static string AgentDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLocalRetrieval");
    private static string AgentInbox => Path.Combine(AgentDir, "agent-inbox.jsonl");
    private static string AgentOutbox => Path.Combine(AgentDir, "agent-outbox.jsonl");
    private static string AgentCursor => Path.Combine(AgentDir, "agent-inbox.cursor");
    private static string AgentDoc => Path.Combine(AgentDir, "AGENTS.md");

    private void StartAgentBridge()
    {
        try
        {
            Directory.CreateDirectory(AgentDir);
            File.WriteAllText(AgentDoc, AgentProtocolDoc());
            if (!File.Exists(AgentInbox)) File.WriteAllText(AgentInbox, "");
            if (File.Exists(AgentCursor) && int.TryParse(File.ReadAllText(AgentCursor).Trim(), out var c)) _agentProcessed = c;
        }
        catch (Exception ex) { Diag.Log("Agent bridge init failed " + ex); return; }

        _agentTimer = DispatcherQueue.CreateTimer();
        _agentTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _agentTimer.Tick += async (_, _) => await PollAgentInboxAsync();
        _agentTimer.Start();
        Diag.Log("Agent bridge ENABLED, inbox=" + AgentInbox);
    }

    private async Task PollAgentInboxAsync()
    {
        if (_agentBusy || !_storeLoaded || _syncing) return;
        string text;
        try { if (!File.Exists(AgentInbox)) return; text = File.ReadAllText(AgentInbox); }
        catch { return; }

        // Only act on COMPLETE lines (terminated by \n); leave a half-written tail for the next poll.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var complete = lines.Length - 1;
        if (complete < _agentProcessed) _agentProcessed = 0;        // inbox was truncated/reset
        if (complete <= _agentProcessed) return;

        _agentBusy = true;
        try
        {
            var fresh = lines.Skip(_agentProcessed).Take(complete - _agentProcessed).ToList();
            var cmds = new List<AgentCommand>();
            foreach (var line in fresh)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<AgentCommand>(line) is { } cmd) cmds.Add(cmd); }
                catch (Exception ex) { Diag.Log("Agent cmd parse skip: " + ex.Message); }
            }
            if (cmds.Count == 0)
            {
                // Blank/unparseable lines: consume them so they don't re-read every poll.
                _agentProcessed = complete;
                try { File.WriteAllText(AgentCursor, _agentProcessed.ToString()); } catch { }
                return;
            }

            // Source-changing ops first, then re-scan (so the agent's own session + any new root are
            // indexed), then the per-chat ops that need those sessions to exist.
            bool IsSourceOp(AgentCommand c) { var o = (c.op ?? "").Trim().ToLowerInvariant(); return o is "init" or "addsource"; }
            var results = new List<(string op, AgentCommandResult res)>();
            foreach (var c in cmds.Where(IsSourceOp)) results.Add((c.op, await _archive.ApplyAgentCommandAsync(c)));
            await SyncNowAsync(initial: false);
            foreach (var c in cmds.Where(c => !IsSourceOp(c))) results.Add((c.op, await _archive.ApplyAgentCommandAsync(c)));

            foreach (var (op, res) in results)
            {
                Diag.Log($"Agent op '{op}': ok={res.Ok} {res.Message}");
                try { File.AppendAllText(AgentOutbox, JsonSerializer.Serialize(new { op, ok = res.Ok, message = res.Message }) + "\n"); }
                catch { }
            }
            if (results.Count > 0) SyncStatus.Text = "Agent: " + results[^1].res.Message;
            SelectFirstSession();
            RenderCurrent();

            // Advance the cursor ONLY after the batch is applied + acked, so a crash/exception
            // before here reprocesses (ops are idempotent) instead of dropping commands forever.
            _agentProcessed = complete;
            try { File.WriteAllText(AgentCursor, _agentProcessed.ToString()); } catch { }
        }
        catch (Exception ex) { Diag.Log("Agent poll error " + ex); }
        finally { _agentBusy = false; }
    }

    // Settings panel: shows the folders being scanned + the protocol path to hand an agent.
    private UIElement AgentAccessPanel()
    {
        var stack = new StackPanel { Spacing = 10, MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(new TextBlock { Text = "Sources & agent access", Foreground = StrongBrush(), FontSize = 18, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = "These folders are scanned for chats. Point any Claude or Codex chat at the protocol file and it can set itself up — favorite itself, file itself into a project, or register a non-default chat folder.",
            Foreground = MutedBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var src in _archive.EffectiveSources())
            stack.Children.Add(SettingRow($"{src.Tool} sessions", src.Root));
        stack.Children.Add(SettingRow("Agent protocol", AgentDoc));
        stack.Children.Add(SettingRow("Agent inbox", AgentInbox));

        var copy = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Copy protocol path", HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(AgentDoc);
            Clipboard.SetContent(package);
            SyncStatus.Text = "Protocol path copied — paste it into a chat.";
        };
        stack.Children.Add(copy);
        return Card(stack);
    }

    private static string AgentProtocolDoc()
    {
        var inbox = AgentInbox;
        var outbox = AgentOutbox;
        return
$@"# Codex Local Retrieval — Agent Protocol

You are an AI agent (Claude or Codex) in a terminal. The human uses this app to index every
Claude/Codex chat on this machine. You can drive it by appending JSON commands (one per line) to:

  inbox:  {inbox}
  acks:   {outbox}

PowerShell:
  Add-Content -Path '{inbox}' -Value '{{""op"":""favorite"",""target"":""self"",""cwd"":""<YOUR-CWD>""}}' -Encoding utf8

Identify yourself: for per-chat ops pass ""target"":""self"" plus ""cwd"" = your working directory
(run `pwd`). The app resolves that to the newest session in that folder — which is you. Add
""tool"":""claude"" or ""codex"" to disambiguate.

Commands (one JSON object per line):
  {{""op"":""init""}}                                              register default Codex+Claude folders
  {{""op"":""addSource"",""tool"":""claude"",""root"":""<path>""}}     register a non-default chat folder
  {{""op"":""favorite"",""target"":""self"",""cwd"":""<cwd>""}}        pin this chat to the top
  {{""op"":""addToProject"",""project"":""X"",""target"":""self"",""cwd"":""<cwd>""}}   file into project X
  {{""op"":""rename"",""target"":""self"",""cwd"":""<cwd>"",""localName"":""..."",""canonicalName"":""...""}}

`canonicalName` also writes back to Codex's own thread title (shows in `codex resume`). Instead of
self+cwd you can target an exact chat with ""id"":""<session-id>"". Check the acks file for results.
";
    }
}
