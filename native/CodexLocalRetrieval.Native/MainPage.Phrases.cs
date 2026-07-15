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
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        top.Children.Add(new TextBlock
        {
            Text = "Every special phrase",
            Foreground = StrongBrush(),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var start = new Button { Content = "Start chat", Style = (Style)Resources["PrimaryPillButtonStyle"] };
        start.Click += async (_, _) => await StartChatAsync();
        Grid.SetColumn(start, 1);
        top.Children.Add(start);
        MainContent.Children.Add(top);

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
                "No phrases yet",
                "Start a chat and give it a special phrase. Chats sharing that phrase stay grouped here."));
            return;
        }

        foreach (var group in groups)
            MainContent.Children.Add(PhraseGroup(group.Phrase, group.Sessions));
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
