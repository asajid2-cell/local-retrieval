using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

// The leaf-family store belongs to the gateway. These tests pin the reading of it to the shape the gateway
// actually writes: a family file named after its creator, a loose per-session pointer one directory down,
// and a file name that is not an id meaning "this is not a family".
[TestClass]
public class LeafFamilyStoreTests
{
    // ---------- the store itself ----------

    [TestMethod]
    public void MissingStoreReadsAsNoFamiliesRatherThanThrowing()
    {
        // Absence is the normal case: an app on a box that has never spawned a leaf must behave exactly as it
        // did before families existed, and must not report an error for it.
        using var fixture = new FamilyFixture(directoryExists: false);

        var store = new LeafFamilyStore(fixture.Options);

        Assert.AreEqual(0, store.Families.Count);
        Assert.AreEqual(0, store.MemberCount);
        Assert.AreEqual(0, store.MultiMemberFamilyCount);
        Assert.IsNull(store.FamilyFor("any-session"));
        Assert.IsNull(store.MemberFor("any-session"));
    }

    [TestMethod]
    public void SkipsFilesThatAreNotFamilies()
    {
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        fixture.WriteFamily(root, 2, fixture.Member(root, 1, "leaf-one"));
        // A sibling that belongs to another program: a stray json, a host claim, a name that is not an id.
        fixture.WriteRaw(Path.Combine(fixture.FamilyDir, "notes.json"), """{"rootSessionId":"x","members":[]}""");
        fixture.WriteRaw(Path.Combine(fixture.FamilyDir, root + ".host.json"), """{"rootSessionId":"x","members":[]}""");
        fixture.WriteRaw(Path.Combine(fixture.FamilyDir, "not-a-guid.json"), """{"rootSessionId":"x","members":[]}""");
        // A family file that cannot be a family: no members array, and one that is not json at all.
        fixture.WriteRaw(Path.Combine(fixture.FamilyDir, fixture.NewId() + ".json"), """{"rootSessionId":"x"}""");
        fixture.WriteRaw(Path.Combine(fixture.FamilyDir, fixture.NewId() + ".json"), "{ not json");

        var store = new LeafFamilyStore(fixture.Options);

        Assert.AreEqual(1, store.Families.Count);
        Assert.AreEqual(root, store.Families[0].RootSessionId);
    }

    [TestMethod]
    public void IndexesEveryMemberOfEveryFamily()
    {
        using var fixture = new FamilyFixture();
        var first = fixture.NewId();
        var second = fixture.NewId();
        fixture.WriteFamily(
            first, 4,
            fixture.Member(first, 1, "root", sessionId: first),
            fixture.Member(first, 2, "one"),
            fixture.Member(first, 3, "two"));
        fixture.WriteFamily(second, 2, fixture.Member(second, 1, "root", sessionId: second));

        var store = new LeafFamilyStore(fixture.Options);

        // 4 members over 2 families; only the first is more than one, which is the only case a collapse
        // changes anything for.
        Assert.AreEqual(2, store.Families.Count);
        Assert.AreEqual(4, store.MemberCount);
        Assert.AreEqual(1, store.MultiMemberFamilyCount);
    }

    [TestMethod]
    public void ResolvesFamilyAndMemberByAnyIdInItIncludingTheRoot()
    {
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        var leaf = fixture.NewId();
        fixture.WriteFamily(root, 3, fixture.Member(root, 1, "the-root", sessionId: root), fixture.Member(root, 2, "leaf-one", sessionId: leaf));

        var store = new LeafFamilyStore(fixture.Options);

        var family = store.FamilyFor(leaf);
        Assert.IsNotNull(family);
        Assert.AreEqual(root, family!.RootSessionId);
        // The creator is a chat like any other, so a lookup BY the root has to find the family too - that is
        // the row a collapse replaces, and it is the row the family is named after.
        Assert.AreEqual(family, store.FamilyFor(root));
        Assert.IsNotNull(store.MemberFor(root));
        Assert.AreEqual("leaf-one", store.MemberFor(leaf)!.Name);
        Assert.IsNull(store.FamilyFor("a-session-in-no-family"));
    }

    [TestMethod]
    public void ReadsMembersInFileOrderWithTheirDisplayNumbers()
    {
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        fixture.WriteFamily(
            root, 6,
            fixture.Member(root, 1, "the-root", sessionId: root),
            fixture.Member(root, 5, "later-leaf", status: "closed"));

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        CollectionAssert.AreEqual(new[] { "the-root", "later-leaf" }, family.Members.Select(m => m.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 5 }, family.Members.Select(m => m.Number).ToArray());
        Assert.IsTrue(family.Members[1].IsClosed);
        Assert.IsFalse(family.Members[0].IsClosed, "an open member is not closed");
        Assert.IsFalse(family.Members[0].IsCreator, "a row the file has is the family's own, not a synthesized one");
    }

    // ---------- who heads the family ----------

    [TestMethod]
    public void FallsBackToTheRootWhenNothingIsDesignated()
    {
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        var leaf = fixture.NewId();
        fixture.WriteFamily(root, 3, fixture.Member(root, 1, "the-root", sessionId: root), fixture.Member(root, 2, "leaf-two", sessionId: leaf));
        fixture.WritePointer(root, rootSessionId: root);   // no patriarchSessionId: the pre-designation shape

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        Assert.AreEqual(root, family.PatriarchSessionId);
        Assert.IsFalse(family.Delegated, "the root IS the head, which is not a delegation");
        Assert.AreEqual(root, family.Patriarch!.SessionId);
        CollectionAssert.AreEqual(new[] { leaf }, family.Others.Select(m => m.SessionId).ToArray());
    }

    [TestMethod]
    public void ReadsTheDesignatedPatriarchFromTheCreatorsOwnPointer()
    {
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        var leaf = fixture.NewId();
        fixture.WriteFamily(root, 3, fixture.Member(root, 1, "the-root", sessionId: root), fixture.Member(root, 2, "leaf-two", sessionId: leaf));
        fixture.WritePointer(root, rootSessionId: root, patriarchSessionId: leaf);

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        Assert.AreEqual(leaf, family.PatriarchSessionId);
        Assert.IsTrue(family.Delegated);
        Assert.AreEqual("leaf-two", family.Patriarch!.Name);
        CollectionAssert.AreEqual(new[] { root }, family.Others.Select(m => m.SessionId).ToArray());
    }

    [TestMethod]
    public void IgnoresADesignationThatNamesASessionOutsideTheFamily()
    {
        // A pointer can outlive the family it names - a member archived out, a file rewritten from an older
        // copy. Drawing a head that cannot be opened is worse than drawing the root, and the gateway resolves
        // it the same way, so the two surfaces never disagree about who heads a family.
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        var leaf = fixture.NewId();
        fixture.WriteFamily(root, 3, fixture.Member(root, 1, "the-root", sessionId: root), fixture.Member(root, 2, "leaf-two", sessionId: leaf));
        fixture.WritePointer(root, rootSessionId: root, patriarchSessionId: fixture.NewId());

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        Assert.AreEqual(root, family.PatriarchSessionId);
        Assert.IsFalse(family.Delegated);
    }

    [TestMethod]
    public void ReadsTheCreatorsPointerAndNotAMembers()
    {
        // Only the creator's own pointer is the authority; every other member's pointer is a republished copy.
        // A copy that has gone stale - the exact thing this app has to be right about when a patriarch moves -
        // must not be able to move the head on its own.
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        var leaf = fixture.NewId();
        var stale = fixture.NewId();
        fixture.WriteFamily(root, 3, fixture.Member(root, 1, "the-root", sessionId: root), fixture.Member(root, 2, "leaf-two", sessionId: leaf));
        fixture.WritePointer(root, rootSessionId: root, patriarchSessionId: leaf);
        fixture.WritePointer(leaf, rootSessionId: root, patriarchSessionId: stale);

        var store = new LeafFamilyStore(fixture.Options);

        Assert.AreEqual(leaf, store.Families.Single().PatriarchSessionId);
    }

    [TestMethod]
    public void ADesignationNamingASessionNoLongerPresentStillFallsBackToTheCreator()
    {
        // The creator row stays even when every leaf of the family is gone - a family of one is a family.
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        fixture.WriteFamily(root, 2, fixture.Member(root, 1, "the-root", sessionId: root));
        fixture.WritePointer(root, rootSessionId: root, patriarchSessionId: fixture.NewId());

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        Assert.AreEqual(root, family.PatriarchSessionId);
        Assert.IsTrue(family.IsSingleMember);
    }

    // ---------- the creator that is not in the file ----------

    // The creator became an ordinary member row only recently: `ensureCreatorMember` writes its row when a
    // session opens the family. A family that has not been opened since has no row for its own root, and the
    // gateway synthesizes one when it reads. 7 of the 123 families on this box are in exactly that state.
    [TestMethod]
    public void SynthesizesTheCreatorWhenTheFamilyHasNoRowForIt()
    {
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        var leaf = fixture.NewId();
        fixture.WriteFamily(root, 2, fixture.Member(root, 1, "leaf-one", sessionId: leaf));
        fixture.WritePointer(root, rootSessionId: root, rootName: "Mux-Leaves");

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        var creator = store.MemberFor(root);
        Assert.IsNotNull(creator, "the chat the family is named after must resolve to its family");
        Assert.IsTrue(creator!.IsCreator);
        Assert.AreEqual(root, creator.ParentSessionId);
        Assert.AreEqual(2, creator.Number, "the creator joins late, so it takes the family's next number");
        Assert.AreEqual("Mux-Leaves", creator.Name);
        Assert.IsFalse(creator.IsClosed);
        Assert.AreEqual(root, family.PatriarchSessionId);
        Assert.IsFalse(family.Delegated);
        Assert.AreEqual(2, family.Members.Count);
        Assert.IsFalse(family.IsSingleMember);
        Assert.AreEqual("Mux-Leaves", family.RootName);
    }

    [TestMethod]
    public void ASynthesizedCreatorCanBeTheDesignatedHead()
    {
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        var leaf = fixture.NewId();
        fixture.WriteFamily(root, 2, fixture.Member(root, 1, "leaf-one", sessionId: leaf));
        fixture.WritePointer(root, rootSessionId: root, patriarchSessionId: leaf);

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        Assert.IsTrue(family.Delegated);
        Assert.AreEqual(leaf, family.PatriarchSessionId);
        Assert.AreEqual(root, family.Others.Single().SessionId);
    }

    [TestMethod]
    public void NamesTheFamilyAfterThePointerRatherThanTheMemberRow()
    {
        // A family can be renamed after its file was written, and the pointer is where the current name lives.
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        fixture.WriteFamily(root, 2, fixture.Member(root, 1, "a-stale-name", sessionId: root));
        fixture.WritePointer(root, rootSessionId: root, rootName: "Mux-Leaves");

        var store = new LeafFamilyStore(fixture.Options);

        Assert.AreEqual("Mux-Leaves", store.Families.Single().RootName);
    }

    [TestMethod]
    public void AFamilyWithNoNameAnywhereIsStillHeadedByASession()
    {
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        fixture.WriteFamily(root, 2, fixture.Member(root, 1, "", sessionId: fixture.NewId()));

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        Assert.AreEqual("", family.RootName);
        Assert.AreEqual(root, family.Patriarch!.SessionId, "the head is a session, not a name");
    }

    // ---------- the pointer directory holds more than pointers ----------

    [TestMethod]
    public void DoesNotReadALaunchRecordAsAPointer()
    {
        // `index/` is shared: a launch ledger named `<id>.json` sits beside a family's pointer. It carries no
        // rootSessionId, so it is not a pointer - and a reader that took one for a pointer would read this
        // family's name and head out of another record entirely.
        using var fixture = new FamilyFixture();
        var root = fixture.NewId();
        fixture.WriteFamily(root, 2, fixture.Member(root, 1, "the-root", sessionId: root));
        fixture.WriteRaw(
            Path.Combine(fixture.IndexDir, root + ".json"),
            """{"version":1,"assignments":[],"parentSessionId":"not-a-pointer","patriarchSessionId":"a-session-in-no-family"}""");

        var store = new LeafFamilyStore(fixture.Options);
        var family = store.Families.Single();

        Assert.AreEqual(root, family.PatriarchSessionId, "the head falls back to the root, not to a field in a ledger");
        Assert.AreEqual("the-root", family.RootName, "the name comes from the member row when there is no pointer");
    }

    // ---------- which config home is read ----------

    [TestMethod]
    public void ReadsTheConfigHomeTheGatewayWouldRead()
    {
        using var configured = new FamilyFixture();
        using var explicitDir = new FamilyFixture();
        var configuredRoot = configured.NewId();
        var explicitRoot = explicitDir.NewId();
        configured.WriteFamily(configuredRoot, 2, configured.Member(configuredRoot, 1, "from-env", sessionId: configuredRoot));
        explicitDir.WriteFamily(explicitRoot, 2, explicitDir.Member(explicitRoot, 1, "from-override", sessionId: explicitRoot));

        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            // A PTY harness points CLAUDE_CONFIG_DIR at a temp root and the gateway reads its home from there,
            // so a reader that hardcoded ~/.claude reports a family that exists as missing.
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", configured.ConfigHome);
            Assert.AreEqual(configuredRoot, new LeafFamilyStore(new LeafFamilyStore.Options()).Families.Single().RootSessionId);

            var overridden = new LeafFamilyStore(new LeafFamilyStore.Options { DirectoryOverride = explicitDir.FamilyDir });
            Assert.AreEqual(explicitRoot, overridden.Families.Single().RootSessionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }

    [TestMethod]
    public void FallsBackToTheUsersOwnClaudeDirectoryWhenNothingIsSet()
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            var options = new LeafFamilyStore.Options();
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "leaf-families");

            Assert.AreEqual(expected, options.EffectiveDirectory);
            Assert.AreEqual(Path.Combine(expected, "index"), options.EffectiveIndexDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }

    // ---------- fixtures ----------

    // A temp config home laid out the way the gateway lays one out: `leaf-families/` with the family files
    // and `leaf-families/index/` with the per-session pointers.
    private sealed class FamilyFixture : IDisposable
    {
        public FamilyFixture(bool directoryExists = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "clr-leaffamily-" + Guid.NewGuid().ToString("N"));
            ConfigHome = Path.Combine(Root, ".claude");
            FamilyDir = Path.Combine(ConfigHome, "leaf-families");
            IndexDir = Path.Combine(FamilyDir, "index");
            if (directoryExists) Directory.CreateDirectory(IndexDir);
            Options = new LeafFamilyStore.Options { ConfigHome = ConfigHome };
        }

        public string Root { get; }
        public string ConfigHome { get; }
        public string FamilyDir { get; }
        public string IndexDir { get; }
        public LeafFamilyStore.Options Options { get; }

        public string NewId() => Guid.NewGuid().ToString();

        // One member object as the gateway writes it, with only the fields this reader takes.
        public string Member(string parent, int number, string name, string? sessionId = null, string status = "open") =>
            "{\"sessionId\":\"" + (sessionId ?? NewId()) + "\",\"parentSessionId\":\"" + parent + "\",\"number\":" + number
            + ",\"name\":\"" + name + "\",\"transcriptPath\":\"C:\\\\transcripts\\\\" + number + ".jsonl\""
            + ",\"forkMessageId\":null,\"createdAt\":\"2026-09-06T22:08:28.789Z\",\"status\":\"" + status + "\",\"cwd\":\"C:\\\\work\"}";

        // A family file, members in the order given - including a creator row when a test wants one.
        public void WriteFamily(string rootSessionId, int nextNumber, params string[] members) =>
            WriteRaw(
                Path.Combine(FamilyDir, rootSessionId + ".json"),
                "{\"version\":1,\"rootSessionId\":\"" + rootSessionId + "\",\"nextNumber\":" + nextNumber
                + ",\"members\":[" + string.Join(",", members) + "],\"returnedContextIds\":[]}");

        // The loose per-session pointer. `patriarchSessionId` is written only when a test sets a designation.
        public void WritePointer(string sessionId, string rootSessionId, string? rootName = null, string? patriarchSessionId = null)
        {
            var json = "{\"rootSessionId\":\"" + rootSessionId
                + "\",\"rootTranscriptPath\":\"C:\\\\transcripts\\\\root.jsonl\",\"rootCwd\":\"C:\\\\work\"";
            if (rootName is not null) json += ",\"rootName\":\"" + rootName + "\"";
            if (patriarchSessionId is not null) json += ",\"patriarchSessionId\":\"" + patriarchSessionId + "\"";
            WriteRaw(Path.Combine(IndexDir, sessionId + ".json"), json + "}");
        }

        public void WriteRaw(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
