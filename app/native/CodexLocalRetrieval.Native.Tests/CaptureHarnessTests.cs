using System.IO;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class CaptureHarnessTests
{
    [TestMethod]
    public void CaptureHarness_UsesStrictButtonAndDisplayedSessionActions()
    {
        var source = File.ReadAllText(FindCaptureFile());

        StringAssert.Contains(source, "Button '{text}' was not found.");
        StringAssert.Contains(source, "Button '{text}' is unavailable.");
        StringAssert.Contains(source, "selectSessionId");
        StringAssert.Contains(source, "ContainerFromIndex");
        StringAssert.Contains(source, "PatternInterface.SelectionItem");
        StringAssert.Contains(source, "selection.Select()");
        Assert.IsFalse(source.Contains("_selected = session", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CaptureHarness_FailsFastAndFencesDestructiveSelection()
    {
        var source = File.ReadAllText(FindCaptureFile());

        StringAssert.Contains(source, "break;");
        StringAssert.Contains(source, "expectedSessionId");
        StringAssert.Contains(source, "Reclaim requires expectedSessionId.");
        StringAssert.Contains(source, "var selectedId = _selected?.Id;");
        StringAssert.Contains(source, "var selectedItemId = (SessionList.SelectedItem as ArchiveSession)?.Id;");
        StringAssert.Contains(source, "StringComparison.Ordinal)");
        StringAssert.Contains(source, "InvokeByText(step.click, step.expectedSessionId);");
        StringAssert.Contains(source, "RequireExpectedSelection(expectedSessionId, text);");
        StringAssert.Contains(source, "if (isReclaim && string.IsNullOrWhiteSpace(expectedSessionId))");
        StringAssert.Contains(source, "caption?.Contains(\"reclaim\", StringComparison.OrdinalIgnoreCase)");
        StringAssert.Contains(source, "unavailable ??= b;");

        var catchBlock = source.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var breakAfterCatch = source.IndexOf("break;", catchBlock, StringComparison.Ordinal);
        Assert.IsTrue(catchBlock >= 0 && breakAfterCatch > catchBlock,
            "A failed step must stop the request before the next step.");
        var nextStep = source.IndexOf("RunStepAsync(step, artifacts)", breakAfterCatch, StringComparison.Ordinal);
        Assert.IsTrue(nextStep < 0, "The loop must not invoke another step after the failure break.");
    }

    [TestMethod]
    public void CaptureHarness_BoundsWaitAndKeepsCalibrationFailuresVisible()
    {
        var source = File.ReadAllText(FindCaptureFile());

        StringAssert.Contains(source, "step.waitMs is < 0 or > 10000");
        StringAssert.Contains(source, "throw new InvalidOperationException($\"Button '{text}' was not found.\")");
        StringAssert.Contains(source, "ok = errors.Count == 0");
    }

    [TestMethod]
    public void CaptureHarness_ClipboardSnapshotReadsOnlyAfterTheCopyClick()
    {
        var source = File.ReadAllText(FindCaptureFile());

        StringAssert.Contains(source, "selectedId = _selected?.Id");
        StringAssert.Contains(source, "syncInProgress = _syncing");
        StringAssert.Contains(source, "InvokeByText(step.click, step.expectedSessionId);");
        StringAssert.Contains(source, "Button '{text}' was not found.");
        StringAssert.Contains(source, "Button '{text}' is unavailable.");
        StringAssert.Contains(source, "ButtonAutomationPeer");
        StringAssert.Contains(source, "PatternInterface.Invoke");
        StringAssert.Contains(source, "if (step.clipboard is true) artifacts.Add(await WriteClipboardSnapshotAsync());");
        StringAssert.Contains(source, "step.click");
        StringAssert.Contains(source, "Clipboard.GetContent()");
        StringAssert.Contains(source, "GetTextAsync()");
        Assert.IsFalse(source.Contains("ResumeCommandText", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Clipboard.SetContent", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("DataPackage", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("EnsureContent", StringComparison.Ordinal));

        var click = source.IndexOf("if (step.click is not null)", StringComparison.Ordinal);
        var snapshot = source.IndexOf("if (step.clipboard is true)", StringComparison.Ordinal);
        Assert.IsTrue(click >= 0 && snapshot > click, "The copy click must precede clipboard observation.");
    }

    private static string FindCaptureFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "..", "..", "..", "..", "CodexLocalRetrieval.Native", "MainPage.Capture.cs");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = dir.Parent;
        }

        var root = FindRepoRoot();
        return Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Capture.cs");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodexLocalRetrieval.Native.Tests.csproj")))
            dir = dir.Parent;
        return dir?.Parent?.FullName ?? throw new DirectoryNotFoundException("Native test project root not found.");
    }
}
