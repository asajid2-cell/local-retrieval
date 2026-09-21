using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

/// <summary>
/// The web Copy menu posts {sessionId, tool, mode} to /api/discovery/copy. That route resolves the
/// chat with ResolveSessionByIdOrAlias - which matches an id case-insensitively AND accepts an alias
/// or a source-path fragment - and then required the resolved chat's Id to equal the request's
/// sessionId EXACTLY (ordinal). A chat reached by any of those routes therefore resolved
/// successfully and was then rejected, so Copy failed for exactly the chats the resolver was built
/// to find.
///
/// These tests pin the contract the route depends on: the identity check must be the same notion of
/// identity the resolver used. They live here rather than in an HTTP test because the route is a
/// thin wrapper and the resolver is where the defect is.
/// </summary>
[TestClass]
public sealed class CopyRouteIdentityTests
{
    private static ArchiveSession Chat(string id, string tool = "claude", params string[] aliases)
    {
        var session = new ArchiveSession
        {
            Id = id,
            Tool = tool,
            Title = "a chat",
            Workspace = "/tmp",
            WorkspaceName = "fixture",
            SourcePath = "/tmp/" + id + ".jsonl",
        };
        foreach (var alias in aliases) session.Aliases.Add(alias);
        return session;
    }

    /// <summary>The route's guard, called directly. It used to be mirrored here, which made these tests
    /// blind to the route: the helper kept its own copy of the ordinal comparison and went on failing
    /// after the route was fixed. The guard lives in production code now for exactly that reason.</summary>
    private static ArchiveSession? ResolveForCopy(ArchiveService service, string requestedId, string tool)
        => service.ResolveStoredSessionByIdOrAlias(requestedId, tool);

    [TestMethod]
    public void AnAliasIdentifiesItsOwnChat_SoCopyMustNotRejectIt()
    {
        var service = new ArchiveService();
        var chat = Chat("claude-aaaaaaaa-1111", aliases: "legacy-alias-2222");
        service.Store.Sessions[chat.Id] = chat;

        // The resolver is explicitly documented to accept an alias; the route must agree.
        Assert.AreSame(chat, service.ResolveSessionByIdOrAlias("legacy-alias-2222", "claude"),
            "the resolver no longer accepts an alias, which this test's premise depends on");
        Assert.AreSame(chat, ResolveForCopy(service, "legacy-alias-2222", "claude"),
            "a chat addressed by its own alias was refused by the copy identity check");
    }

    [TestMethod]
    public void ADifferingCaseIsTheSameChat_SoCopyMustNotRejectIt()
    {
        var service = new ArchiveService();
        var chat = Chat("claude-AbCdEf12-3333");
        service.Store.Sessions[chat.Id] = chat;

        Assert.AreSame(chat, ResolveForCopy(service, "claude-abcdef12-3333", "claude"),
            "the resolver matched case-insensitively but the copy identity check did not");
    }

    [TestMethod]
    public void TheListedIdRoundTripsExactly()
    {
        // The ordinary path: the page sends back the id it was given. This must keep working.
        var service = new ArchiveService();
        var chat = Chat("claude-99999999-4444");
        service.Store.Sessions[chat.Id] = chat;

        Assert.AreSame(chat, ResolveForCopy(service, chat.Id, "claude"));
    }

    [TestMethod]
    public void AnUnknownIdIsStillRefused()
    {
        // The guard must not become a rubber stamp: a genuinely absent chat is still not found.
        var service = new ArchiveService();
        var chat = Chat("claude-77777777-5555");
        service.Store.Sessions[chat.Id] = chat;

        Assert.IsNull(ResolveForCopy(service, "claude-00000000-0000", "claude"));
    }

    [TestMethod]
    public void AToolMismatchIsStillRefused()
    {
        var service = new ArchiveService();
        var chat = Chat("claude-55555555-6666", tool: "claude");
        service.Store.Sessions[chat.Id] = chat;

        Assert.IsNull(ResolveForCopy(service, chat.Id, "codex"));
    }

    [TestMethod]
    public void AChatThatIsNotStoredIsRefusedEvenIfItsSourcePathMatches()
    {
        // SessionHasIdOrAlias also matches a source-path fragment. That is a resolution convenience,
        // not authority to copy a chat the store does not hold, so the guard must still bind the
        // request to a session the service actually knows.
        var service = new ArchiveService();
        var stray = Chat("claude-33333333-7777");
        // deliberately NOT added to Store.Sessions

        Assert.IsNull(ResolveForCopy(service, stray.Id, "claude"),
            "an unstored chat was accepted by the copy identity check");
    }
}
