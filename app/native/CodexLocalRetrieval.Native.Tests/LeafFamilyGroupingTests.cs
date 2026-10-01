using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

// Grouping draws each gateway leaf family as the ONE row it is a family of: the patriarch's own chat. These
// pin the two rules that make that safe - a family is only drawn where its head IS a row, and a member is
// only folded into a family that is actually drawn.
[TestClass]
public class LeafFamilyGroupingTests
{
    [TestMethod]
    public void FoldsEveryMemberIntoThePatriarchsRow()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leafOne = box.AddChat("Leaf 1: fork");
        var leafTwo = box.AddChat("Leaf 2: probe");
        box.WriteFamily(root.Id, 4, root.Id, leafOne.Id, leafTwo.Id);

        var rows = box.Collapse(root, leafOne, leafTwo);

        Assert.AreEqual(1, rows.Count);
        Assert.AreSame(root, rows[0], "the family is drawn at the patriarch's own chat, not a new row");
        Assert.IsTrue(rows[0].IsFamilyHead);
        Assert.AreEqual("family of 3", rows[0].FamilyBadge);
        Assert.IsFalse(rows[0].LeafFamily!.Delegated);
    }

    [TestMethod]
    public void LeavesAChatWithNoFamilyAlone()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        var loner = box.AddChat("An ordinary chat");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var rows = box.Collapse(root, leaf, loner);

        CollectionAssert.AreEquivalent(new[] { root, loner }, rows.ToList());
        Assert.IsFalse(loner.IsFamilyHead, "a chat in no family is not a family row");
        Assert.AreEqual("", loner.FamilyBadge);
    }

    // A family whose head is filtered out of view is not drawn at all - its members go with it. That IS "the
    // family goes wherever the patriarch is": the patriarch's tier is the family's tier.
    [TestMethod]
    public void DrawsNothingWhenThePatriarchIsNotAmongTheRows()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        // The tier filter handed over the leaf but not the root, which is what "the patriarch is archived"
        // looks like from the Active tab.
        var rows = box.Collapse(leaf);

        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public void KeepsMembersScatteredWhenTheFamilyHasNoHeadChatAtAll()
    {
        // 85 of the 123 families on this box name a creator whose chat is nowhere the app can see. There is no
        // row to draw such a family at, and inventing one would put a chat in the list that cannot be opened.
        using var box = new GroupingBox();
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily("a-creator-that-is-not-a-chat", 2, leaf.Id);

        var rows = box.Collapse(leaf);

        CollectionAssert.AreEquivalent(new[] { leaf }, rows.ToList());
        Assert.IsFalse(leaf.IsFamilyHead);
    }

    [TestMethod]
    public void FollowsTheDesignationToWhicheverMemberHoldsItNow()
    {
        // The whole reason the collapse is derived and never stored: when a patriarch leaves, the gateway hands
        // the family down and republishes the pointers, and the row has to move with it.
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var atRoot = box.Collapse(root, leaf);
        Assert.AreSame(root, atRoot.Single(), "with no designation the creator is the head");

        // The designation moves - a patriarch left, or the title was conferred - and the next read has to put
        // the family's row on the member that holds it now, not on the one that held it first.
        box.WritePointer(root.Id, root.Id, patriarchSessionId: leaf.Id);
        var atLeaf = box.Collapse(root, leaf);
        Assert.AreSame(leaf, atLeaf.Single());
        Assert.IsTrue(atLeaf[0].IsFamilyHead);
        Assert.AreEqual("family of 2", atLeaf[0].FamilyBadge);
    }

    [TestMethod]
    public void AFamilyOfOneIsLeftAsItIs()
    {
        using var box = new GroupingBox();
        var only = box.AddChat("A chat that spawned nothing");
        box.WriteFamily(only.Id, 2, only.Id);

        var rows = box.Collapse(only);

        CollectionAssert.AreEquivalent(new[] { only }, rows.ToList());
        Assert.IsFalse(only.IsFamilyHead, "one chat is already one row; a badge would say nothing");
    }

    [TestMethod]
    public void TurningGroupingOffGivesEveryChatBack()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var grouped = box.Collapse(root, leaf);
        Assert.AreEqual(1, grouped.Count);

        var ungrouped = box.Collapse(enabled: false, root, leaf);
        CollectionAssert.AreEquivalent(new[] { root, leaf }, ungrouped.ToList());
        Assert.IsFalse(root.IsFamilyHead, "the stamp from the grouped pass must not survive the toggle");
        Assert.IsFalse(leaf.IsFamilyHead);
    }

    [TestMethod]
    public void FindsTheHeadThroughOneOfItsAliasIds()
    {
        // A chat is resumed under a new id, so a store row answers to several. A family named after the id it
        // was created with must still find the row it belongs to.
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        var createdAt = root.Id;
        box.Reidentify(root, "resumed-id");
        box.WriteFamily(createdAt, 3, createdAt, leaf.Id);

        var rows = box.Collapse(root, leaf);

        Assert.AreSame(root, rows.Single());
        Assert.IsTrue(root.IsFamilyHead);
    }

    // ---------- following a folded chat, and the flat-list leaf mark ----------

    // A chat the collapse folded away is not a row any more, so anything still pointing at it - the
    // selection, a restore after a sync - has to be able to ask where its family went.
    [TestMethod]
    public void HandsAFoldedMemberTheRowItsFamilyIsDrawnAt()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var rows = box.Collapse(root, leaf);

        Assert.AreSame(root, box.Service.FamilyRowFor(rows, leaf), "the folded leaf resolves to the family's row");
        Assert.AreSame(root, box.Service.FamilyRowFor(rows, root), "the head resolves to its own row");
        Assert.IsNull(box.Service.FamilyRowFor(rows, box.AddChat("An ordinary chat")),
            "a chat in no family has no family row to follow");
    }

    [TestMethod]
    public void HasNoFamilyRowWhenGroupingIsOff()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var flat = box.Collapse(enabled: false, root, leaf);

        Assert.IsNull(box.Service.FamilyRowFor(flat, leaf), "nothing was folded, so there is nothing to follow");
    }

    // Flat mode still says which rows are leaves, and where their patriarch is, because a leaf is not a chat
    // in its own right: what you want from it is the family it belongs to.
    [TestMethod]
    public void MarksLeafRowsInAFlatListAndNeverThePatriarch()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var flat = box.Collapse(enabled: false, root, leaf);

        CollectionAssert.AreEquivalent(new[] { root, leaf }, flat.ToList());
        Assert.IsTrue(leaf.IsLeaf);
        Assert.AreEqual("❧", leaf.LeafGlyph);
        Assert.AreEqual(root.Id, leaf.MemberFamily!.PatriarchSessionId);
        Assert.IsFalse(root.IsLeaf, "the patriarch is the row a family is drawn at, not a leaf of one");
        Assert.AreEqual("", root.LeafGlyph);
    }

    // The head is a chat this app holds, so the family has somewhere to go - but the head's ROW is not in
    // this view. The leaf still knows its patriarch, which is all the glyph needs to take you there.
    [TestMethod]
    public void MarksALeafEvenWhenItsPatriarchsRowIsFilteredOut()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var flat = box.Collapse(enabled: false, leaf);

        Assert.IsTrue(leaf.IsLeaf);
        Assert.AreEqual(root.Id, leaf.MemberFamily!.PatriarchSessionId);
    }

    [TestMethod]
    public void LeavesALeafUnmarkedWhenThereIsNoPatriarchChatToGoTo()
    {
        // 85 of the 123 families on this box name a creator whose chat is nowhere the app can see. A leaf of
        // one has no patriarch to take you to, so it is drawn as the ordinary chat it looks like.
        using var box = new GroupingBox();
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily("a-creator-that-is-not-a-chat", 2, leaf.Id);

        var flat = box.Collapse(enabled: false, leaf);

        Assert.IsFalse(leaf.IsLeaf);
        Assert.AreEqual("", leaf.LeafGlyph);
    }

    // ---------- the roster the badge opens ----------

    [TestMethod]
    public void NamesRosterRowsFromTheStoreNotTheFamilyFile()
    {
        // The family file carries whatever the tool had at fork time - real families on this box say
        // "Conversation" and "1" - so the roster reads the name the app lists each chat under.
        using var box = new GroupingBox();
        var root = box.AddChat("350-workthrough");
        var leaf = box.AddChat("Leaf 1: 350-labs");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var family = box.Service.LeafFamilies.FamilyFor(root.Id)!;

        var head = family.Members.Single(m => m.SessionId == root.Id);
        var member = family.Members.Single(m => m.SessionId == leaf.Id);
        Assert.AreEqual("350-workthrough", head.DisplayName);
        Assert.AreEqual("Leaf 1: 350-labs", member.DisplayName);
        Assert.AreEqual("1. 350-workthrough  · patriarch", head.RosterLine);
        Assert.AreEqual("2. Leaf 1: 350-labs", member.RosterLine);
    }

    // A chat can carry two names - the tool's own and the app-assigned one - and the list shows one of
    // them. The roster has to name the chat the way the chat's own row names it, or the family would call
    // a chat something the list never calls it.
    [TestMethod]
    public void NamesRosterRowsTheSameWayTheRowIsNamed()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("350-workthrough");
        var leaf = box.AddChat("Leaf 1: 350-labs");
        root.CustomTitle = "archiver";
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var head = box.Service.LeafFamilies.FamilyFor(root.Id)!.Members.Single(m => m.SessionId == root.Id);

        Assert.AreEqual(root.RowName, head.DisplayName, "the roster names the chat the way its own row does");
        Assert.AreEqual("350-workthrough", head.DisplayName, "which is the tool's own name while app names are off");
        Assert.AreEqual("1. 350-workthrough  · patriarch", head.RosterLine);
    }

    [TestMethod]
    public void OpensAndShutsTheFamilyRosterOnTheRow()    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leafOne = box.AddChat("Leaf 1: fork");
        var leafTwo = box.AddChat("Leaf 2: probe");
        box.WriteFamily(root.Id, 4, root.Id, leafOne.Id, leafTwo.Id);

        var rows = box.Collapse(root, leafOne, leafTwo);
        var head = rows.Single();

        Assert.IsFalse(head.FamilyExpanded, "a family starts shut - opening it is your move");
        Assert.AreEqual("▸", head.FamilyToggleGlyph);
        Assert.AreEqual(0, head.FamilyRoster.Count);

        head.FamilyExpanded = true;
        Assert.AreEqual("▾", head.FamilyToggleGlyph);
        Assert.AreEqual(3, head.FamilyRoster.Count);
        Assert.AreEqual("1. Mux-Leaves  · patriarch", head.FamilyRoster[0].RosterLine);
        Assert.AreEqual(1, rows.Count, "the roster is a list inside the row, not rows of its own");
    }

    [TestMethod]
    public void MarksThePatriarchOnWhicheverMemberHoldsTheDesignationNow()
    {
        using var box = new GroupingBox();
        var root = box.AddChat("Mux-Leaves");
        var leaf = box.AddChat("Leaf 1: fork");
        box.WriteFamily(root.Id, 3, root.Id, leaf.Id);

        var family = box.Service.LeafFamilies.FamilyFor(root.Id)!;
        Assert.IsTrue(family.Members.Single(m => m.SessionId == root.Id).IsPatriarch);

        box.WritePointer(root.Id, root.Id, patriarchSessionId: leaf.Id);
        family = box.Service.LeafFamilies.FamilyFor(root.Id)!;

        var member = family.Members.Single(m => m.SessionId == leaf.Id);
        Assert.IsTrue(member.IsPatriarch, "the mark follows the designation, like the row does");
        Assert.IsFalse(family.Members.Single(m => m.SessionId == root.Id).IsPatriarch);
        Assert.AreEqual("2. Leaf 1: fork  · patriarch", member.RosterLine);
    }

    // A family's pointer names the id the chat was CREATED with, and a resume hands the chat a new one - so
    // the chat a leaf points at has to be found by any id it answers to, not only its current one.
    [TestMethod]
    public void FindsAChatByAnIdItWasResumedUnder()
    {
        using var box = new GroupingBox();
        var chat = box.AddChat("A resumed chat");
        var createdAt = chat.Id;
        box.Reidentify(chat, "resumed-id");
        box.Reload();

        Assert.AreSame(chat, box.Service.FindChat(createdAt), "an alias resolves to the chat that answers to it");
        Assert.AreSame(chat, box.Service.FindChat("resumed-id"));
        Assert.IsNull(box.Service.FindChat("nothing-answers-to-this"));
    }

    // ---------- fixture ----------

    private sealed class GroupingBox : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "clr-grouping-" + Guid.NewGuid().ToString("N"));

        public GroupingBox()
        {
            LeafDir = Path.Combine(_root, "leaf-families");
            Directory.CreateDirectory(Path.Combine(LeafDir, "index"));
            Service = new ArchiveService(
                storePath: Path.Combine(_root, "app-store.json"),
                useBundledStore: false,
                codexSessionsRoot: Path.Combine(_root, "codex"),
                claudeSessionsRoot: Path.Combine(_root, "projects"),
                codexStateDbPath: Path.Combine(_root, "state.sqlite"),
                templatesRoot: Path.Combine(_root, "templates"),
                transcriptSearchIndexPath: Path.Combine(_root, "search.sqlite"),
                enableTranscriptSearchIndex: false,
                restrictTranscriptSources: true,
                leafFamiliesDirectory: LeafDir);
        }

        public ArchiveService Service { get; }
        public string LeafDir { get; }

        public void Reload() => Service.ReloadLeafFamilies();

        public ArchiveSession AddChat(string name)
        {
            var session = new ArchiveSession { Id = Guid.NewGuid().ToString(), Title = name };
            Service.Store.Sessions[session.Id] = session;
            return session;
        }

        // A resume hands the chat a new current id and keeps the old one as an alias.
        public void Reidentify(ArchiveSession session, string newId)
        {
            Service.Store.Sessions.Remove(session.Id);
            session.Aliases.Add(session.Id);
            session.Id = newId;
            Service.Store.Sessions[newId] = session;
        }

        // A family file as the gateway writes it: the root's row is a member like any other.
        public void WriteFamily(string rootSessionId, int nextNumber, params string[] members)
        {
            var rows = members.Select((id, i) =>
                "{\"sessionId\":\"" + id + "\",\"parentSessionId\":\"" + rootSessionId + "\",\"number\":" + (i + 1)
                + ",\"name\":\"member " + (i + 1) + "\",\"transcriptPath\":\"C:\\\\t\\\\" + i + ".jsonl\","
                + "\"forkMessageId\":null,\"createdAt\":\"2026-09-06T22:08:28.789Z\",\"status\":\"open\"}");
            File.WriteAllText(
                Path.Combine(LeafDir, rootSessionId + ".json"),
                "{\"version\":1,\"rootSessionId\":\"" + rootSessionId + "\",\"nextNumber\":" + nextNumber
                + ",\"members\":[" + string.Join(",", rows) + "],\"returnedContextIds\":[]}");
            Service.ReloadLeafFamilies();
        }

        public void WritePointer(string sessionId, string rootSessionId, string? patriarchSessionId = null)
        {
            var pointer = "{\"rootSessionId\":\"" + rootSessionId + "\",\"rootName\":\"Mux-Leaves\"";
            if (patriarchSessionId is not null) pointer += ",\"patriarchSessionId\":\"" + patriarchSessionId + "\"";
            File.WriteAllText(Path.Combine(LeafDir, "index", sessionId + ".json"), pointer + "}");
            Service.ReloadLeafFamilies();
        }

        public IReadOnlyList<ArchiveSession> Collapse(params ArchiveSession[] rows) => Collapse(true, rows);

        public IReadOnlyList<ArchiveSession> Collapse(bool enabled, params ArchiveSession[] rows)
            => Service.CollapseFamilies(rows, enabled);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
