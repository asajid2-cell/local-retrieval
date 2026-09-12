using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class SelectionRaceTests
{
    [TestMethod]
    public void NewerClickWinsWhenScanCompletes()
    {
        Assert.AreEqual("B", SelectionRaceGuard.Resolve("A", 7, 8, "B", new[] { "A", "B" }));
    }

    [TestMethod]
    public void NoInterveningClickRestoresCapturedSelection()
    {
        Assert.AreEqual("A", SelectionRaceGuard.Resolve("A", 7, 7, "A", new[] { "A", "B" }));
    }

    [TestMethod]
    public void QueuedFilterPreservesNewerSelection()
    {
        Assert.AreEqual("B", SelectionRaceGuard.Resolve("A", 7, 8, "B", new[] { "B", "C" }));
    }

    [TestMethod]
    public void QueuedFilterDoesNotChooseUnrelatedFirstRow()
    {
        Assert.AreEqual("B", SelectionRaceGuard.Resolve("A", 7, 8, "B", new[] { "C", "D" }));
    }

    [TestMethod]
    public void SameIdRebindRestoresCapturedSelection()
    {
        Assert.AreEqual("A", SelectionRaceGuard.Resolve("A", 7, 7, "A", new[] { "A" }));
    }

    [TestMethod]
    public void ActualClickRejectsCapturedSelectionEvenWhenIdIsUnchanged()
    {
        Assert.AreEqual("A", SelectionRaceGuard.Resolve("A", 7, 8, "A", new[] { "A", "B" }));
    }
}
