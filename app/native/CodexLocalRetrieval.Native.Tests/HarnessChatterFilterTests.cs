using System.Collections.ObjectModel;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// The list's default visibility policy also covers harness chatter: an opening turn that is an
// INSTRUCTION to an agent rather than a conversation (leaf liveness probes, tandem token probes, the
// harness's own fixtures) and the stubs a leaf worker leaves behind. Every shape below is copied from
// the live store, sizes and all — the guard values were read off the real corpus, not guessed.
[TestClass]
public sealed class HarnessChatterFilterTests
{
    private static ArchiveSession Chat(string title, string opening, int userMessages, int messages) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Tool = "claude",
        Title = title,
        FirstUserMessage = opening,
        UserMessageCount = userMessages,
        MessageCount = messages,
    };

    // ---- hidden: the instruction identifies the session -------------------------------------------------

    // A leaf liveness probe. Nine user turns, not one, which is exactly why the one-off rule misses it.
    [TestMethod]
    public void LeafLivenessProbeIsHidden()
        => Assert.IsTrue(ArchiveService.IsLowSignalChat(
            Chat("Reply with LEAF_LOCAL_PROOF", "Reply with LEAF_LOCAL_PROOF", 9, 15)));

    // A leaf context handoff stub: the harness opens a worker, hands it another leaf's context, and it
    // never becomes a conversation.
    [TestMethod]
    public void LeafContextHandoffStubIsHidden()
        => Assert.IsTrue(ArchiveService.IsLowSignalChat(
            Chat("Leaf 3: Original", "Explicit context return from Leaf 1: Source (0c52bf87-8c6c-445c-b2b1-b2e087d3b5c9)", 2, 2)));

    // The harness's own fixture prompts.
    [TestMethod]
    public void HarnessFixtureIsHidden()
    {
        Assert.IsTrue(ArchiveService.IsLowSignalChat(
            Chat("Leaf 8: leaf1-queue", "Queue probe step 1. Use the Bash tool to run `sleep 20` (timeout 60000). Then re-run.", 2, 6)));
        Assert.IsTrue(ArchiveService.IsLowSignalChat(
            Chat("Follow these steps in order and nothing else. Step 1", "Follow these steps in order and nothing else. Step 1: call the Skill tool", 3, 7)));
    }

    // The tandem/bridge token probes sit at the top of the size range this rule accepts (90 messages).
    [TestMethod]
    public void TandemTokenProbeIsHidden()
        => Assert.IsTrue(ArchiveService.IsLowSignalChat(
            Chat("Reply with exactly: TANDEM_FRESH_OK and the cu", "Reply with exactly: TANDEM_FRESH_OK and the current working directory. One line.", 5, 90)));

    // A "Leaf <n>:" title with a two-turn body and an opening that is not itself an instruction: only
    // the stub arm can catch this one.
    [TestMethod]
    public void LeafTitleStubIsHidden()
        => Assert.IsTrue(ArchiveService.IsLowSignalChat(Chat("Leaf 3: Original", "ok", 2, 2)));

    // A session containing nothing but "ping".
    [TestMethod]
    public void BarePingChatIsHidden()
        => Assert.IsTrue(ArchiveService.IsLowSignalChat(Chat("ping", "ping", 1, 2)));

    // ---- visible: real work, and anything the user deliberately kept -------------------------------------

    // 2375 messages of real leaf work under the same "Leaf <n>:" naming. The title must NOT be enough.
    [TestMethod]
    public void RealLeafWorkStaysVisible()
        => Assert.IsFalse(ArchiveService.IsLowSignalChat(
            Chat("Leaf 4: ProductShape", "This session is being continued from a previous conversation that ran out of context.", 289, 2375)));

    // "ping" as an opener to a real question: five turns, so the stub arm's guard does not reach it.
    [TestMethod]
    public void RealChatThatOpensWithPingStaysVisible()
        => Assert.IsFalse(ArchiveService.IsLowSignalChat(
            Chat("explain some cpp syntax to me,", "ping", 5, 7)));

    // The markers are only read at the START of the opening turn, so a real chat that mentions the
    // probe vocabulary mid-sentence is untouched.
    [TestMethod]
    public void ChatThatMentionsTheMarkersMidSentenceStaysVisible()
        => Assert.IsFalse(ArchiveService.IsLowSignalChat(
            Chat("compare the two plans", "compare these two plans, then reply with the one you would ship", 6, 40)));

    // ---- deliberate keeps: the same exemptions the one-off rule honour ----------------------------------

    [TestMethod]
    public void PinnedTaggedAndCodenameProbesStayVisible()
    {
        var pinned = Chat("Reply with LEAF_LOCAL_PROOF", "Reply with LEAF_LOCAL_PROOF", 9, 15);
        pinned.Pinned = true;
        Assert.IsFalse(ArchiveService.IsLowSignalChat(pinned), "a pinned probe is a deliberate keep");

        var tagged = Chat("Reply with LEAF_LOCAL_PROOF", "Reply with LEAF_LOCAL_PROOF", 9, 15);
        tagged.Tags = new ObservableCollection<string> { "keepme" };
        Assert.IsFalse(ArchiveService.IsLowSignalChat(tagged), "a user tag is a deliberate keep");

        var stashed = Chat("Reply with LEAF_LOCAL_PROOF", "Reply with LEAF_LOCAL_PROOF", 9, 15);
        stashed.SpecialPhrases = new ObservableCollection<string> { "kd-37" };
        Assert.IsFalse(ArchiveService.IsLowSignalChat(stashed), "a codename is a deliberate keep");

        var archived = Chat("Reply with LEAF_LOCAL_PROOF", "Reply with LEAF_LOCAL_PROOF", 9, 15);
        archived.Archived = true;
        Assert.IsFalse(ArchiveService.IsLowSignalChat(archived), "an archived chat is already out of the active list");
    }

    // The reserved auto-tags sit on every chat, so they must never read as a deliberate keep.
    [TestMethod]
    public void ReservedAutoTagsDoNotKeepAProbe()
    {
        var probe = Chat("Reply with LEAF_LOCAL_PROOF", "Reply with LEAF_LOCAL_PROOF", 9, 15);
        probe.Tags = new ObservableCollection<string> { "archive", "code" };
        Assert.IsTrue(ArchiveService.IsLowSignalChat(probe));
    }

    // The one-off rule this was added to is unchanged, including its tiny-transcript guard.
    [TestMethod]
    public void OneOffRuleIsUnchanged()
    {
        Assert.IsTrue(ArchiveService.IsLowSignalChat(Chat("hello", "hello there", 1, 8)));
        Assert.IsFalse(ArchiveService.IsLowSignalChat(Chat("hello", "hello there", 1, 9)),
            "a large transcript is never a one-off, even with a single prompt");
    }

    // ---- the list actually applies it, and the toggle still reveals it ----------------------------------

    [TestMethod]
    public async Task SidebarListHidesHarnessChatterAndTheToggleRevealsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-harness-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            // The key is the session Id the list filters on, so it must match the id the assertions use.
            var probe = Chat("Reply with LEAF_LOCAL_PROOF", "Reply with LEAF_LOCAL_PROOF", 9, 15);
            probe.Id = "probe";
            service.Store.Sessions["probe"] = probe;
            var real = Chat("Fix the scroll paging", "the transcript stops loading older messages", 12, 400);
            real.Id = "real";
            service.Store.Sessions["real"] = real;

            var hidden = service.FilterChats(new ChatFilter());
            Assert.IsFalse(hidden.Any(s => s.Id == "probe"), "the probe must not be in the default list");
            Assert.IsTrue(hidden.Any(s => s.Id == "real"), "real work stays");

            var revealed = service.FilterChats(new ChatFilter { ShowHidden = true });
            Assert.IsTrue(revealed.Any(s => s.Id == "probe"), "the Show hidden toggle still reveals it");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
