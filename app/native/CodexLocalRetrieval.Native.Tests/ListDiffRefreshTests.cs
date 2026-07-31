using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// ObservableDiff is the mechanism behind RefreshSessions no longer rebuilding the bound list. These
// tests are pure — no WinUI, no store — and they pin both halves of the contract: the notification
// COUNT (which is what the ListView pays for) and FUNCTIONAL EQUIVALENCE with a full rebuild (which is
// what stops the optimisation from being a bug).
[TestClass]
public sealed class ListDiffRefreshTests
{
    private sealed class Row(string id)
    {
        public string Id { get; } = id;
        public override string ToString() => Id;
    }

    private static ObservableCollection<Row> Collection(params string[] ids) =>
        new(ids.Select(i => new Row(i)));

    private static (int Ops, int Events, string Final) Run(ObservableCollection<Row> target, IReadOnlyList<Row> desired)
    {
        var events = 0;
        void Count(object? s, NotifyCollectionChangedEventArgs e) => events++;
        target.CollectionChanged += Count;
        int ops;
        try { ops = ObservableDiff.Apply(target, desired, r => r.Id); }
        finally { target.CollectionChanged -= Count; }
        return (ops, events, string.Join(",", target.Select(r => r.Id)));
    }

    [TestMethod]
    public void UnchangedList_RaisesNothing()
    {
        var target = Collection("a", "b", "c", "d");
        var desired = target.ToList();
        var (ops, events, final) = Run(target, desired);

        Assert.AreEqual(0, ops);
        Assert.AreEqual(0, events, "an unchanged list must not notify the UI at all");
        Assert.AreEqual("a,b,c,d", final);
    }

    [TestMethod]
    public void RemovingOneRow_RaisesOneEvent()
    {
        var target = Collection("a", "b", "c", "d");
        var desired = target.Where(r => r.Id != "c").ToList();
        var (_, events, final) = Run(target, desired);

        Assert.AreEqual(1, events, "one removal must cost one notification");
        Assert.AreEqual("a,b,d", final);
    }

    [TestMethod]
    public void InsertingOneRow_RaisesOneEvent()
    {
        var target = Collection("a", "b", "d");
        var desired = new List<Row> { target[0], target[1], new("c"), target[2] };
        var (_, events, final) = Run(target, desired);

        Assert.AreEqual(1, events);
        Assert.AreEqual("a,b,c,d", final);
    }

    [TestMethod]
    public void MovingOneRowToTheTop_RaisesOneEvent()
    {
        var target = Collection("a", "b", "c", "d");
        var desired = new List<Row> { target[2], target[0], target[1], target[3] };
        var (_, events, final) = Run(target, desired);

        Assert.AreEqual(1, events, "a pin/bump that floats one row must not rebuild the list");
        Assert.AreEqual("c,a,b,d", final);
    }

    // A genuinely different result set is cheaper as one Reset than as a long run of Moves, and the
    // differ is allowed to take that path — but it must still land on exactly the right contents.
    [TestMethod]
    public void WholesaleReplacement_FallsBackToResetButStaysCorrect()
    {
        var target = Collection("a", "b", "c", "d");
        var desired = new List<Row> { new("x"), new("y"), new("z") };
        var (_, events, final) = Run(target, desired);

        Assert.AreEqual("x,y,z", final);
        Assert.IsTrue(events <= 1 + desired.Count,
            $"a full replacement must not cost more than a rebuild; it cost {events}");
    }

    // The one way the target can end up LONGER than the desired list after the move pass: it already
    // held the same key twice. Nothing in the app produces that today, but the trim that handles it is
    // only trustworthy if something actually drives it.
    [TestMethod]
    public void TargetHoldingADuplicateKey_IsTrimmedToTheDesiredLength()
    {
        var dup = new Row("b");
        var target = new ObservableCollection<Row> { new("a"), dup, new("b"), new("c") };
        var desired = new List<Row> { target[0], dup, target[3] };

        var (_, _, final) = Run(target, desired);

        Assert.AreEqual("a,b,c", final, "the duplicate must be trimmed, not left dangling");
        Assert.AreEqual(3, target.Count);
    }

    // A re-parse or merge-scan can put a BRAND NEW object under an id the list already holds. The list
    // must end up bound to the new instance — if the differ treated "same keys, same order" as nothing
    // to do, the UI would keep rendering the stale object forever and no key-based assertion would see it.
    [TestMethod]
    public void SameKeysButNewInstances_RebindsToTheNewInstances()
    {
        var target = Collection("a", "b", "c");
        var desired = new List<Row> { new("a"), new("b"), new("c") };

        var (_, events, final) = Run(target, desired);

        Assert.AreEqual("a,b,c", final);
        for (var i = 0; i < desired.Count; i++)
            Assert.AreSame(desired[i], target[i],
                $"position {i} is still bound to the stale instance; the UI would render stale data");
        Assert.IsTrue(events > 0, "replacing every instance cannot be a no-op");
    }

    [TestMethod]
    public void EmptyDesired_ClearsTheList()
    {
        var target = Collection("a", "b", "c");
        var (_, _, final) = Run(target, new List<Row>());
        Assert.AreEqual("", final);
        Assert.AreEqual(0, target.Count);
    }

    // "The filter matched nothing" is an everyday keystroke AND the worst case for the old rebuild, so
    // it is the last place that may stay expensive. Emptying the list must be ONE Reset, not one
    // notification per row — asserting only the final state (as EmptyDesired_ClearsTheList does) passes
    // happily while the list is torn down 600 times.
    [TestMethod]
    public void EmptyingAFullList_CostsExactlyOneNotification()
    {
        var target = new ObservableCollection<Row>(Enumerable.Range(0, 600).Select(i => new Row("r" + i)));

        var (_, events, final) = Run(target, new List<Row>());

        Assert.AreEqual("", final);
        Assert.AreEqual(1, events,
            $"emptying a 600-row list raised {events} notifications; it must be a single Reset, " +
            "otherwise the freeze is still there exactly when a search matches nothing");
    }

    // A pure sort-order change keeps every row, so the membership threshold never fires. Reordering in
    // place would scan the list once per row to find each item — quadratic — while a Reset does the same
    // job linearly. keyOf invocations are counted because that is deterministic: unlike wall time it
    // cannot be perturbed by whatever else this machine is running.
    [TestMethod]
    public void FullReversal_DoesNotScanTheListOncePerRow()
    {
        const int n = 600;
        var rows = Enumerable.Range(0, n).Select(i => new Row("r" + i)).ToList();
        var target = new ObservableCollection<Row>(rows);
        var desired = Enumerable.Reverse(rows).ToList();

        var keyCalls = 0;
        var ops = ObservableDiff.Apply(target, desired, r => { keyCalls++; return r.Id; });

        CollectionAssert.AreEqual(desired, target, "a reversal must still land on the right order");
        Assert.IsTrue(keyCalls < 20 * n,
            $"reversing {n} rows cost {keyCalls:N0} key lookups; a linear path needs a few per row, " +
            "a per-row rescan needs hundreds of thousands");
        Assert.IsTrue(ops <= 1 + n,
            $"a full reversal raised {ops} notifications, more than a plain rebuild would have");
    }

    // The honesty gate: for a spread of random edits, the diffed result must be IDENTICAL — same
    // objects, same order — to what a naive clear-and-refill would have produced.
    [TestMethod]
    public void ForRandomEdits_TheDiffedResultMatchesAFullRebuild()
    {
        var rng = new Random(20260730);
        var pool = Enumerable.Range(0, 40).Select(i => new Row("r" + i)).ToList();

        for (var trial = 0; trial < 300; trial++)
        {
            var startCount = rng.Next(0, pool.Count);
            var target = new ObservableCollection<Row>(pool.OrderBy(_ => rng.Next()).Take(startCount));

            var desired = pool.OrderBy(_ => rng.Next()).Take(rng.Next(0, pool.Count)).ToList();

            var reference = new ObservableCollection<Row>();
            foreach (var r in desired) reference.Add(r);

            ObservableDiff.Apply(target, desired, r => r.Id);

            CollectionAssert.AreEqual(reference, target,
                $"trial {trial}: diffed list diverged from a full rebuild");
            for (var i = 0; i < desired.Count; i++)
                Assert.AreSame(desired[i], target[i], $"trial {trial}: position {i} holds the wrong instance");
        }
    }

    // RefreshSessions is the real consumer; prove the wiring, not just the primitive.
    [TestMethod]
    public void RefreshSessions_ReRunWithTheSameSet_RaisesNothing()
    {
        var svc = NewService(out var dir);
        try
        {
            var sessions = Enumerable.Range(0, 50)
                .Select(i => new ArchiveSession { Id = "s" + i, Title = "chat " + i, UpdatedAt = $"2026-01-{1 + i % 28:D2}T00:00:00Z" })
                .ToList();
            foreach (var s in sessions) svc.Store.Sessions[s.Id] = s;

            svc.RefreshSessions(svc.Store.Sessions.Values);
            var first = svc.Sessions.ToList();

            var events = 0;
            void Count(object? _, NotifyCollectionChangedEventArgs __) => events++;
            svc.Sessions.CollectionChanged += Count;
            try { svc.RefreshSessions(svc.Store.Sessions.Values); }
            finally { svc.Sessions.CollectionChanged -= Count; }

            Assert.AreEqual(0, events, "re-running RefreshSessions over an unchanged store must not notify");
            CollectionAssert.AreEqual(first, svc.Sessions.ToList());
        }
        finally { TryDelete(dir); }
    }

    [TestMethod]
    public void RefreshSessions_StillCapsTheListAndSkipsArchived()
    {
        var svc = NewService(out var dir);
        try
        {
            for (var i = 0; i < 700; i++)
                svc.Store.Sessions["s" + i] = new ArchiveSession
                {
                    Id = "s" + i,
                    Title = "chat " + i,
                    UpdatedAt = $"2026-01-01T00:00:{i % 60:D2}Z",
                    Archived = i % 100 == 0,
                };

            svc.RefreshSessions(svc.Store.Sessions.Values);

            Assert.AreEqual(600, svc.Sessions.Count, "the 600-row cap must survive the rewrite");
            Assert.IsFalse(svc.Sessions.Any(s => s.Archived), "archived chats must stay out of the list");
        }
        finally { TryDelete(dir); }
    }

    private static ArchiveService NewService(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "clr-listdiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new ArchiveService(
            storePath: Path.Combine(dir, "app-store.json"),
            codexSessionsRoot: Path.Combine(dir, "no-such-codex"),
            claudeSessionsRoot: Path.Combine(dir, "no-such-claude"));
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
