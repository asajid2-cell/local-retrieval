using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// Every chat carries TWO names. The agent gives itself one (Title - what `claude --resume` / `codex resume`
// list, and what a NATIVE rename rewrites); the app assigns the other (CustomTitle - what a mux rename and
// the web tab-rename's "App name" field write, stored only in this app's store). The list shows the AGENT's
// name by default and puts the app-assigned one behind the "Show MUX names" filter. These tests pin that
// default, the toggle, and the one place they interact (an in-app rename).
[TestClass]
public sealed class ChatNameSourceTests
{
    private static ArchiveSession Chat(string native, string appName) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Tool = "claude",
        Title = native,
        CustomTitle = appName,
    };

    // ---- the default: the agent's own name ----------------------------------------------------------

    // A chat the app renamed still lists under the name the agent carries, until you ask for the app's.
    [TestMethod]
    public void NativeNameIsShownByDefault()
    {
        var chat = Chat("raw agent title", "mux work");

        Assert.AreEqual("raw agent title", chat.RowName);
        Assert.AreEqual("raw agent title", chat.ListTitle);
    }

    [TestMethod]
    public void MuxNameIsShownWhenTheListAsksForIt()
    {
        var chat = Chat("raw agent title", "mux work");
        chat.PreferMuxName = true;

        Assert.AreEqual("mux work", chat.RowName);
        Assert.AreEqual("mux work", chat.ListTitle);
    }

    // The app-assigned override is not deleted, just not the default - the rest of the app still resolves
    // the same name it always did (reader header actions, remote surfaces, agent tool output).
    [TestMethod]
    public void TheAppNameStillWinsForDisplayTitle()
    {
        var chat = Chat("raw agent title", "mux work");

        Assert.AreEqual("mux work", chat.DisplayTitle);
        Assert.IsFalse(chat.PreferMuxName, "the row default is the agent's name");
    }

    // A chat with no name of its own must not render blank when the native source is selected.
    [TestMethod]
    public void NativeNameFallsBackToTheAppNameWhenTheAgentHasNone()
    {
        var chat = Chat("", "mux work");

        Assert.AreEqual("mux work", chat.NativeTitle);
        Assert.AreEqual("mux work", chat.ListTitle);
    }

    // The last/first-user-message sorts name rows after what YOU said; the "no messages" fallback has to
    // follow the same name source as the plain rows.
    [TestMethod]
    public void UserMessageSortsKeepTheSameNameSource()
    {
        var chat = Chat("raw agent title", "mux work");

        chat.RowTitleMode = "last-user";
        Assert.AreEqual("No messages · raw agent title", chat.ListTitle);

        chat.RowTitleMode = "first-user";
        Assert.AreEqual("raw agent title", chat.ListTitle);

        chat.PreferMuxName = true;
        Assert.AreEqual("mux work", chat.ListTitle);
    }

    // The list is a live binding, so flipping the source has to repaint rows already on screen.
    [TestMethod]
    public void FlippingTheNameSourceRepaintsTheRow()
    {
        var chat = Chat("raw agent title", "mux work");
        var changed = new List<string?>();
        chat.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        chat.PreferMuxName = true;

        CollectionAssert.Contains(changed, nameof(ArchiveSession.ListTitle));
        CollectionAssert.Contains(changed, nameof(ArchiveSession.RowName));
    }

    // ---- the interaction: an in-app rename does NOT adopt the new name as the native one -------------

    [TestMethod]
    public async Task AnInAppRenameStoresTheMuxNameAndLeavesTheAgentsNameAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-name-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var chat = Chat("raw agent title", "");
            chat.Id = "chat";
            service.Store.Sessions["chat"] = chat;

            await service.RenameSessionAsync(chat, "mux work");

            Assert.AreEqual("mux work", chat.CustomTitle, "a rename in the app is the app-assigned (mux) name");
            Assert.AreEqual("raw agent title", chat.Title, "the agent's own name is untouched");
            Assert.AreEqual("mux work", chat.DisplayTitle);
            Assert.AreEqual("raw agent title", chat.ListTitle, "so the default list keeps showing the agent's name");

            chat.PreferMuxName = true;
            Assert.AreEqual("mux work", chat.ListTitle, "and the toggle shows the name the app was given");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---- the count behind the toggle label ----------------------------------------------------------

    [TestMethod]
    public void MuxNamedChatCountCountsTheAppAssignedNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-name-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            service.Store.Sessions["named"] = Chat("raw agent title", "mux work");
            service.Store.Sessions["plain"] = Chat("another agent title", "");

            Assert.AreEqual(1, service.MuxNamedChatCount());

            service.Store.Sessions["blank"] = Chat("third agent title", "   ");
            Assert.AreEqual(1, service.MuxNamedChatCount(), "whitespace is not a name");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
