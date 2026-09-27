using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    private void RenderPhrases()
    {
        ScreenLabel.Text = "Exact handles for related chats";
        TitleText.Text = "Phrases";
        MainContent.Children.Clear();

        var top = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };
        top.Children.Add(new TextBlock
        {
            Text = "Every special phrase",
            Foreground = StrongBrush(),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        // The retirement is offered only while old-scheme handles remain, and retired once and for all:
        // after it, the list below holds nothing and the button is gone.
        var oldHandles = _archive.Store.Sessions.Values
            .SelectMany(s => s.SpecialPhrases)
            .Where(p => !string.IsNullOrWhiteSpace(p) && !CodexLocalRetrieval.Core.Services.PhraseGenerator.IsGenerated(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (oldHandles > 0)
        {
            var retire = new Button
            {
                Content = $"Retire {oldHandles} old phrase{(oldHandles == 1 ? "" : "s")}",
                Style = (Style)Resources["PillButtonStyle"]
            };
            ToolTipService.SetToolTip(retire,
                "Park the phrases from the old naming scheme in a kept list, so the chats start clean under the new scheme.");
            retire.Click += async (_, _) => await RetireOldPhrasesAsync(oldHandles);
            Grid.SetColumn(retire, 1);
            top.Children.Add(retire);
        }

        var start = new Button { Content = "Start chat", Style = (Style)Resources["PrimaryPillButtonStyle"] };
        start.Click += async (_, _) => await StartChatAsync();
        Grid.SetColumn(start, 2);
        top.Children.Add(start);
        MainContent.Children.Add(top);

        if (_archive.Store.LegacyPhrases.Count > 0) MainContent.Children.Add(LegacyPhrasesPanel());

        var groups = _archive.Store.Sessions.Values
            .Where(s => !s.Archived)
            .SelectMany(s => s.SpecialPhrases
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => new { Phrase = p.Trim(), Session = s }))
            .GroupBy(x => x.Phrase, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Phrase = g.OrderBy(x => x.Phrase, StringComparer.Ordinal).First().Phrase,
                Sessions = g.Select(x => x.Session).DistinctBy(s => s.Id)
                    .OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal).ToList()
            })
            .OrderByDescending(g => g.Sessions.Max(s => s.UpdatedAt), StringComparer.Ordinal)
            .ThenBy(g => g.Phrase, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0)
        {
            MainContent.Children.Add(EmptyBlock(
                "No live phrases yet",
                "Vet a chat from the list (right-click it, then Vet chat) and give it a phrase. Chats sharing a phrase stay grouped here."));
            return;
        }

        foreach (var group in groups)
            MainContent.Children.Add(PhraseGroup(group.Phrase, group.Sessions));
    }

    // Retiring rewrites the phrase metadata of every chat carrying an old handle, so it is confirmed first
    // and the result names the backup copy the service takes before it touches anything.
    private async Task RetireOldPhrasesAsync(int count)
    {
        var dialog = new ContentDialog
        {
            Title = $"Retire {count} old phrase{(count == 1 ? "" : "s")}?",
            Content = new TextBlock
            {
                Text = "The phrases from the old naming scheme come off every chat they are on, and are kept "
                     + "in a list here with the chats each one covered.\n\nA copy of the app store is taken "
                     + "first. Phrases issued by vetting are left where they are.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Retire them",
            CloseButtonText = "Keep them",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // A retirement that could not be written must say so: an exception escaping here would leave the
        // phrases in place with nothing shown, which reads as the button having done nothing.
        try
        {
            var (phrases, chats, backup) = await _archive.DetachLegacyPhrasesAsync();
            UpdateChrome();
            RenderCurrent();
            SyncStatus.Text = phrases == 0
                ? "No old phrases were left to retire."
                : $"Retired {phrases} old phrase{(phrases == 1 ? "" : "s")} off {chats} chat{(chats == 1 ? "" : "s")}."
                  + (backup.Length == 0 ? " No backup copy could be taken." : $" Backup: {backup}");
        }
        catch (Exception ex)
        {
            RenderCurrent();
            SyncStatus.Text = "Could not retire the old phrases: " + ex.Message;
        }
    }

    // The retired handles, kept because a phrase-to-chats mapping is information worth having: you can
    // still see which chats a "petunia" covered, and put one back by hand from the backup if you want it.
    private UIElement LegacyPhrasesPanel()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = "Retired phrases",
            Foreground = StrongBrush(),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{_archive.Store.LegacyPhrases.Count} handles from the old scheme, with the chats each one covered.",
            Foreground = MutedBrush(),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var group in _archive.Store.LegacyPhrases
            .OrderByDescending(g => g.SessionIds.Count)
            .ThenBy(g => g.Phrase, StringComparer.OrdinalIgnoreCase))
        {
            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                ColumnSpacing = 8
            };
            row.Children.Add(new TextBlock
            {
                Text = group.Phrase,
                Foreground = StrongBrush(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var count = new Border
            {
                Background = AccentVerySoftBrush(),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = $"{group.SessionIds.Count} chat{(group.SessionIds.Count == 1 ? "" : "s")}",
                    Foreground = MutedBrush(),
                    FontSize = 11
                }
            };
            Grid.SetColumn(count, 1);
            row.Children.Add(count);
            stack.Children.Add(row);
        }
        return Card(stack);
    }

    private UIElement PhraseGroup(string phrase, IReadOnlyList<ArchiveSession> sessions)
    {
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };
        var exact = new Button
        {
            Content = phrase,
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Resources["PillButtonStyle"],
            FontWeight = FontWeights.SemiBold
        };
        ToolTipService.SetToolTip(exact, $"Search exactly [{phrase}]");
        exact.Click += (_, _) =>
        {
            SearchBox.Text = $"[{phrase}]";
            Navigate("Archive");
        };
        header.Children.Add(exact);

        var contexts = sessions
            .SelectMany(SessionCollectionNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var contextText = new TextBlock
        {
            Text = contexts.Count == 0 ? "Unfiled" : string.Join(" · ", contexts),
            Foreground = MutedBrush(),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300
        };
        Grid.SetColumn(contextText, 1);
        header.Children.Add(contextText);

        var count = new Border
        {
            Background = AccentVerySoftBrush(),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock
            {
                Text = $"{sessions.Count} chat{(sessions.Count == 1 ? "" : "s")}",
                Foreground = MutedBrush(),
                FontSize = 11
            }
        };
        Grid.SetColumn(count, 2);
        header.Children.Add(count);

        // The screen is a compact list of PHRASES; the chats under each stay collapsed until you open one.
        // Rows are built lazily on first expand, so a phrase with dozens of chats costs nothing until then.
        var content = new StackPanel { Spacing = 0 };
        var built = false;

        var expander = new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = PanelBrush(),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        expander.Expanding += (_, _) =>
        {
            if (built) return;
            built = true;
            foreach (var session in sessions.Take(200))
                content.Children.Add(SessionRow(session));
        };
        return expander;
    }

    private IEnumerable<string> SessionCollectionNames(ArchiveSession session)
    {
        var ids = new HashSet<string>(session.Aliases, StringComparer.OrdinalIgnoreCase) { session.Id };
        return _archive.Store.Collections.Values
            .Where(c => c.SessionIds.Any(ids.Contains))
            .Select(c =>
            {
                var deck = _archive.Decks.FirstOrDefault(d =>
                    string.Equals(d.Id, ArchiveService.CollectionDeck(c), StringComparison.OrdinalIgnoreCase))?.Name ?? "Main";
                return $"{deck} / {c.Name}";
            });
    }
}
