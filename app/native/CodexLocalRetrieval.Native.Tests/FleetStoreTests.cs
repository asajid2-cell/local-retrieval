using System.Globalization;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

public abstract class FleetTempRootTests
{
    protected string RootDirectory { get; private set; } = string.Empty;

    [TestInitialize]
    public void CreateFreshRoot()
    {
        RootDirectory = Path.Combine(
            Path.GetTempPath(),
            "fleet-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDirectory);
    }

    [TestCleanup]
    public void RemoveFreshRoot()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
        catch
        {
            // A cleanup failure must not mask the test result.
        }
    }

    protected FleetStore.Options StoreOptions(DateTimeOffset? now = null) =>
        new(RootDirectory, now);

    protected FleetWriterLock.Options LockOptions() =>
        new(RootDirectory);

    protected static FleetSessionRecord Session(
        string tool = "codex",
        string sessionId = "session-1",
        int[]? pids = null,
        string firstSeenUtc = "2026-08-01T10:00:00.0000000Z",
        string lastSeenUtc = "2026-08-01T10:05:00.0000000Z") =>
        new(
            tool,
            sessionId,
            (pids ?? new[] { 101, 202 }).ToList(),
            firstSeenUtc,
            lastSeenUtc);

    protected static FleetState State(
        string name = "current-name",
        string kind = "rolling",
        string savedAtUtc = "2026-08-01T12:34:56.0000000Z",
        string bootStampUtc = "2026-08-01T00:00:00.0000000Z",
        List<FleetSessionRecord>? sessions = null) =>
        new()
        {
            Name = name,
            Kind = kind,
            SavedAtUtc = savedAtUtc,
            BootStampUtc = bootStampUtc,
            Sessions = sessions ?? new List<FleetSessionRecord> { Session() },
        };

    protected static void AssertSessionEqual(FleetSessionRecord expected, FleetSessionRecord actual)
    {
        Assert.AreEqual(expected.Tool, actual.Tool);
        Assert.AreEqual(expected.SessionId, actual.SessionId);
        Assert.IsNotNull(actual.Pids);
        CollectionAssert.AreEqual(expected.Pids, actual.Pids);
        Assert.AreEqual(expected.FirstSeenUtc, actual.FirstSeenUtc);
        Assert.AreEqual(expected.LastSeenUtc, actual.LastSeenUtc);
    }

    protected static void AssertStateEqual(FleetState expected, FleetState actual)
    {
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Kind, actual.Kind);
        Assert.AreEqual(expected.SavedAtUtc, actual.SavedAtUtc);
        Assert.AreEqual(expected.BootStampUtc, actual.BootStampUtc);
        Assert.IsNotNull(actual.Sessions);
        Assert.AreEqual(expected.Sessions.Count, actual.Sessions.Count);
        for (var i = 0; i < expected.Sessions.Count; i++)
            AssertSessionEqual(expected.Sessions[i], actual.Sessions[i]);
    }
}

[TestClass]
public sealed class FleetStoreTests : FleetTempRootTests
{
    [TestMethod]
    public void FleetState_DefaultConstruction_UsesContractDefaults()
    {
        var state = new FleetState();

        Assert.AreEqual(string.Empty, state.Name);
        Assert.AreEqual("rolling", state.Kind);
        Assert.AreEqual(string.Empty, state.SavedAtUtc);
        Assert.AreEqual(string.Empty, state.BootStampUtc);
        Assert.IsNotNull(state.Sessions);
        Assert.AreEqual(0, state.Sessions.Count);
    }

    [TestMethod]
    public void TryWriteCurrent_ThenReadCurrent_PreservesAllFieldsAndSessions()
    {
        var now = new DateTimeOffset(2026, 8, 1, 13, 14, 15, TimeSpan.Zero);
        var expected = State(
            name: "ignored-current-name",
            kind: "named",
            savedAtUtc: "2026-07-31T23:59:58.1234567Z",
            bootStampUtc: "2026-07-30T01:02:03.4567890Z",
            sessions: new List<FleetSessionRecord>
            {
                Session(
                    tool: "claude",
                    sessionId: "session-alpha",
                    pids: new[] { 7, 11, 42 },
                    firstSeenUtc: "2026-07-30T01:02:03.0000000Z",
                    lastSeenUtc: "2026-07-31T23:59:57.0000000Z"),
                Session(
                    tool: "codex",
                    sessionId: "session-beta",
                    pids: new[] { 1001 },
                    firstSeenUtc: "2026-07-31T12:00:00.0000000Z",
                    lastSeenUtc: "2026-07-31T12:30:00.0000000Z"),
            });

        Assert.IsTrue(FleetStore.TryWriteCurrent(expected, out var writeDetail, StoreOptions(now)), writeDetail);
        Assert.IsTrue(FleetStore.TryReadCurrent(out var actual, out var readDetail, StoreOptions(now)), readDetail);
        Assert.IsNotNull(actual);
        AssertStateEqual(expected, actual!);
    }

    [TestMethod]
    public void TryWriteCurrent_SecondWrite_ReplacesPreviousCurrent()
    {
        var now = new DateTimeOffset(2026, 8, 1, 13, 14, 15, TimeSpan.Zero);
        var first = State(
            name: "first",
            kind: "rolling",
            sessions: new List<FleetSessionRecord> { Session(sessionId: "old-session") });
        var replacement = State(
            name: "second",
            kind: "boot",
            savedAtUtc: "2026-08-01T14:00:00.0000000Z",
            sessions: new List<FleetSessionRecord> { Session(sessionId: "new-session", pids: new[] { 909 }) });

        Assert.IsTrue(FleetStore.TryWriteCurrent(first, out var firstDetail, StoreOptions(now)), firstDetail);
        Assert.IsTrue(FleetStore.TryWriteCurrent(replacement, out var replacementDetail, StoreOptions(now)), replacementDetail);
        Assert.IsTrue(FleetStore.TryReadCurrent(out var actual, out var readDetail, StoreOptions(now)), readDetail);
        Assert.IsNotNull(actual);
        AssertStateEqual(replacement, actual!);
    }

    [TestMethod]
    public void TryReadCurrent_WhenCurrentFileIsMissing_ReturnsSuccessWithNull()
    {
        Assert.IsTrue(FleetStore.TryReadCurrent(out var state, out var detail, StoreOptions()));
        Assert.IsNull(state);
    }

    [TestMethod]
    public void TryWriteCurrent_AfterSuccessfulWrite_LeavesNoTemporaryFile()
    {
        var state = State();

        Assert.IsTrue(FleetStore.TryWriteCurrent(state, out var detail, StoreOptions()), detail);

        var temporaryFiles = Directory.GetFiles(RootDirectory, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert.AreEqual(0, temporaryFiles.Length);
        Assert.IsTrue(File.Exists(Path.Combine(RootDirectory, "current.json")));
    }

    [TestMethod]
    public void TrySaveNamed_ThenReadListAndDelete_CompletesNamedStateLifecycle()
    {
        var state = State(
            name: "state-name-will-be-replaced",
            kind: "named",
            sessions: new List<FleetSessionRecord> { Session(sessionId: "named-session") });
        var options = StoreOptions();

        Assert.IsTrue(FleetStore.TrySaveNamed("alpha", state, out var saveDetail, options), saveDetail);
        Assert.IsTrue(FleetStore.TryReadState("alpha", out var read, out var readDetail, options), readDetail);
        Assert.IsNotNull(read);
        Assert.AreEqual("alpha", read!.Name);
        AssertStateEqual(
            State(
                name: "alpha",
                kind: state.Kind,
                savedAtUtc: state.SavedAtUtc,
                bootStampUtc: state.BootStampUtc,
                sessions: state.Sessions),
            read);

        Assert.IsTrue(FleetStore.TryListStates(out var states, out var listDetail, options), listDetail);
        Assert.AreEqual(1, states.Count);
        AssertStateEqual(read, states[0]);

        Assert.IsTrue(FleetStore.TryDeleteState("alpha", out var deleteDetail, options), deleteDetail);
        Assert.IsFalse(FleetStore.TryReadState("alpha", out var deleted, out _, options));
        Assert.IsNull(deleted);
    }

    [TestMethod]
    public void TryListStates_WhenStatesDirectoryIsMissing_ReturnsSuccessWithEmptyList()
    {
        Assert.IsTrue(FleetStore.TryListStates(out var states, out var detail, StoreOptions()), detail);
        Assert.IsNotNull(states);
        Assert.AreEqual(0, states.Count);
    }

    [TestMethod]
    public void TrySaveNamed_InvalidNames_AreRefused()
    {
        var invalidNames = new[]
        {
            string.Empty,
            @"..\evil",
            "a/b",
            new string('a', 65),
            "-leading",
        };
        var state = State();

        foreach (var invalidName in invalidNames)
        {
            Assert.IsFalse(
                FleetStore.TrySaveNamed(invalidName, state, out var detail, StoreOptions()),
                $"Name '{invalidName}' was accepted. Detail: {detail}");
        }
    }

    [TestMethod]
    public void TrySaveNamed_SameNameWrittenTwice_OverwritesExistingState()
    {
        var first = State(
            name: "first",
            savedAtUtc: "2026-08-01T10:00:00.0000000Z",
            sessions: new List<FleetSessionRecord> { Session(sessionId: "first-session") });
        var second = State(
            name: "second",
            savedAtUtc: "2026-08-01T11:00:00.0000000Z",
            sessions: new List<FleetSessionRecord> { Session(sessionId: "second-session") });
        var options = StoreOptions();

        Assert.IsTrue(FleetStore.TrySaveNamed("same-name", first, out var firstDetail, options), firstDetail);
        Assert.IsTrue(FleetStore.TrySaveNamed("same-name", second, out var secondDetail, options), secondDetail);
        Assert.IsTrue(FleetStore.TryReadState("same-name", out var actual, out var readDetail, options), readDetail);
        Assert.IsNotNull(actual);
        Assert.AreEqual("same-name", actual!.Name);
        AssertStateEqual(
            State(
                name: "same-name",
                kind: second.Kind,
                savedAtUtc: second.SavedAtUtc,
                bootStampUtc: second.BootStampUtc,
                sessions: second.Sessions),
            actual);
    }

    [TestMethod]
    public void TryListStates_MultipleSavedStates_ReturnsNewestSavedAtFirst()
    {
        var options = StoreOptions();
        var entries = new[]
        {
            ("oldest", "2026-08-01T10:00:00.0000000Z"),
            ("newest", "2026-08-03T10:00:00.0000000Z"),
            ("middle", "2026-08-02T10:00:00.0000000Z"),
        };

        foreach (var (name, savedAtUtc) in entries)
        {
            Assert.IsTrue(
                FleetStore.TrySaveNamed(name, State(savedAtUtc: savedAtUtc), out var detail, options),
                detail);
        }

        Assert.IsTrue(FleetStore.TryListStates(out var states, out var listDetail, options), listDetail);
        CollectionAssert.AreEqual(new[] { "newest", "middle", "oldest" }, states.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public void TryListStates_CorruptStateFile_SkipsItAndNamesItInDetail()
    {
        var options = StoreOptions();
        Assert.IsTrue(
            FleetStore.TrySaveNamed("good", State(name: "good"), out var saveDetail, options),
            saveDetail);

        var statesDirectory = Path.Combine(RootDirectory, "states");
        Directory.CreateDirectory(statesDirectory);
        File.WriteAllText(Path.Combine(statesDirectory, "bad.json"), "{ not valid json");

        Assert.IsTrue(FleetStore.TryListStates(out var states, out var detail, options), detail);
        Assert.AreEqual(1, states.Count);
        Assert.AreEqual("good", states[0].Name);
        StringAssert.Contains(detail, "bad.json");
    }

    [TestMethod]
    public void SameBoot_EqualStamps_ReturnsTrue()
    {
        var stamp = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero).ToString("O");

        Assert.IsTrue(FleetStore.SameBoot(stamp, stamp));
    }

    [TestMethod]
    public void CurrentBootStampUtc_WithExplicitNow_ReturnsOFormattedStampNearExpectedValue()
    {
        var now = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var stamp = FleetStore.CurrentBootStampUtc(now);

        Assert.IsTrue(
            DateTimeOffset.TryParseExact(
                stamp,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var actual));
        var expected = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        Assert.IsTrue(
            (actual - expected).Duration() <= TimeSpan.FromSeconds(1),
            $"Stamp was {actual:O}; expected approximately {expected:O}.");
    }

    [TestMethod]
    public void SameBoot_StampsTwentyNineSecondsApart_ReturnsTrue()
    {
        var first = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var second = first.AddSeconds(29);

        Assert.IsTrue(FleetStore.SameBoot(first.ToString("O"), second.ToString("O")));
    }

    [TestMethod]
    public void SameBoot_StampsThirtyOneSecondsApart_ReturnsFalse()
    {
        var first = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var second = first.AddSeconds(31);

        Assert.IsFalse(FleetStore.SameBoot(first.ToString("O"), second.ToString("O")));
    }

    [TestMethod]
    public void SameBoot_UnparseableStamp_ReturnsFalse()
    {
        var valid = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero).ToString("O");

        Assert.IsFalse(FleetStore.SameBoot("garbage", valid));
        Assert.IsFalse(FleetStore.SameBoot(valid, "also-garbage"));
    }

    [TestMethod]
    public void TryPromoteStaleCurrent_WhenCurrentIsMissing_ReturnsNullPromotion()
    {
        var now = new DateTimeOffset(2026, 8, 1, 13, 14, 15, TimeSpan.Zero);

        Assert.IsTrue(
            FleetStore.TryPromoteStaleCurrent(out var promotedName, out var detail, StoreOptions(now)),
            detail);
        Assert.IsNull(promotedName);
    }

    [TestMethod]
    public void TryPromoteStaleCurrent_WhenCurrentHasCurrentBootStamp_ReturnsNullPromotion()
    {
        var now = new DateTimeOffset(2026, 8, 1, 13, 14, 15, TimeSpan.Zero);
        var current = State(bootStampUtc: FleetStore.CurrentBootStampUtc(now));

        Assert.IsTrue(FleetStore.TryWriteCurrent(current, out var writeDetail, StoreOptions(now)), writeDetail);
        Assert.IsTrue(
            FleetStore.TryPromoteStaleCurrent(out var promotedName, out var detail, StoreOptions(now)),
            detail);
        Assert.IsNull(promotedName);
        Assert.IsTrue(File.Exists(Path.Combine(RootDirectory, "current.json")));
    }

    [TestMethod]
    public void TryPromoteStaleCurrent_WhenCurrentIsStale_PromotesBootStateAndKeepsCurrent()
    {
        var now = new DateTimeOffset(2026, 8, 1, 13, 14, 15, TimeSpan.Zero);
        var savedAtUtc = "2026-08-01T12:34:56.0000000Z";
        var current = State(
            name: "before-promotion",
            kind: "rolling",
            savedAtUtc: savedAtUtc,
            bootStampUtc: FleetStore.CurrentBootStampUtc(now.AddHours(-2)),
            sessions: new List<FleetSessionRecord>
            {
                Session(
                    tool: "claude",
                    sessionId: "preserve-me",
                    pids: new[] { 12, 34 },
                    firstSeenUtc: "2026-08-01T12:00:00.0000000Z",
                    lastSeenUtc: "2026-08-01T12:30:00.0000000Z"),
            });
        var options = StoreOptions(now);
        var expectedName = "pre-reboot-20260801-123456";

        Assert.IsTrue(FleetStore.TryWriteCurrent(current, out var writeDetail, options), writeDetail);
        Assert.IsTrue(
            FleetStore.TryPromoteStaleCurrent(out var promotedName, out var promoteDetail, options),
            promoteDetail);
        Assert.AreEqual(expectedName, promotedName);

        Assert.IsTrue(FleetStore.TryReadState(expectedName, out var promoted, out var readDetail, options), readDetail);
        Assert.IsNotNull(promoted);
        Assert.AreEqual(expectedName, promoted!.Name);
        Assert.AreEqual("boot", promoted.Kind);
        Assert.AreEqual(current.SavedAtUtc, promoted.SavedAtUtc);
        Assert.AreEqual(current.BootStampUtc, promoted.BootStampUtc);
        Assert.AreEqual(current.Sessions.Count, promoted.Sessions.Count);
        AssertSessionEqual(current.Sessions[0], promoted.Sessions[0]);
        Assert.IsTrue(File.Exists(Path.Combine(RootDirectory, "current.json")));
    }

    [TestMethod]
    public void TryPromoteStaleCurrent_WhenCalledTwice_IsIdempotentAndCreatesOneStateFile()
    {
        var now = new DateTimeOffset(2026, 8, 1, 13, 14, 15, TimeSpan.Zero);
        var current = State(
            savedAtUtc: "2026-08-01T12:34:56.0000000Z",
            bootStampUtc: FleetStore.CurrentBootStampUtc(now.AddHours(-3)));
        var options = StoreOptions(now);

        Assert.IsTrue(FleetStore.TryWriteCurrent(current, out var writeDetail, options), writeDetail);
        Assert.IsTrue(
            FleetStore.TryPromoteStaleCurrent(out var firstName, out var firstDetail, options),
            firstDetail);
        Assert.IsNotNull(firstName);
        Assert.IsTrue(
            FleetStore.TryPromoteStaleCurrent(out var secondName, out var secondDetail, options),
            secondDetail);
        Assert.IsNull(secondName);

        var stateFiles = Directory.GetFiles(Path.Combine(RootDirectory, "states"), "*.json");
        Assert.AreEqual(1, stateFiles.Length);
    }
}

[TestClass]
public sealed class FleetWriterLockTests : FleetTempRootTests
{
    [TestMethod]
    public void TryAcquire_WhileAlreadyHeld_RejectsSecondAcquireWithNullHandle()
    {
        IDisposable? firstHandle = null;
        try
        {
            Assert.IsTrue(
                FleetWriterLock.TryAcquire("host-one", out firstHandle, out var firstDetail, LockOptions()),
                firstDetail);
            Assert.IsNotNull(firstHandle);

            Assert.IsFalse(
                FleetWriterLock.TryAcquire("host-two", out var secondHandle, out _ , LockOptions()));
            Assert.IsNull(secondHandle);
        }
        finally
        {
            firstHandle?.Dispose();
        }
    }

    [TestMethod]
    public void TryReadHolder_WhileLockIsHeld_ReturnsCurrentProcessAndHost()
    {
        IDisposable? handle = null;
        try
        {
            Assert.IsTrue(
                FleetWriterLock.TryAcquire("readable-host", out handle, out var acquireDetail, LockOptions()),
                acquireDetail);
            Assert.IsNotNull(handle);

            Assert.IsTrue(
                FleetWriterLock.TryReadHolder(out var holder, out var readDetail, LockOptions()),
                readDetail);
            Assert.IsNotNull(holder);
            Assert.AreEqual(Environment.ProcessId, holder!.Pid);
            Assert.AreEqual("readable-host", holder.Host);
            Assert.IsFalse(string.IsNullOrWhiteSpace(holder.AcquiredAtUtc));
        }
        finally
        {
            handle?.Dispose();
        }
    }

    [TestMethod]
    public void TryAcquire_AfterDispose_ReacquiresAndDoubleDisposeIsHarmless()
    {
        Assert.IsTrue(
            FleetWriterLock.TryAcquire("first-host", out var firstHandle, out var firstDetail, LockOptions()),
            firstDetail);
        Assert.IsNotNull(firstHandle);

        firstHandle!.Dispose();
        firstHandle.Dispose();

        Assert.IsTrue(
            FleetWriterLock.TryAcquire("second-host", out var secondHandle, out var secondDetail, LockOptions()),
            secondDetail);
        Assert.IsNotNull(secondHandle);
        secondHandle!.Dispose();
    }

    [TestMethod]
    public void TryAcquire_WithClosedLeftoverLockFile_DoesNotBlockAcquire()
    {
        var lockPath = Path.Combine(RootDirectory, "writer.lock");
        File.WriteAllBytes(lockPath, new byte[] { 0x01, 0x02, 0x03, 0x04 });

        Assert.IsTrue(
            FleetWriterLock.TryAcquire("after-closed-file", out var handle, out var detail, LockOptions()),
            detail);
        Assert.IsNotNull(handle);
        handle!.Dispose();
    }

    [TestMethod]
    public void TryReadHolder_WithEmptyExistingLockFile_ReturnsSuccessWithNullAndDetail()
    {
        var lockPath = Path.Combine(RootDirectory, "writer.lock");
        File.WriteAllText(lockPath, string.Empty);

        Assert.IsTrue(
            FleetWriterLock.TryReadHolder(out var holder, out var detail, LockOptions()),
            detail);
        Assert.IsNull(holder);
        Assert.IsFalse(string.IsNullOrWhiteSpace(detail));
    }
}
