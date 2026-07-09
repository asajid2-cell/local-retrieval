using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    private SessionIntegritySummary? _integritySummary;
    private string _integritySessionKey = "";
    private DateTimeOffset _integrityBuiltAt;

    private void RefreshIntegrity_Click(object sender, RoutedEventArgs e) => RenderIntegrity(force: true);

    private void RenderIntegrity(bool force = false)
    {
        IntegrityItems.Children.Clear();
        if (_selected is null)
        {
            SetRiskySessionActionsEnabled(false);
            IntegrityItems.Children.Add(new TextBlock { Text = "No chat selected", Foreground = MutedBrush(), FontSize = 12 });
            return;
        }

        var key = IntegrityKey(_selected);
        var stale = DateTimeOffset.UtcNow - _integrityBuiltAt > TimeSpan.FromSeconds(5);
        if (force || _integritySummary is null || !string.Equals(_integritySessionKey, key, StringComparison.Ordinal) || stale)
        {
            try
            {
                _integritySummary = SessionIntegrity.Build(_archive.Store, _selected);
                _integritySessionKey = key;
                _integrityBuiltAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                Diag.Log("RenderIntegrity failed: " + ex);
                _integritySummary = null;
                _integritySessionKey = "";
                SetRiskySessionActionsEnabled(false);
                IntegrityItems.Children.Add(IntegrityHeadline("danger", "Integrity checks failed. Refusing to treat this chat as clear."));
                return;
            }
        }

        var summary = _integritySummary;
        if (summary is null) return;
        SetRiskySessionActionsEnabled(!string.Equals(summary.Severity, "danger", StringComparison.OrdinalIgnoreCase));

        IntegrityItems.Children.Add(IntegrityHeadline(summary.Severity, summary.Headline));
        IntegrityItems.Children.Add(IntegrityMeta(summary));

        foreach (var check in summary.Checks)
            IntegrityItems.Children.Add(IntegrityCheckRow(check));

        if (summary.MuxTabs.Count > 0)
            IntegrityItems.Children.Add(IntegrityEvidenceBlock("Mux custody", summary.MuxTabs.Take(3).Select(t =>
                $"{t.Name}: {(t.IsCurrent ? "current" : "history")}{(string.IsNullOrWhiteSpace(t.Kind) ? "" : " - " + t.Kind)}")));

        if (summary.LaunchClaims.Count > 0)
            IntegrityItems.Children.Add(IntegrityEvidenceBlock("Launch claims", summary.LaunchClaims.Take(3).Select(c =>
                $"{(c.Expired ? "expired" : "active")} - {c.OwnerProcess} pid {c.OwnerPid}")));

        if (summary.PendingIntents.Count > 0)
            IntegrityItems.Children.Add(IntegrityEvidenceBlock("Pending filing", summary.PendingIntents.Take(3).Select(p =>
                $"{p.Tool} - {p.Workspace}")));

        if (summary.RecentEvents.Count > 0)
            IntegrityItems.Children.Add(IntegrityEvidenceBlock("Recent events", summary.RecentEvents.Take(4).Select(e =>
                $"{e.Kind}: {Trim(e.Summary, 92)}")));
    }

    private bool RiskySessionActionBlocked()
    {
        RenderIntegrity(force: true);
        return _integritySummary is null
               || string.Equals(_integritySummary.Severity, "danger", StringComparison.OrdinalIgnoreCase);
    }

    private void SetRiskySessionActionsEnabled(bool enabled)
    {
        ResumeTerminalButton.IsEnabled = enabled;
        ResumeMultiplexButton.IsEnabled = enabled;
        ResumeHeadlessMultiplexButton.IsEnabled = enabled;
        CopyCommandButton.IsEnabled = enabled;
    }

    private string IntegrityKey(ArchiveSession session)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length > 0) ids.Add(id);
        }
        Add(session.Id);
        foreach (var alias in session.Aliases) Add(alias);

        var collections = _archive.Store.Collections.Values
            .Where(c => c.SessionIds.Any(id => ids.Contains(id)))
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        var pending = _archive.Store.PendingNewChats.Count(p =>
            string.Equals(p.Tool, session.Tool, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeIntegrityPath(p.Cwd), NormalizeIntegrityPath(session.Workspace), StringComparison.OrdinalIgnoreCase));
        var mux = _archive.Store.MuxTabHistory
            .Where(kv => (kv.Value.Current is not null && ids.Contains(kv.Value.Current.Id))
                         || kv.Value.History.Any(h => ids.Contains(h.Id)))
            .Select(kv => kv.Key + ":" + (kv.Value.Current?.Id ?? "") + ":" + kv.Value.History.Count)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        return string.Join("|", new[]
        {
            session.Id,
            string.Join(",", session.Aliases.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)),
            session.SourcePath,
            session.UpdatedAt,
            string.Join(",", collections),
            pending.ToString(),
            string.Join(",", mux)
        });
    }

    private static string NormalizeIntegrityPath(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) return "";
        try { path = System.IO.Path.GetFullPath(path); } catch { }
        return path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    private Border IntegrityHeadline(string severity, string text)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                IntegrityDot(severity),
                new TextBlock
                {
                    Text = IntegrityLabel(severity),
                    Foreground = IntegrityBrush(severity),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        });
        stack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = StrongBrush(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        });

        return new Border
        {
            Background = IntegrityBackground(severity),
            BorderBrush = IntegrityBorderBrush(severity),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = stack
        };
    }

    private UIElement IntegrityMeta(SessionIntegritySummary summary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(IntegrityChip(summary.Tool.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "CL" : "CX"));
        row.Children.Add(IntegrityChip(summary.SourceStatus));
        row.Children.Add(IntegrityChip(summary.Collections.Count == 0 ? "unfiled" : summary.Collections.Count + " collection" + (summary.Collections.Count == 1 ? "" : "s")));
        return row;
    }

    private UIElement IntegrityCheckRow(SessionIntegrityCheck check)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 8
        };
        grid.Children.Add(IntegrityDot(check.Severity));
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = check.Name,
            Foreground = StrongBrush(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = check.Summary,
            Foreground = MutedBrush(),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private UIElement IntegrityEvidenceBlock(string title, IEnumerable<string> lines)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock { Text = title, Foreground = StrongBrush(), FontSize = 12, FontWeight = FontWeights.SemiBold });
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = MutedBrush(),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 15
            });
        }
        return new Border
        {
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 7, 9, 7),
            Child = stack
        };
    }

    private Border IntegrityChip(string text) => new()
    {
        BorderBrush = LineBrush(),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 2, 7, 2),
        Child = new TextBlock { Text = text, Foreground = MutedBrush(), FontSize = 10, FontWeight = FontWeights.SemiBold }
    };

    private UIElement IntegrityDot(string severity) => new FontIcon
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        Glyph = severity switch
        {
            "danger" => "\uE783",
            "warn" => "\uE7BA",
            "ok" => "\uE73E",
            _ => "\uE946"
        },
        FontSize = 12,
        Foreground = IntegrityBrush(severity),
        VerticalAlignment = VerticalAlignment.Center
    };

    private string IntegrityLabel(string severity) => severity switch
    {
        "danger" => "BLOCKED",
        "warn" => "REVIEW",
        "ok" => "CLEAR",
        _ => "UNKNOWN"
    };

    private SolidColorBrush IntegrityBrush(string severity) => severity switch
    {
        "danger" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFB, 0x71, 0x85)),
        "warn" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFB, 0xBF, 0x24)),
        "ok" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x34, 0xD3, 0x99)),
        _ => MutedBrush()
    };

    private SolidColorBrush IntegrityBorderBrush(string severity) => severity switch
    {
        "danger" => new SolidColorBrush(Windows.UI.Color.FromArgb(170, 0xFB, 0x71, 0x85)),
        "warn" => new SolidColorBrush(Windows.UI.Color.FromArgb(150, 0xFB, 0xBF, 0x24)),
        "ok" => new SolidColorBrush(Windows.UI.Color.FromArgb(130, 0x34, 0xD3, 0x99)),
        _ => LineBrush()
    };

    private SolidColorBrush IntegrityBackground(string severity) => severity switch
    {
        "danger" => new SolidColorBrush(Windows.UI.Color.FromArgb(26, 0xFB, 0x71, 0x85)),
        "warn" => new SolidColorBrush(Windows.UI.Color.FromArgb(24, 0xFB, 0xBF, 0x24)),
        "ok" => new SolidColorBrush(Windows.UI.Color.FromArgb(18, 0x34, 0xD3, 0x99)),
        _ => PanelBrush()
    };
}
