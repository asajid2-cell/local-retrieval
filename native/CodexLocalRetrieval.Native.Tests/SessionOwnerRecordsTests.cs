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
