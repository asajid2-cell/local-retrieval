using System.Collections.ObjectModel;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class DiscoveryApiTests
{
    private static ArchiveSession Chat(
        string id,
        string title,
        string tool,
        string updatedAt,
        int userMessages,
        params string[] tags) => new()
    {
        Id = id,
        Title = title,
        Tool = tool,
        UpdatedAt = updatedAt,
        CreatedAt = updatedAt,
        Workspace = Path.Combine(Path.GetTempPath(), "projects", title.Replace(' ', '-')),
        WorkspaceName = title.Replace(' ', '-').ToLowerInvariant(),
        SourcePath = Path.Combine(Path.GetTempPath(), id + ".jsonl"),
        UserMessageCount = userMessages,
        MessageCount = Math.Max(10, userMessages * 2),
        Tags = new ObservableCollection<string>(tags),
    };

    private static (ArchiveService archive, DiscoveryApi api) Fixture()
    {
        var archive = new ArchiveService(useBundledStore: true);
        var alpha = Chat("a", "Alpha Work", "codex", "2026-08-02T10:00:00Z", 10, "active", "web");
        alpha.Pinned = true;
        alpha.SpecialPhrases.Add("web-parity");
        var beta = Chat("b", "Beta Notes", "claude", "2026-08-01T10:00:00Z", 3, "active", "notes");
        beta.SpecialPhrases.Add("web-parity");
        var hidden = Chat("c", "One Shot", "codex", "2026-07-31T10:00:00Z", 1);
        hidden.MessageCount = 2;
        var unsafeChat = Chat("bad id", "Unsafe Resume", "codex", "2026-07-30T10:00:00Z", 5, "web");

        foreach (var chat in new[] { alpha, beta, hidden, unsafeChat })
            archive.Store.Sessions[chat.Id] = chat;
        archive.Store.Collections["project-web"] = new ArchiveCollection
        {
            Id = "project-web",
            Name = "Web Project",
            SessionIds = new List<string> { "a", "bad id" },
        };
        archive.Store.Collections["project-notes"] = new ArchiveCollection
        {
            Id = "project-notes",
            Name = "Notes",
            SessionIds = new List<string> { "b" },
        };

        return (archive, new DiscoveryApi(archive, session => session.Id != "bad id"));
    }

    [TestMethod]
    public void Chats_ReusesCompoundFiltersAndDefaultsToHidingOneOffs()
    {
        var (_, api) = Fixture();

        var page = api.Chats(new DiscoveryQuery(
            Include: "active, ACTIVE",
            Exclude: "notes",
            Match: "all",
            Agent: "codex",
            MinUserMessages: 2));

        Assert.AreEqual(1, page.Total);
        Assert.AreEqual("a", page.Rows.Single().Id);
        CollectionAssert.AreEqual(new[] { "active", "web" }, page.Rows.Single().Tags.ToArray());

        var hidden = api.Chats(new DiscoveryQuery());
        CollectionAssert.DoesNotContain(hidden.Rows.Select(row => row.Id).ToList(), "c");
        var revealed = api.Chats(new DiscoveryQuery(ShowHidden: true));
        CollectionAssert.Contains(revealed.Rows.Select(row => row.Id).ToList(), "c");
    }

    [TestMethod]
    public void Chats_ExactPhraseSearchAndStableOffsetPagination()
    {
        var (_, api) = Fixture();

        var first = api.Chats(new DiscoveryQuery(Q: "[web-parity]", Offset: 0, Limit: 1));
        var second = api.Chats(new DiscoveryQuery(Q: "[web-parity]", Offset: 1, Limit: 1));

        Assert.AreEqual(2, first.Total);
        Assert.AreEqual(1, first.Rows.Count);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual("a", first.Rows[0].Id);
        Assert.AreEqual("b", second.Rows[0].Id);
        Assert.IsFalse(second.HasMore);
    }

    [TestMethod]
    public void Chats_EmitsDisplayMetadataAndNeverLocalLaunchData()
    {
        var (_, api) = Fixture();

        var page = api.Chats(new DiscoveryQuery(Project: "project-web", ShowHidden: true));
        var good = page.Rows.Single(row => row.Id == "a");
        var bad = page.Rows.Single(row => row.Id == "bad id");

        Assert.AreEqual("alpha-work", good.WorkspaceLabel);
        Assert.AreEqual("web-parity", good.Phrases.Single());
        Assert.AreEqual(10, good.UserMsgCount);
        Assert.IsTrue(good.Pinned);
        Assert.IsTrue(good.Resumable);
        Assert.IsFalse(bad.Resumable);
        Assert.IsTrue(good.MuxName.Length > 0);

        var json = JsonSerializer.Serialize(page);
        Assert.IsFalse(json.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("workingDirectory", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("\"command\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains(".jsonl", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Facets_CountFilteredTagsPhrasesAndProjects()
    {
        var (_, api) = Fixture();

        var facets = api.Facets(new DiscoveryQuery(Include: "active"));

        Assert.AreEqual(2, facets.Total);
        Assert.AreEqual(1, facets.Hidden);
        Assert.AreEqual(2, facets.Tags.Single(tag => tag.Value == "active").Count);
        Assert.AreEqual(2, facets.Phrases.Single(phrase => phrase.Value == "web-parity").Count);
        Assert.AreEqual(1, facets.Projects.Single(project => project.Id == "project-web").Count);
        Assert.AreEqual(1, facets.Projects.Single(project => project.Id == "project-notes").Count);
    }

    [TestMethod]
    public void Chats_ClampsPagingAndRejectsUnsupportedPresetValues()
    {
        var (_, api) = Fixture();

        var page = api.Chats(new DiscoveryQuery(
            Agent: "other",
            Date: "year",
            MinUserMessages: 4,
            Sort: "random",
            Offset: -20,
            Limit: 999,
            ShowHidden: true));

        Assert.AreEqual(0, page.Offset);
        Assert.AreEqual(DiscoveryApi.MaxLimit, page.Limit);
        Assert.AreEqual(4, page.Total);
    }

    [TestMethod]
    public void Chats_LastAndFirstUserSortsExposeTheMatchingDesktopRowTitle()
    {
        var (archive, api) = Fixture();
        archive.Store.Sessions["a"].LastUserMessage = "Ship the endpoint";
        archive.Store.Sessions["a"].FirstUserMessage = "Start web parity";

        var last = api.Chats(new DiscoveryQuery(Q: "Alpha", Sort: "last-user", ShowHidden: true));
        var first = api.Chats(new DiscoveryQuery(Q: "Alpha", Sort: "first-user", ShowHidden: true));

        Assert.AreEqual("Ship the endpoint", last.Rows.Single().Title);
        Assert.AreEqual("Start web parity", first.Rows.Single().Title);
        Assert.AreEqual(0, api.Chats(new DiscoveryQuery(Project: "missing-project")).Total);
    }
}
