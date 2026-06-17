using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

// The co-pilot: a multi-turn chat over the archive backed by DeepSeek (key autograbbed from the
// environment) and the narrow ArchiveToolService. Mutations route through a confirm dialog; show_chat
// opens a floating preview window. Tool results are data — the system prompt forbids obeying them.
public sealed partial class MainPage
{
    private readonly List<ChatMessage> _copilotApi = new();
    private readonly List<Window> _previewWindows = new();
    private bool _copilotBusy;
    private string _copilotStatus = "";
    private string _copilotBackend = "deepseek"; // deepseek | claude-cli

    private static readonly string[] CopilotSuggestions =
    {
        "Summarize my most recent chat",
        "Find chats about VENPOD and list them",
        "Open my most recent chat in a preview window",
        "Resume my most recent chat in a terminal",
        "Make a restore packet for my latest chat"
    };

    private void ResetCopilot()
    {
        _copilotApi.Clear();
        _copilotApi.Add(ChatMessage.System(ArchiveToolService.SystemPrompt));
        _copilotStatus = "";
    }

    private IChatBackend? BuildCopilotBackend()
    {
        if (_copilotBackend == "claude-cli")
            return new ClaudexBackend(ArchiveService.ResolveClaudeExe());

        var provider = _archive.ActiveAiProvider()
                       ?? _archive.EnsureAiProvider("DeepSeek", "https://api.deepseek.com", "deepseek-v4-flash");
        var key = LoadApiKey(provider.Id);
        if (string.IsNullOrWhiteSpace(key)) return null;
        return new DeepSeekBackend(provider.BaseUrl, provider.Model, key);
    }

    private void RenderCopilot()
    {
        ScreenLabel.Text = "Co-pilot";
        TitleText.Text = "Chat about your archive";
        MainContent.Children.Clear();
        if (_copilotApi.Count == 0) ResetCopilot();

        MainContent.Children.Add(CopilotComposer());

        if (_copilotBusy) MainContent.Children.Add(CopilotNote("Thinking..."));
        if (!string.IsNullOrWhiteSpace(_copilotStatus)) MainContent.Children.Add(CopilotNote(_copilotStatus));

        // Newest exchange first, directly under the composer, so the latest reply is visible
        // without scrolling. System + tool messages are not shown; tool calls render as chips.
        var visible = _copilotApi.Where(m => m.Role is "user" or "assistant").ToList();
        for (var i = visible.Count - 1; i >= 0; i--)
        {
            var m = visible[i];
            if (m.Role == "user")
            {
                MainContent.Children.Add(CopilotBubble("You", m.Content ?? "", true));
            }
            else
            {
                if (m.ToolCalls is { Count: > 0 })
                    foreach (var call in m.ToolCalls) MainContent.Children.Add(ToolChip(call.Function.Name, call.Function.Arguments));
                if (!string.IsNullOrWhiteSpace(m.Content))
                    MainContent.Children.Add(CopilotBubble("Co-pilot", m.Content!, false));
            }
        }

        if (visible.Count == 0) MainContent.Children.Add(CopilotSuggestionsCard());
    }

    private UIElement CopilotComposer()
    {
        var input = new TextBox
        {
            PlaceholderText = "Ask about your chats, or tell me to organize them...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 160,
            CornerRadius = ControlCornerRadius(),
            IsEnabled = !_copilotBusy
        };
        input.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter &&
                (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0)
            {
                e.Handled = true;
                var text = input.Text;
                input.Text = "";
                await SendCopilotAsync(text);
            }
        };

        var send = new Button { Style = (Style)Resources["PrimaryPillButtonStyle"], Content = "Send", IsEnabled = !_copilotBusy };
        send.Click += async (_, _) => { var t = input.Text; input.Text = ""; await SendCopilotAsync(t); };

        var reset = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "New chat" };
        reset.Click += (_, _) => { ResetCopilot(); RenderCopilot(); };

        var backendCombo = new ComboBox { MinWidth = 140, CornerRadius = ControlCornerRadius(), VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_copilotBusy };
        backendCombo.Items.Add(new ComboBoxItem { Content = "DeepSeek", Tag = "deepseek" });
        backendCombo.Items.Add(new ComboBoxItem { Content = "Claude (CLI)", Tag = "claude-cli" });
        backendCombo.SelectedIndex = _copilotBackend == "claude-cli" ? 1 : 0;
        backendCombo.SelectionChanged += (_, _) =>
        {
            if (backendCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && tag != _copilotBackend)
            {
                _copilotBackend = tag;
                RenderCopilot();
            }
        };

        var note = new TextBlock
        {
            Text = _copilotBackend == "claude-cli"
                ? "Claude CLI — experimental, plain chat (no archive tools)"
                : "DeepSeek key: " + ApiKeySource("deepseek"),
            Foreground = MutedBrush(),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children = { backendCombo, note } };

        var buttons = new Grid { Margin = new Thickness(0, 10, 0, 0), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        buttons.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(reset);
        right.Children.Add(send);
        Grid.SetColumn(right, 1);
        buttons.Children.Add(right);

        return Card(new StackPanel { Spacing = 0, Children = { input, buttons } });
    }

    private UIElement CopilotSuggestionsCard()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Try asking", Foreground = StrongBrush(), FontSize = 15, FontWeight = FontWeights.SemiBold });
        foreach (var s in CopilotSuggestions)
        {
            var b = new Button { Style = (Style)Resources["PillButtonStyle"], Content = s, HorizontalAlignment = HorizontalAlignment.Left };
            b.Click += async (_, _) => await SendCopilotAsync(s);
            stack.Children.Add(b);
        }
        return Card(stack);
    }

    private async Task SendCopilotAsync(string text)
    {
        if (_copilotBusy || string.IsNullOrWhiteSpace(text)) return;
        if (_copilotApi.Count == 0) ResetCopilot();

        var backend = BuildCopilotBackend();
        if (backend is null)
        {
            _copilotStatus = "No DeepSeek key found. Set DEEPSEEK_API_KEY in your environment (or add a provider in Settings), then try again.";
            RenderCopilot();
            return;
        }

        _copilotApi.Add(ChatMessage.User(text.Trim()));
        _copilotBusy = true;
        _copilotStatus = "";
        RenderCopilot();

        try
        {
            var tools = new ArchiveToolService(_archive, showChat: OpenChatPreview, resumeChat: ResumeChatFromCopilot).Tools();
            var orchestrator = new ChatOrchestrator(backend, tools, confirm: ConfirmCopilotActionAsync);
            var result = await orchestrator.RunAsync(_copilotApi);
            if (string.IsNullOrWhiteSpace(result.Answer) && !string.IsNullOrWhiteSpace(result.Error))
                _copilotStatus = result.Error!;
        }
        catch (Exception ex)
        {
            Diag.Log("Copilot error " + ex);
            _copilotStatus = "Error talking to the model: " + ex.Message;
        }
        finally
        {
            _copilotBusy = false;
            RenderCopilot();
        }
    }

    private async Task<bool> ConfirmCopilotActionAsync(ChatTool tool, JsonElement args)
    {
        Diag.Log($"Copilot confirm requested for tool: {tool.Name} {args}");
        var dialog = new ContentDialog
        {
            Title = "Allow this action?",
            Content = $"The co-pilot wants to run \"{tool.Name}\":\n\n{args}\n\nThis changes app metadata only — your chat files are never modified.",
            PrimaryButtonText = "Allow",
            CloseButtonText = "Skip",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ResumeChatFromCopilot(string id)
    {
        var session = _archive.GetSession(id);
        if (session is not null) DispatcherQueue.TryEnqueue(() => ResumeInTerminal(session));
    }

    private void OpenChatPreview(string id)
    {
        var session = _archive.GetSession(id);
        if (session is null) return;
        var snapshot = session.Messages
            .Take(80)
            .Select(m => (m.RoleLabel, ArchiveService.ForReading(m.Text)))
            .ToList();
        var title = session.DisplayTitle;
        var subtitle = $"{(string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase) ? "Claude" : "Codex")} · {session.WorkspaceName}";
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var window = new ChatPreviewWindow(title, subtitle, snapshot);
                _previewWindows.Add(window);
                window.Closed += (_, _) => _previewWindows.Remove(window);
                window.Activate();
                Diag.Log($"Preview window opened for \"{title}\" ({_previewWindows.Count} open)");
            }
            catch (Exception ex) { Diag.Log("Preview window failed " + ex); }
        });
    }

    private UIElement CopilotBubble(string who, string text, bool isUser)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = who, Foreground = MutedBrush(), FontSize = 12 });
        if (isUser)
            stack.Children.Add(new TextBlock { Text = text, Foreground = StrongBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 22, FontSize = 14 });
        else
            stack.Children.Add(BuildMarkdown(text)); // assistant replies are markdown
        return new Border
        {
            Background = isUser ? AccentVerySoftBrush() : PanelBrush(),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = PanelCornerRadius(),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = stack
        };
    }

    private UIElement ToolChip(string name, string argsJson)
    {
        var text = string.IsNullOrWhiteSpace(argsJson) || argsJson == "{}" ? name : $"{name} {Trim(argsJson, 80)}";
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 120, 170, 255)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12, Glyph = "", Foreground = MutedBrush() },
                    new TextBlock { Text = text, Foreground = MutedBrush(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
    }

    private UIElement CopilotNote(string text) =>
        new TextBlock { Text = text, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0) };

    // Lightweight markdown -> RichTextBlock: headers, **bold**, `code`, bullets, and table rows
    // (kept monospaced). Robust to unclosed markers; not a full parser, just readable formatting.
    private RichTextBlock BuildMarkdown(string text)
    {
        var rtb = new RichTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = StrongBrush(), LineHeight = 22, FontSize = 14 };
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw;
            var para = new Microsoft.UI.Xaml.Documents.Paragraph();

            var t = line.Trim();
            if (t is "---" or "***" or "___")
            {
                para.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "──────────", Foreground = MutedBrush() });
                rtb.Blocks.Add(para);
                continue;
            }

            var hashes = 0;
            while (hashes < line.Length && line[hashes] == '#') hashes++;
            if (hashes > 0 && hashes < line.Length && line[hashes] == ' ')
            {
                line = line[(hashes + 1)..];
                para.FontWeight = FontWeights.SemiBold;
                para.FontSize = hashes <= 2 ? 16 : 15;
                para.Margin = new Thickness(0, 8, 0, 2);
            }

            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("- ") || trimmedStart.StartsWith("* "))
                line = "  •  " + trimmedStart[2..];

            var monospace = line.Contains('|'); // table row
            AddInlineRuns(para.Inlines, line, monospace);
            rtb.Blocks.Add(para);
        }
        return rtb;
    }

    private void AddInlineRuns(Microsoft.UI.Xaml.Documents.InlineCollection inlines, string line, bool monospace)
    {
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > 0) { inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line[(i + 2)..end], FontWeight = FontWeights.Bold }); i = end + 2; continue; }
            }
            if (line[i] == '`')
            {
                var end = line.IndexOf('`', i + 1);
                if (end > 0) { inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line[(i + 1)..end], FontFamily = new FontFamily("Consolas") }); i = end + 1; continue; }
            }
            var next = NextMarker(line, i);
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = line[i..next] };
            if (monospace) run.FontFamily = new FontFamily("Consolas");
            inlines.Add(run);
            i = next;
        }
    }

    private static int NextMarker(string s, int from)
    {
        for (var k = from + 1; k < s.Length; k++)
        {
            if (s[k] == '`') return k;
            if (s[k] == '*' && k + 1 < s.Length && s[k + 1] == '*') return k;
        }
        return s.Length;
    }
}
