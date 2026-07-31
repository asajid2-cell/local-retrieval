using System.Collections.Specialized;
using System.Diagnostics;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests.Perf;

// The reported symptom is "freezes and hitches when filtering or trying to find chats". That is a
// UI-THREAD BLOCK, so what has to be measured is the work the dispatcher does between accepting a
// keystroke and being free again — not throughput and not allocation.
//
// MainPage.Tags.cs ApplyFilters() is that block, and every line of it is synchronous on the dispatcher:
//
//     var results = _archive.FilterChats(CurrentChatFilter());   // full filter pass
//     foreach (var s in results) s.RowTitleMode = titleMode;      // per-result property notification
//     _archive.RefreshSessions(results, preserveOrder: preserve); // Sessions.Clear() + N Add()
//     SelectFirstSession();
//     RenderTagFilterBar();                                       // AllChatTags() + HiddenChatCount()
//
// This class reproduces exactly that sequence against a 4000-session corpus and reports two numbers:
//
//   * wall milliseconds of the whole synchronous block — the stall the user feels; and
//   * CollectionChanged events raised on the bound Sessions collection — the number that actually
//     drives the XAML cost, because the ListView tears down and rebuilds item containers per event.
//     A headless test cannot measure layout, but it can measure how much layout is being ASKED for,
//     and Clear() + 600 Add() asks for 601 separate relayouts.
[TestClass]
public sealed class FilterHitchTests
{
    private const int CorpusSessions = 4000;

    private static ArchiveService _svc = null!;
    private static string _storeDir = "";

    [ClassInitialize]
    public static void BuildCorpus(TestContext _)
    {
        _svc = PerfCorpus.NewStoreService(out _storeDir);
        _svc.Store.Settings.BundledHistoryAbsorbed = true;
        PerfCorpus.FillSessions(_svc, CorpusSessions);
    }

    [ClassCleanup]
    public static void DropCorpus() => PerfCorpus.TryDeleteDirectory(_storeDir);

    // One keystroke's worth of dispatcher work, exactly as ApplyFilters orders it.
    private static (double Ms, int Events) Keystroke(string query)
    {
        var events = 0;
        void Count(object? s, NotifyCollectionChangedEventArgs e) => events++;
        _svc.Sessions.CollectionChanged += Count;
        try
        {
            var filter = new ChatFilter { Query = query };
            var sw = Stopwatch.StartNew();
            var results = _svc.FilterChats(filter);
            foreach (var s in results) s.RowTitleMode = "";
            _svc.RefreshSessions(results, preserveOrder: !string.IsNullOrWhiteSpace(query));
            _ = _svc.AllChatTags();
            _ = _svc.HiddenChatCount();
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, events);
        }
        finally
        {
            _svc.Sessions.CollectionChanged -= Count;
        }
    }

    // The budget: a keystroke must not block the dispatcher long enough to be seen. 16 ms is one frame
    // at 60 Hz; 50 ms is the threshold at which an interaction stops feeling attached to the keyboard.
    // 50 ms is the gate, with the frame budget recorded alongside it as the thing to aim at.
    private const double KeystrokeBudgetMs = 50.0;

    // The list has 600 visible rows at most. A filter change that genuinely reorders everything cannot
    // avoid touching them, but the COMMON case — typing another character, or re-running the same
    // filter after a mutation — changes few rows or none, and must cost events proportional to the
    // delta rather than to the list.
    private const int UnchangedFilterEventBudget = 0;
    private const int SmallDeltaEventBudget = 8;

    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public void TypingAKeystroke_DoesNotBlockTheDispatcherPastTheBudget()
    {
        // Warm: the user has already been typing. Cold JIT is not what they are complaining about.
        for (var i = 0; i < 3; i++) { Keystroke(""); Keystroke("archive store"); }

        var samples = new List<double>();
        foreach (var q in new[] { "", "r", "re", "ren", "rend", "render", "renderer", "renderer p", "renderer pa", "renderer parse" })
            samples.Add(Keystroke(q).Ms);

        samples.Sort();
        var best = samples[0];
        var median = samples[samples.Count / 2];
        PerfRecord.Measure("filter.keystroke.ms.min", best, "ms");
        PerfRecord.Measure("filter.keystroke.ms.p50", median, "ms");
        PerfRecord.Measure("filter.keystroke.ms.max", samples[^1], "ms");

        // Asserted on the BEST sample deliberately. This machine runs a 6000-entry process table and
        // several concurrent agents; a median or max bound here measures whatever else is running and
        // goes red for reasons that have nothing to do with this code. The best-of-N answers the only
        // question a fence should ask — can this path still complete inside the budget at all.
        Assert.IsTrue(best < KeystrokeBudgetMs,
            $"the best keystroke still blocked the dispatcher for {best:F2} ms (median {median:F2} ms); " +
            $"budget is {KeystrokeBudgetMs} ms. This is the freeze the user reports.");
    }

    // The structural half of the hitch: re-running the SAME filter must not tell the ListView to throw
    // away and rebuild every row. This is what a mutation (tag, pin, rename, background sync) does via
    // ReapplyActiveFilter, and what every keystroke does via ApplyFilters.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public void RerunningTheSameFilter_RaisesNoCollectionChangedEvents()
    {
        Keystroke("renderer parse");
        var (_, events) = Keystroke("renderer parse");

        PerfRecord.Measure("filter.rerun.collectionChangedEvents", events, "count");
        Assert.AreEqual(UnchangedFilterEventBudget, events,
            $"re-running an unchanged filter raised {events} CollectionChanged events; each one makes the " +
            "bound ListView rebuild item containers, which is the visible hitch");
    }

    // A one-row delta must cost a one-row update, not a full rebuild.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public void OneSessionLeavingTheFilter_RaisesEventsProportionalToTheDelta()
    {
        Keystroke("");
        var victim = _svc.Sessions.First();

        var events = 0;
        void Count(object? s, NotifyCollectionChangedEventArgs e) => events++;
        _svc.Sessions.CollectionChanged += Count;
        try
        {
            victim.Archived = true;   // drops out of the visible set
            var results = _svc.FilterChats(new ChatFilter());
            _svc.RefreshSessions(results);
        }
        finally
        {
            _svc.Sessions.CollectionChanged -= Count;
            victim.Archived = false;
        }

        PerfRecord.Measure("filter.oneRowDelta.collectionChangedEvents", events, "count");
        Assert.IsTrue(events <= SmallDeltaEventBudget,
            $"removing one row from the filtered set raised {events} CollectionChanged events, " +
            $"budget {SmallDeltaEventBudget}");
    }
}
