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
        if (Environment.GetEnvironmentVariable("CLR_CAPTURE") != "1") return;
        try { Directory.CreateDirectory(Path.Combine(CapDir, "out")); } catch { }
        Diag.Log("Capture harness ENABLED, dir=" + CapDir);
        _capTimer = DispatcherQueue.CreateTimer();
        _capTimer.Interval = TimeSpan.FromMilliseconds(350);
        _capTimer.Tick += async (_, _) => await PollCaptureAsync();
        _capTimer.Start();
        try { File.WriteAllText(Path.Combine(CapDir, "ready.txt"), DateTime.Now.ToString("o")); } catch { }
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
                catch (Exception ex) { errors.Add(step + ": " + ex.Message); Diag.Log("step error " + ex); }
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
        if (step.nav is not null) Navigate(step.nav);
        if (step.resize is { Length: 2 }) MainWindow.Instance?.AppWindow.Resize(new Windows.Graphics.SizeInt32(step.resize[0], step.resize[1]));
        if (step.type is not null) { SearchBox.Text = step.type; ApplySearch(step.type); }
        if (step.click is not null) InvokeByText(step.click);
        await SettleAsync(step.wait ?? 3);
        if (step.shot is not null) artifacts.Add(await CaptureAsync(step.shot));
        if (step.dump is not null) artifacts.Add(DumpTree(step.dump));
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

    private void InvokeByText(string text)
    {
        Button? Find(DependencyObject d)
        {
            if (d is Button b && (b.Name == text || (b.Content as string) == text || ButtonText(b) == text)) return b;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) { var r = Find(VisualTreeHelper.GetChild(d, i)); if (r is not null) return r; }
            return null;
        }
        var btn = Find(this);
        if (btn is not null && new ButtonAutomationPeer(btn).GetPattern(PatternInterface.Invoke) is IInvokeProvider inv)
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
    public string? shot { get; set; }
    public string? dump { get; set; }
    public int? wait { get; set; }
    public override string ToString() => JsonSerializer.Serialize(this);
}
