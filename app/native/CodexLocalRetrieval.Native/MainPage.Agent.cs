using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
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
    // The cursor is a BYTE OFFSET (with a line counter riding along so acks keep quoting absolute line
    // numbers). That is what makes a poll cost the size of the append instead of the size of the inbox.
    private AppendCursorState _agentCursor = AppendCursorState.Zero;
    private bool _agentBusy;
    private IFileWatchRegistration? _agentWatch;
    private sealed record PendingAgentLine(long LineNumber, AgentCommand? Command, string? ParseError);

    private static string AgentDir =>
        GuiVerificationFixture.Enabled ? Path.Combine(GuiVerificationFixture.Root, "agent") :
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
            // Migrates a v1 line-count cursor left by an older build exactly once, in place.
            _agentCursor = AppendCursor.Load(AgentCursor, AgentInbox);
        }
        catch (Exception ex) { Diag.Log("Agent bridge init failed " + ex); return; }

        // The inbox is now read when an agent WRITES to it, not on a blind 1.5 s beat that re-read the
        // whole file. FileWatchService keeps a fallback poll behind the event, so a volume that drops
        // change notifications still drains the inbox on its own.
        try
        {
            _agentWatch = FileWatch.WatchFile(AgentInbox, () =>
                DispatcherQueue.TryEnqueue(async () => await PollAgentInboxAsync()));
        }
        catch (Exception ex) { Diag.Log("Agent inbox watch failed " + ex); }
        Diag.Log("Agent bridge ENABLED, inbox=" + AgentInbox);

        // Durable queue: commands an agent appended while the app was CLOSED sit in the inbox past the
        // saved cursor, so they resolve (and get acked) on the next open. Drain them as soon as the
        // store is ready instead of waiting a full poll tick, so reopening the app catches up instantly.
        DispatcherQueue.TryEnqueue(async () =>
        {
            for (var i = 0; i < 60 && !_storeLoaded; i++) await Task.Delay(100);
            await PollAgentInboxAsync();
        });
    }

    private async Task PollAgentInboxAsync()
    {
        if (_agentBusy || !_storeLoaded || _syncing)
        {
            // Declined, not consumed — make the watcher re-deliver instead of treating this as handled.
            _agentWatch?.Rearm();
            return;
        }
        if (!File.Exists(AgentInbox)) return;

        // Read ONLY the bytes appended past the cursor, and do it off the UI thread. ReadNewLines
        // returns complete lines only, so a half-written tail is left for the next read; if the inbox
        // is now shorter than the cursor it was reset, and Restarted says the counters restarted at 0.
        var cursor = _agentCursor;
        AppendReadResult read;
        try { read = await Task.Run(() => AppendCursor.ReadNewLines(AgentInbox, cursor)); }
        catch (Exception ex) { Diag.Log("Agent inbox read error " + ex.Message); return; }

        var next = new AppendCursorState(read.NextOffset, read.NextLine);
        if (read.Lines.Count == 0)
        {
            if (read.Restarted || next != _agentCursor) { _agentCursor = next; AppendCursor.Save(AgentCursor, next); }
            return;
        }

        _agentBusy = true;
        try
        {
            var pending = new List<PendingAgentLine>();
            for (var i = 0; i < read.Lines.Count; i++)
            {
                var line = read.Lines[i];
                var lineNumber = read.NextLine - read.Lines.Count + i + 1;   // absolute, as the acks quote it
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    pending.Add(JsonSerializer.Deserialize<AgentCommand>(line) is { } cmd
                        ? new PendingAgentLine(lineNumber, cmd, null)
                        : new PendingAgentLine(lineNumber, null, "Invalid agent command JSON."));
                }
                catch (Exception ex)
                {
                    Diag.Log("Agent cmd parse error: " + ex.Message);
                    pending.Add(new PendingAgentLine(lineNumber, null, "Invalid agent command JSON: " + ex.Message));
                }
            }
            if (pending.Count == 0)
            {
                // Blank lines: consume them so they don't re-read every poll.
                _agentCursor = next;
                AppendCursor.Save(AgentCursor, next);
                return;
            }

            // Source-changing ops first, then re-scan (so the agent's own session + any new root are
            // indexed), then the per-chat ops that need those sessions to exist.
            bool IsSourceOp(AgentCommand c) => ArchiveService.NormalizeAgentOp(c.op) is "init" or "addsource";
            var results = new List<(PendingAgentLine item, AgentCommandResult res)>();
            foreach (var item in pending.Where(p => p.ParseError is not null))
                results.Add((item, new AgentCommandResult(false, item.ParseError!)));

            var commandLines = pending.Where(p => p.Command is not null).ToList();

            // /tomux: hand a live LOCAL session off to multiplex (start it there, then stop the local copy).
            // Needs the Native mux-start + kill, so it's handled here rather than in Core's ApplyAgentCommand.
            bool IsToMux(AgentCommand c) => ArchiveService.NormalizeAgentOp(c.op) == "tomux";
            foreach (var item in commandLines.Where(p => IsToMux(p.Command!)))
                results.Add((item, await HandleToMuxAsync(item.Command!)));
            commandLines = commandLines.Where(p => !IsToMux(p.Command!)).ToList();

            bool IsMirrorLocal(AgentCommand c) => ArchiveService.NormalizeAgentOp(c.op) == "mirrorlocal";
            foreach (var item in commandLines.Where(p => IsMirrorLocal(p.Command!)))
                results.Add((item, await HandleMirrorLocalAsync(item.Command!)));
            commandLines = commandLines.Where(p => !IsMirrorLocal(p.Command!)).ToList();

            foreach (var item in commandLines.Where(p => IsSourceOp(p.Command!)))
                results.Add((item, await _archive.ApplyAgentCommandAsync(item.Command!)));

            if (commandLines.Count > 0) await SyncNowAsync(initial: false);

            foreach (var item in commandLines.Where(p => !IsSourceOp(p.Command!)))
                results.Add((item, await _archive.ApplyAgentCommandAsync(item.Command!)));

            foreach (var (item, res) in results)
            {
                var cmd = item.Command;
                var op = cmd is null ? "parse" : ArchiveService.NormalizeAgentOp(cmd.op);
                Diag.Log($"Agent op '{op}' line {item.LineNumber}: ok={res.Ok} {res.Message}");
                try
                {
                    File.AppendAllText(AgentOutbox, JsonSerializer.Serialize(new
                    {
                        requestId = cmd?.requestId,
                        line = item.LineNumber,
                        op,
                        ok = res.Ok,
                        message = res.Message,
                        inputId = res.InputId,
                        resolvedSessionId = res.ResolvedSessionId,
                        project = res.Project,
                        persisted = res.Persisted
                    }) + "\n");
                }
                catch { }
            }
            if (results.Count > 0) SyncStatus.Text = "Agent: " + results[^1].res.Message;
            // Agent commands mutate metadata in the background; they must not navigate away from the
            // chat the user is reading. ReapplyActiveFilter/SyncNowAsync preserve the selected id.
            RenderCurrent();

            // Advance the cursor ONLY after the batch is applied + acked, so a crash/exception
            // before here reprocesses (ops are idempotent) instead of dropping commands forever.
            _agentCursor = next;
            AppendCursor.Save(AgentCursor, next);
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

        // Optional resume launch arguments — inserted right after the CLI when you Resume a chat in a
        // terminal. e.g. set Codex to "--profile http_sse" to force the stable HTTP/SSE transport (the
        // websocket transport drops long Windows sessions). Blank = the default launch is untouched.
        stack.Children.Add(new TextBlock { Text = "Resume launch arguments (optional)", Foreground = StrongBrush(), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        stack.Children.Add(new TextBlock
        {
            Text = "Inserted right after the CLI when you Resume a chat in a terminal. Example: Codex “--profile http_sse” launches \"codex --profile http_sse resume …\" so the session rides the stable HTTP/SSE transport. Leave blank for the default.",
            Foreground = MutedBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(new TextBlock { Text = "Codex", Foreground = StrongBrush() });
        var codexArgs = new TextBox { Text = _archive.Store.Settings.CodexLaunchArgs, Width = 360, CornerRadius = ControlCornerRadius(), PlaceholderText = "--profile http_sse" };
        codexArgs.LostFocus += async (_, _) =>
        {
            _archive.Store.Settings.CodexLaunchArgs = (codexArgs.Text ?? "").Trim();
            await _archive.SaveAsync();
            SyncStatus.Text = "Codex launch args saved.";
        };
        stack.Children.Add(codexArgs);

        stack.Children.Add(new TextBlock { Text = "Claude", Foreground = StrongBrush() });
        var claudeArgs = new TextBox { Text = _archive.Store.Settings.ClaudeLaunchArgs, Width = 360, CornerRadius = ControlCornerRadius() };
        claudeArgs.LostFocus += async (_, _) =>
        {
            _archive.Store.Settings.ClaudeLaunchArgs = (claudeArgs.Text ?? "").Trim();
            await _archive.SaveAsync();
            SyncStatus.Text = "Claude launch args saved.";
        };
        stack.Children.Add(claudeArgs);

        return Card(stack);
    }

    private static string AgentProtocolDoc()
    {
        var inbox = AgentInbox;
        var outbox = AgentOutbox;
        return
$@"# MUX - Agent Protocol

You are an AI agent (Claude or Codex) in a terminal. The human uses this app to index every
Claude/Codex chat on this machine. You can drive it by appending JSON commands (one per line) to:

  inbox:  {inbox}
  acks:   {outbox}

IDENTIFY YOURSELF BY RUNTIME ID. Your strongest identity is your live session id, which is in your
environment:
  - Codex:  $env:CODEX_THREAD_ID
  - Claude: $env:CLAUDE_CODE_SESSION_ID
Pass it as ""id"" (plus ""tool"":""codex""|""claude""). The app resolves that runtime id to the
stored chat key using exact aliases from the transcript header/path, including resumed/forked Codex
ids. It never falls back to a different chat when an id is unknown. Only if your runtime truly has
no session-id variable, use ""target"":""self"" with your real ""cwd"" and ""tool"".

Use a requestId so you can find the matching ack instead of guessing from the outbox tail.

PowerShell (Codex example):
  $rid = [guid]::NewGuid().ToString()
  $cmd = @{{op=""addSelfToProject"";project=""X"";id=$env:CODEX_THREAD_ID;tool=""codex"";requestId=$rid}} | ConvertTo-Json -Compress
  Add-Content -Path '{inbox}' -Value $cmd -Encoding utf8

Commands (one JSON object per line):
  {{""op"":""init"",""requestId"":""...""}}                                                       register default Codex+Claude folders
  {{""op"":""addSource"",""tool"":""claude"",""root"":""<path>"",""requestId"":""...""}}              register a non-default chat folder
  {{""op"":""favorite"",""id"":""<runtime-id>"",""tool"":""codex"",""requestId"":""...""}}            pin this chat to the top
  {{""op"":""bump"",""id"":""<runtime-id>"",""tool"":""codex"",""requestId"":""...""}}                float this chat to the top of your own resume list
  {{""op"":""addSelfToProject"",""project"":""X"",""deck"":""<deck name, default Main>"",""name"":""<optional>"",""id"":""<runtime-id>"",""tool"":""codex"",""requestId"":""...""}} file into project X on a deck
  {{""op"":""setName"",""name"":""<in-app name>"",""id"":""<runtime-id>"",""tool"":""codex"",""requestId"":""...""}}  set this chat's app-only name (no project needed)
  {{""op"":""tag"",""tags"":[""bug"",""urgent""],""id"":""<runtime-id>"",""tool"":""codex"",""requestId"":""...""}}     add app-only tags to this chat (untag removes)
  {{""op"":""rename"",""id"":""<runtime-id>"",""tool"":""codex"",""localName"":""..."",""canonicalName"":""..."",""requestId"":""...""}}
  {{""op"":""tomux"",""id"":""<runtime-id>"",""tool"":""claude"",""pid"":<your host agent pid>,""name"":""<optional tab name>"",""requestId"":""...""}}  HAND THIS SESSION OFF TO MULTIPLEX: resumes it as a muxd-hosted tab (drivable from the web, tinted 'remote-resumed'), then STOPS this local copy. Pass ""pid"" = the claude.exe/codex.exe host pid so the local kill is reliable.
  {{""op"":""stash"",""name"":""<app name>"",""collection"":""<collection>"",""deck"":""<deck, default Main>"",""phrase"":""<codename e.g. petunia>"",""id"":""<runtime-id>"",""tool"":""claude"",""requestId"":""...""}}  STASH this chat: set its app-only name, file it into a collection on a deck, AND tag it with a searchable CODENAME (""special phrase""). Any subset of name/collection/phrase works. Searching that phrase in the app later surfaces EVERY chat stashed under it — so many chats can live under one codename.

SPECIAL PHRASES (codenames): app-only searchable labels a chat carries — several per chat, and many chats
can share one. They are folded into the app's search text, so typing the phrase in the search box (or deep
search) brings up every chat stashed under it. They are NEVER written into your transcript.

`addToProject` and `addToCollection` are accepted as legacy aliases for `addSelfToProject`.
`setName`/`name`/`label` are aliases for `rename`. The optional ""name"" on addSelfToProject (or
setName) sets this chat's APP-ONLY name - it never changes your global/native title.
`canonicalName` on rename also writes back to Codex's own thread title (shows in `codex resume`).

Each ack echoes requestId, line, op, inputId, resolvedSessionId, project, and persisted. Treat
`ok:true` plus `persisted:true` as success for project filing. Acks file: {outbox}
";
    }
}
