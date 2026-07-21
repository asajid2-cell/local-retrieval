using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CodexLocalRetrieval_Native;

// A small floating window the co-pilot opens to show one chat as an example. It is handed an
// immutable snapshot (role + cleaned text) so it never binds to the live, mutating archive.
public sealed class ChatPreviewWindow : Window
{
    public ChatPreviewWindow(string title, string subtitle, IReadOnlyList<(string Role, string Text)> messages)
    {
        Title = "Chat preview — " + title;
        ExtendsContentIntoTitleBar = false;

        var amoled = (SolidColorBrush)Application.Current.Resources["AmoledBrush"];
        var strong = (SolidColorBrush)Application.Current.Resources["TextStrongBrush"];
        var muted = (SolidColorBrush)Application.Current.Resources["TextMutedBrush"];
        var line = (SolidColorBrush)Application.Current.Resources["LineBrush"];
        var panel = (SolidColorBrush)Application.Current.Resources["PanelBrush"];
        var accentSoft = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 251, 113, 133));

        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(22) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = strong, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap });

        foreach (var (role, text) in messages.Take(80))
        {
            var isUser = role.Equals("user", System.StringComparison.OrdinalIgnoreCase) || role.Equals("you", System.StringComparison.OrdinalIgnoreCase);
            var bubble = new StackPanel { Spacing = 6 };
            bubble.Children.Add(new TextBlock { Text = role, FontSize = 11, Foreground = muted });
            bubble.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(text) ? "(no text)" : text, Foreground = strong, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
            stack.Children.Add(new Border
            {
                Background = isUser ? accentSoft : panel,
                BorderBrush = line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Child = bubble
            });
        }

        Content = new Border
        {
            Background = amoled,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack }
        };

        try { AppWindow.Resize(new SizeInt32(560, 760)); } catch { }
    }
}
