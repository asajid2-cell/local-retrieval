using System.Text.Json;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public class SessionOwnerRecordsTests
{
    [TestMethod]
    public void TryWrite_WritesOneRecordPerCandidateIdInSortedOrder()
    {
        using var dir = TempOwnerDir();

        Assert.IsTrue(SessionOwnerRecords.TryWrite(
            "parent-id",
            new[] { "child-id", "another-id", "parent-id" },
            wrapperPid: 4321,
            wrapperStartTimeUtc: DateTimeOffset.UtcNow,
            transport: "terminal",
            out var detail,
            options: Options(dir.Path)), detail);

        var files = Directory.EnumerateFiles(dir.Path).Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.AreEqual(3, files.Count);
        CollectionAssert.AreEquivalent(
            new[] { "another-id", "child-id", "parent-id" }.Select(FileNameFor).ToList(),
            files);

        // Every record carries the full candidate set, sorted, with the lowest id as the canonical one -
        // an alias-addressed launch must land on the same files as a sid-addressed one.
        foreach (var file in files)
        {
            var record = ReadRaw(Path.Combine(dir.Path, file!));
            CollectionAssert.AreEqual(
                new[] { "another-id", "child-id", "parent-id" },
                record.GetProperty("CandidateIds").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.AreEqual("another-id", record.GetProperty("SessionId").GetString());
        }
    }

    [TestMethod]
    public void TryWrite_RoundTripsEveryField()
    {
        using var dir = TempOwnerDir();
        var started = new DateTimeOffset(2026, 7, 19, 4, 5, 6, TimeSpan.Zero);
        var claimedAt = new DateTimeOffset(2026, 7, 19, 4, 5, 9, TimeSpan.Zero);

        Assert.IsTrue(SessionOwnerRecords.TryWrite(
            "parent-id",
            new[] { "child-id" },
            wrapperPid: 2468,
            wrapperStartTimeUtc: started,
            transport: "muxd",
            out var detail,
            muxName: "clr-parent-id",
            options: Options(dir.Path, claimedAt)), detail);

        var records = SessionOwnerRecords.ReadRecordsForSession("child-id", options: Options(dir.Path));
        Assert.AreEqual(1, records.Count);
        var record = records[0];
        Assert.AreEqual("child-id", record.SessionId);
        CollectionAssert.AreEqual(new[] { "child-id", "parent-id" }, record.CandidateIds.ToArray());
        Assert.AreEqual(2468, record.WrapperPid);
        Assert.AreEqual(started, record.WrapperStartTimeUtc);
        Assert.AreEqual("muxd", record.Transport);
        Assert.AreEqual(claimedAt, record.ClaimedAt);
        Assert.AreEqual("clr-parent-id", record.MuxName);
        Assert.IsNull(record.JobName, "a muxd record has no wrapper of ours to put in a job");
    }

    // [F#7] The job name is the kill handle for this launch: without it round-tripping, Kill silently falls
    // back to the racy snapshot tree-kill.
    [TestMethod]
    public void TryWrite_RoundTripsTheJobName()
    {
        using var dir = TempOwnerDir();

        Assert.IsTrue(SessionOwnerRecords.TryWrite(
            "parent-id",
            new[] { "child-id" },
            wrapperPid: 1357,
            wrapperStartTimeUtc: DateTimeOffset.UtcNow,
            transport: "terminal",
            out var detail,
            options: Options(dir.Path),
            jobName: @"Local\CodexLocalRetrieval-owner-1357-638000000000000000"), detail);

        // Both the sid record and the alias record must carry it - an alias-addressed kill reads the alias file.
        foreach (var id in new[] { "parent-id", "child-id" })
        {
            var record = SessionOwnerRecords.ReadRecordsForSession(id, options: Options(dir.Path)).Single();
            Assert.AreEqual(@"Local\CodexLocalRetrieval-owner-1357-638000000000000000", record.JobName);
        }
    }

    [TestMethod]
    public void TryWrite_LeavesAnAbsentJobNameNull()
    {
        using var dir = TempOwnerDir();

        Assert.IsTrue(SessionOwnerRecords.TryWrite(
            "parent-id", null, 42, null, "terminal", out var detail, options: Options(dir.Path)), detail);
        // Whitespace is not a job name either - Kill must be able to test it with a plain null check.
        Assert.IsTrue(SessionOwnerRecords.TryWrite(
            "blank-id", null, 42, null, "terminal", out var blankDetail,
            options: Options(dir.Path), jobName: "   "), blankDetail);

        Assert.IsNull(SessionOwnerRecords.ReadRecordsForSession("parent-id", options: Options(dir.Path)).Single().JobName);
        Assert.IsNull(SessionOwnerRecords.ReadRecordsForSession("blank-id", options: Options(dir.Path)).Single().JobName);
    }

    // Records written before the job-object field existed must still read back cleanly.
    [TestMethod]
    public void ReadRecordsForSession_TreatsAPreJobObjectRecordAsHavingNoJob()
    {
        using var dir = TempOwnerDir();
        File.WriteAllText(
            Path.Combine(dir.Path, FileNameFor("parent-id")),
            """{"SessionId":"parent-id","CandidateIds":["parent-id"],"WrapperPid":7,"Transport":"terminal"}""");

        var record = SessionOwnerRecords.ReadRecordsForSession("parent-id", options: Options(dir.Path)).Single();
        Assert.AreEqual(7, record.WrapperPid);
        Assert.IsNull(record.JobName);
    }

    // The one place a real Process is in hand is the one place a job is assigned [F#7]; that assignment must
    // stay opt-out so record-only tests never create job objects as a side effect.
    [TestMethod]
    public void TryWriteForProcess_AssignsAJobAndRecordsItsName()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("job objects are Windows-only.");
        using var dir = TempOwnerDir();
        using var wrapper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/Q", "/K" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        })!;
        try
        {
            Assert.IsTrue(SessionOwnerRecords.TryWriteForProcess(
                "parent-id", null, wrapper, "terminal", out var detail, Options(dir.Path)), detail);

            var record = SessionOwnerRecords.ReadRecordsForSession("parent-id", options: Options(dir.Path)).Single();
            Assert.IsNotNull(record.JobName);
            StringAssert.Contains(record.JobName!, wrapper.Id.ToString());
            Assert.IsTrue(OwnerJobObjects.HoldsHandle(record.JobName),
                "the creating handle must be held for the app's lifetime, or the job dies with its last member");

            Assert.IsTrue(SessionOwnerRecords.TryWriteForProcess(
                "other-id", null, wrapper, "terminal", out var optOut, Options(dir.Path), assignJobObject: false), optOut);
            Assert.IsNull(
                SessionOwnerRecords.ReadRecordsForSession("other-id", options: Options(dir.Path)).Single().JobName);

            OwnerJobObjects.ReleaseHandle(record.JobName);
        }
        finally
        {
            try { if (!wrapper.HasExited) wrapper.Kill(entireProcessTree: true); } catch { }
        }
    }

    [TestMethod]
    public void TryWrite_AcceptsAnUnknownWrapper()
    {
        // The muxd path records before muxd has published its shell pid; that is expected, not an error.
        using var dir = TempOwnerDir();

        Assert.IsTrue(SessionOwnerRecords.TryWrite(
            "parent-id",
            null,
            wrapperPid: 0,
            wrapperStartTimeUtc: null,
            transport: "muxd",
            out var detail,
            muxName: "clr-parent-id",
            options: Options(dir.Path)), detail);

        var record = SessionOwnerRecords.ReadRecordsForSession("parent-id", options: Options(dir.Path)).Single();
        Assert.AreEqual(0, record.WrapperPid);
        Assert.IsNull(record.WrapperStartTimeUtc);
    }

    [TestMethod]
    public void TryWrite_RejectsMissingSessionIdWithoutWritingAnything()
    {
        using var dir = TempOwnerDir();

        Assert.IsFalse(SessionOwnerRecords.TryWrite(
            "ünicode-id",
            null,
            wrapperPid: 1,
            wrapperStartTimeUtc: null,
            transport: "terminal",
            out var detail,
            options: Options(dir.Path)));

        StringAssert.Contains(detail, "missing session id");
        Assert.AreEqual(0, Directory.EnumerateFiles(dir.Path).Count());
    }

    [TestMethod]
    public void TryWrite_ReportsLockedRecordFileInsteadOfThrowing()
    {
        using var dir = TempOwnerDir();
        var locked = Path.Combine(dir.Path, FileNameFor("parent-id"));
        using var hold = new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None);

        var ok = SessionOwnerRecords.TryWrite(
            "parent-id",
            new[] { "child-id" },
            wrapperPid: 99,
            wrapperStartTimeUtc: null,
            transport: "terminal",
            out var detail,
            options: Options(dir.Path));

        Assert.IsFalse(ok);
        StringAssert.Contains(detail, "parent-id");
        // The unblocked sibling still gets its record - one locked file does not abandon the rest.
        Assert.IsTrue(File.Exists(Path.Combine(dir.Path, FileNameFor("child-id"))));
    }

    [TestMethod]
    public void TryWrite_ReportsUnusableRootInsteadOfThrowing()
    {
        using var dir = TempOwnerDir();
        // A file where the owners directory should be: CreateDirectory cannot succeed here.
        var blocked = Path.Combine(dir.Path, "owners");
        File.WriteAllText(blocked, "not a directory");

        var ok = SessionOwnerRecords.TryWrite(
            "parent-id",
            null,
            wrapperPid: 1,
            wrapperStartTimeUtc: null,
            transport: "terminal",
            out var detail,
            options: Options(blocked));

        Assert.IsFalse(ok);
        StringAssert.Contains(detail, "owner-record directory");
    }

    [TestMethod]
    public void TryWriteForProcess_SurvivesAWrapperThatAlreadyExited()
    {
        using var dir = TempOwnerDir();
        using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        exited.WaitForExit();

        Assert.IsTrue(SessionOwnerRecords.TryWriteForProcess(
            "parent-id",
            null,
            exited,
            "terminal",
            out var detail,
            Options(dir.Path)), detail);

        var record = SessionOwnerRecords.ReadRecordsForSession("parent-id", options: Options(dir.Path)).Single();
        Assert.AreEqual("terminal", record.Transport);
        Assert.AreEqual(exited.Id, record.WrapperPid);
    }

    [TestMethod]
    public void TryWriteForProcess_RecordsNoWrapperWhenProcessStartReturnedNothing()
    {
        using var dir = TempOwnerDir();

        Assert.IsTrue(SessionOwnerRecords.TryWriteForProcess(
            "parent-id",
            null,
            process: null,
            transport: "server",
            out var detail,
            Options(dir.Path)), detail);

        var record = SessionOwnerRecords.ReadRecordsForSession("parent-id", options: Options(dir.Path)).Single();
        Assert.AreEqual(0, record.WrapperPid);
        Assert.IsNull(record.WrapperStartTimeUtc);
        Assert.AreEqual("server", record.Transport);
    }

    [TestMethod]
    public void TryWrite_OverwritesTheRecordOnRelaunch()
    {
        using var dir = TempOwnerDir();
        var options = Options(dir.Path);

        Assert.IsTrue(SessionOwnerRecords.TryWrite("parent-id", null, 11, null, "terminal", out var first, options: options), first);
        Assert.IsTrue(SessionOwnerRecords.TryWrite("parent-id", null, 22, null, "terminal", out var second, options: options), second);

        var records = SessionOwnerRecords.ReadRecordsForSession("parent-id", options: options);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(22, records[0].WrapperPid);
    }

    private static JsonElement ReadRaw(string path)
        => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    // Mirrors SessionLaunchClaims' claim-file naming, with the .owner.json suffix.
    private static string FileNameFor(string id)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToLowerInvariant())))
            .ToLowerInvariant();
        return $"{id}-{hash[..16]}.owner.json";
    }

    private static SessionOwnerRecords.Options Options(string root, DateTimeOffset? now = null)
        => new(root, now);

    private static TempDir TempOwnerDir()
        => new(Path.Combine(Path.GetTempPath(), "clr-owners-" + Guid.NewGuid().ToString("N")));

    private sealed class TempDir : IDisposable
    {
        public TempDir(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
