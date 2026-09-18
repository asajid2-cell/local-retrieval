using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval_Native;

// In-app capture/automation harness. Enabled when CLR_CAPTURE=1. The app polls a
// JSON command file (request.json) in CLR_CAP_DIR; for each request it runs the
// steps (navigate / resize window / type / click / screenshot / dump layout) on the
// UI thread, self-captures via RenderTargetBitmap (no external window handle needed),
// dumps the visual tree with bounds, and writes result-<id>.json. This replaces the
// Playwright battery for this WinUI app and works with `dotnet watch` live reload.
public sealed partial class MainPage
{
    private static string CapDir =>
        Environment.GetEnvironmentVariable("CLR_CAP_DIR") ?? Path.Combine(Path.GetTempPath(), "clrcap");

    private long _lastCapReqId = -1;
    private bool _capBusy;
    // Held in a field so the timer isn't garbage-collected (a local ref would stop ticking).
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _capTimer;

    private void StartCaptureHarness()
    {
        if (Environment.GetEnvironmentVariable("CLR_CAPTURE") != "1" || _capTimer is not null) return;
        try { Directory.CreateDirectory(Path.Combine(CapDir, "out")); } catch { }
        Diag.Log("Capture harness ENABLED, dir=" + CapDir);
        _capTimer = DispatcherQueue.CreateTimer();
        _capTimer.Interval = TimeSpan.FromMilliseconds(350);
        _capTimer.Tick += async (_, _) => await PollCaptureAsync();
        _capTimer.Start();
        try { File.WriteAllText(Path.Combine(CapDir, "ready.txt"), DateTime.Now.ToString("o")); } catch { }
    }

    private void StopCaptureHarness()
    {
        _capTimer?.Stop();
        _capTimer = null;
        _capBusy = false;
    }

    private async Task PollCaptureAsync()
    {
        if (_capBusy) return;
        var reqPath = Path.Combine(CapDir, "request.json");
        if (!File.Exists(reqPath)) return;
        CapRequest? req;
        try { req = JsonSerializer.Deserialize<CapRequest>(File.ReadAllText(reqPath)); }
        catch { return; }
        if (req is null || req.id == _lastCapReqId) return;

        _capBusy = true;
        _lastCapReqId = req.id;
        var artifacts = new List<string>();
        var errors = new List<string>();
        try
        {
            Diag.Log($"Capture req {req.id} ({req.steps?.Count ?? 0} steps)");
            foreach (var step in req.steps ?? new List<CapStep>())
            {
                try { await RunStepAsync(step, artifacts); }
                catch (Exception ex)
                {
                    errors.Add(step + ": " + ex.Message);
                    Diag.Log("step error " + ex);
                    break;
                }
            }
        }
        finally
        {
            var res = JsonSerializer.Serialize(new { id = req.id, ok = errors.Count == 0, screen = _screen, artifacts, errors });
            try { File.WriteAllText(Path.Combine(CapDir, $"result-{req.id}.json"), res); } catch { }
            Diag.Log($"Capture req {req.id} DONE ({artifacts.Count} artifacts, {errors.Count} errors)");
            _capBusy = false;
        }
    }

    private async Task RunStepAsync(CapStep step, List<string> artifacts)
    {
        if (GuiVerificationFixture.Enabled)
        {
            if (step.nav is not (null or "Archive") || step.click is not null || step.clipboard is true)
                throw new InvalidOperationException("GUI metadata fixture permits read-only capture steps only");
            foreach (var name in new[] { step.shot, step.dump, step.snapshot })
                if (name is not null && !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z0-9_-]{1,80}\z"))
                    throw new InvalidOperationException("GUI fixture artifact name refused");
        }
        if (step.nav is not null) Navigate(step.nav);
        if (step.resize is { Length: 2 }) MainWindow.Instance?.AppWindow.Resize(new Windows.Graphics.SizeInt32(step.resize[0], step.resize[1]));
        if (step.type is not null) { SearchBox.Text = step.type; ApplySearch(step.type); }
        if (step.click is not null)
        {
            InvokeByText(step.click, step.expectedSessionId);
        }
        else if (step.expectedSessionId is not null)
        {
            RequireExpectedSelection(step.expectedSessionId, null);
        }
        if (step.clipboard is true
            && step.click is not ("CopyGatewayCommandButton" or "Copy Gateway command"))
            throw new InvalidOperationException("Clipboard snapshot requires a CopyGatewayCommandButton click in the same step.");
        if (step.selectSessionId is not null) SelectDisplayedSession(step.selectSessionId);
        if (step.waitMs is not null)
        {
            if (step.waitMs is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(step.waitMs), "waitMs must be between 0 and 10000 milliseconds.");
            await Task.Delay(step.waitMs.Value);
        }
        await SettleAsync(step.wait ?? 3);
        if (step.shot is not null) artifacts.Add(await CaptureAsync(step.shot));
        if (step.dump is not null) artifacts.Add(DumpTree(step.dump));
        if (step.snapshot is not null) artifacts.Add(WriteStateSnapshot(step.snapshot));
        if (step.clipboard is true) artifacts.Add(await WriteClipboardSnapshotAsync());
    }

    private void SelectDisplayedSession(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("selectSessionId must not be empty.", nameof(id));

        for (var index = 0; index < SessionList.Items.Count; index++)
        {
            if (SessionList.Items[index] is not ArchiveSession session
                || !string.Equals(session.Id, id, StringComparison.OrdinalIgnoreCase)) continue;

            var container = SessionList.ContainerFromIndex(index) as ListViewItem;
            if (container is null) throw new InvalidOperationException($"Session '{id}' is not displayed.");
            var peer = new ListViewItemDataAutomationPeer(session, new ListViewAutomationPeer(SessionList));
            if (peer.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection)
                throw new InvalidOperationException("Displayed session item has no selection automation pattern.");

            selection.Select();
            if (SessionList.SelectedItem is not ArchiveSession selected
                || !string.Equals(selected.Id, id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Session selection did not settle on '{id}'.");
            return;
        }

        throw new InvalidOperationException($"Displayed session '{id}' was not found.");
    }

    private string WriteStateSnapshot(string name)
    {
        var selectedItemId = (SessionList.SelectedItem as ArchiveSession)?.Id;
        var path = Path.Combine(CapDir, "out", name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            selectedId = _selected?.Id,
            selectedItemId,
            renderedTitle = TitleText.Text,
            screen = _screen,
            syncInProgress = _syncing,
            count = _archive.Sessions.Count
        }));
        return path;
    }

    private async Task<string> WriteClipboardSnapshotAsync()
    {
        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (content is null || !content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            throw new InvalidOperationException("Clipboard does not contain text after the copy action.");

        var text = await content.GetTextAsync();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("Clipboard text is absent after the copy action.");

        var path = Path.Combine(CapDir, "out", "clipboard-command.txt");
        File.WriteAllText(path, text);
        return path;
    }

    // Yield enough UI-thread frames for layout/render to settle before capturing.
    private async Task SettleAsync(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            var tcs = new TaskCompletionSource();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.SetResult());
            await tcs.Task;
        }
        await Task.Delay(120);
    }

    private async Task<string> CaptureAsync(string name)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(this);
        var pixels = await rtb.GetPixelsAsync();
        var path = Path.Combine(CapDir, "out", name + ".png");
        using var fs = new FileStream(path, FileMode.Create);
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
        await enc.FlushAsync();
        return path;
    }

    // Walk the visual tree and record each element's type/name/bounds/visibility,
    // so the external harness can run ui-craft layout checks (offscreen / zero-size /
    // overlap / clipped) without an external automation client.
    private string DumpTree(string name)
    {
        var elements = new List<object>();
        void Walk(DependencyObject d, int depth)
        {
            if (d is FrameworkElement fe)
            {
                try
                {
                    var p = fe.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
                    elements.Add(new
                    {
                        type = d.GetType().Name,
                        name = fe.Name,
                        x = Math.Round(p.X, 1),
                        y = Math.Round(p.Y, 1),
                        w = Math.Round(fe.ActualWidth, 1),
                        h = Math.Round(fe.ActualHeight, 1),
                        vis = fe.Visibility.ToString(),
                        depth
                    });
                }
                catch { }
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), depth + 1);
        }
        Walk(this, 0);
        var path = Path.Combine(CapDir, "out", name + ".json");
        try { File.WriteAllText(path, JsonSerializer.Serialize(new { window = new { w = ActualWidth, h = ActualHeight }, screen = _screen, elements })); } catch { }
        return path;
    }

    private void RequireExpectedSelection(string? expectedSessionId, string? action)
    {
        var isReclaim = action?.Contains("reclaim", StringComparison.OrdinalIgnoreCase) == true;
        if (isReclaim && string.IsNullOrWhiteSpace(expectedSessionId))
            throw new InvalidOperationException("Reclaim requires expectedSessionId.");
        if (expectedSessionId is null) return;
        if (string.IsNullOrWhiteSpace(expectedSessionId))
            throw new InvalidOperationException("expectedSessionId must not be empty.");
        var selectedId = _selected?.Id;
        var selectedItemId = (SessionList.SelectedItem as ArchiveSession)?.Id;
        if (!string.Equals(selectedId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(selectedItemId, expectedSessionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected selected session '{expectedSessionId}', actual _selected='{selectedId}', SelectedItem='{selectedItemId}'.");
    }

    private void InvokeByText(string text, string? expectedSessionId)
    {
        RequireExpectedSelection(expectedSessionId, text);
        if (text.Contains("launch", StringComparison.OrdinalIgnoreCase)
            || text.Contains("resume", StringComparison.OrdinalIgnoreCase)
            || text.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || text.Contains("terminate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("kill", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Destructive capture action '{text}' is not allowlisted.");

        Button? unavailable = null;
        Button? Find(DependencyObject d)
        {
            if (d is Button b)
            {
                var caption = b.Content as string ?? ButtonText(b);
                var matches = b.Name == text || caption == text
                    || (text.Contains("reclaim", StringComparison.OrdinalIgnoreCase)
                        && (b.Name.Contains("reclaim", StringComparison.OrdinalIgnoreCase)
                            || caption?.Contains("reclaim", StringComparison.OrdinalIgnoreCase) == true));
                if (matches)
                {
                    if (b.IsEnabled && b.Visibility == Visibility.Visible && b.ActualWidth > 0 && b.ActualHeight > 0)
                        return b;
                    unavailable ??= b;
                }
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var result = Find(VisualTreeHelper.GetChild(d, i));
                if (result is not null) return result;
            }
            return null;
        }

        var btn = Find(this);
        if (btn is null)
        {
            if (unavailable is not null) throw new InvalidOperationException($"Button '{text}' is unavailable.");
            throw new InvalidOperationException($"Button '{text}' was not found.");
        }
        if (new ButtonAutomationPeer(btn).GetPattern(PatternInterface.Invoke) is not IInvokeProvider inv)
            throw new InvalidOperationException($"Button '{text}' has no invoke automation pattern.");
        inv.Invoke();
    }


    private static string? ButtonText(Button b)
    {
        string? found = null;
        void W(DependencyObject d)
        {
            if (found is not null) return;
            if (d is TextBlock t) { found = t.Text; return; }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) W(VisualTreeHelper.GetChild(d, i));
        }
        if (b.Content is DependencyObject dobj) W(dobj);
        return found;
    }
}

internal sealed class CapRequest
{
    public long id { get; set; }
    public List<CapStep>? steps { get; set; }
}

internal sealed class CapStep
{
    public string? nav { get; set; }
    public int[]? resize { get; set; }
    public string? type { get; set; }
    public string? click { get; set; }
    public string? expectedSessionId { get; set; }
    public string? selectSessionId { get; set; }
    public string? shot { get; set; }
    public string? dump { get; set; }
    public string? snapshot { get; set; }
    public bool? clipboard { get; set; }
    public int? wait { get; set; }
    public int? waitMs { get; set; }
    public override string ToString() => JsonSerializer.Serialize(this);
}
