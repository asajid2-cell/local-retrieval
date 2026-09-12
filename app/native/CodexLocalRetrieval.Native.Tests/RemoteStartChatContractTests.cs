namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class RemoteStartChatContractTests
{
    [TestMethod]
    public void StartChat_CommandUsesTheExistingIntentFenceBeforeItsHandler()
    {
        var poller = Slice(RemoteSource(), "private async Task PollCommandsAsync()", "private void ApplyCommandPollInterval");
        AssertOrdered(poller,
            "_commandIntents.Admit(c.type, c.replayPolicy, c.intentId, c.leaseToken, out var gated)",
            "try { outcome = await StartChatHeadlessFromIntentAsync(c); }",
            "if (outcome.Uncertain)",
            "_commandIntents.Release(c.intentId);",
            "continue;",
            "res = (outcome.Ok, outcome.Detail);",
            "_commandIntents.Record(c.intentId, res.ok, res.detail)",
            "await AckCommandAsync(target, port, c.id, ackJson);");
        Assert.IsFalse(poller.Contains("c.type = \"startmux\";", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NativeRename_UsesSyncGateAndExplicitSuccessAndRefusesCodex()
    {
        var handler = Slice(RemoteSource(),
            "else if (string.Equals(c.type, \"rename\", StringComparison.OrdinalIgnoreCase))",
            "else if (c.type is \"setfavorite\"");
        AssertOrdered(handler, "await _syncGate.WaitAsync();",
            "ArchiveRemoteCommands.ExecuteAsync(_archive, command.RootElement)",
            "catch (RemoteCommandUnconfirmedException)", "_commandIntents.Release(c.intentId);", "continue;",
            "finally { _syncGate.Release(); }", "renamed |= res.ok;");
        Assert.IsFalse(handler.Contains("RenameNativeByIdAsync", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("!status.Contains", StringComparison.Ordinal));
        var repaint = Slice(RemoteSource(), "if (killed || renamed || added)", "catch (Exception ex) { Diag.Log(\"PollCommands failed:");
        AssertOrdered(repaint, "await _syncGate.WaitAsync();", "RenderSelectedSessionPane();",
            "finally { _syncGate.Release(); }", "await PushProjectsAsync();");
        Assert.IsFalse(repaint.Contains("SelectFirstSession", StringComparison.Ordinal));
        Assert.IsFalse(repaint.Contains("Navigate(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProjectionAndBinding_UseArchiveGateAndReservePushBeforeAwait()
    {
        var push = Slice(RemoteSource(), "private async Task PushProjectsAsync()", "private async Task PollCommandsAsync()");
        AssertOrdered(push, "_syncPushing = true;", "await _syncGate.WaitAsync();",
            "_archive.BuildProjectsProjectionJson", "await _archive.SaveMuxHistoryIfDirtyAsync();",
            "finally { _syncGate.Release(); }", "await RunSshAsync", "finally { _syncPushing = false; }");
        var tab = Slice(RemoteSource(), "private async void OnTabTick", "private async Task BindPendingMuxIdentityAsync");
        AssertOrdered(tab, "await _syncGate.WaitAsync();", "_archive.ResolvePendingMuxBindings()",
            "finally { _syncGate.Release(); }", "await BindPendingMuxIdentityAsync(binding);");
    }

    [TestMethod]
    public void StartChat_DtoCarriesOnlyPickerIdentitiesAndDisplayMetadata()
    {
        var dto = Slice(RemoteSource(), "private sealed class AppCommand",
            "private async Task<StartChatPreparationResult> StartChatHeadlessFromIntentAsync");
        foreach (var field in new[]
        {
            "checkpointId", "checkpointRevision", "workspaceId", "subfolder", "phrase",
            "deckId", "collectionId", "collectionRevision", "collection", "title", "tool",
            "muxName", "launchMode", "handoffFromId",
        })
            StringAssert.Contains(dto, $"string? {field}");
        Assert.IsFalse(dto.Contains("commandLine", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(dto.Contains("executable", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(dto.Contains("workingDirectory", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CheckpointStart_PersistsMetadataAndFrozenLaunchBeforeDispatch()
    {
        var preparation = Slice(OperationsSource(),
            "public async Task<StartChatPreparationResult> PrepareStartChatAsync",
            "public Task<StartChatPreparationResult> MarkStartChatAppliedAsync");
        AssertOrdered(preparation,
            "await ExecuteBranchOperationAsync(",
            "StartChatSubIntent(normalized.IntentId, \"spawn\")",
            "ApplyPreparedStartChatMetadata(branch, normalized, targetCollectionId);",
            "BuildMultiplexCommand(branch, launchModeOverride: normalized.LaunchMode)",
            "parent.State = \"ready\";",
            "parent.StartChatLaunch = CloneStartChatLaunch(launch);",
            "await SaveAsync();",
            "return new(true, \"ready\"");
        var coordinator = Slice(OperationsSource(), "public static class StartChatCoordinator",
            "public sealed partial class ArchiveService");
        AssertOrdered(coordinator, "var prepared = await prepare();",
            "prepared.State != \"ready\"", "var response = await muxRequest(new",
            "intentId = request.IntentId.Trim()", "await finalize(applied, detail, generation)");
    }

    [TestMethod]
    public void BlankStart_UsesSharedCoordinatorAndRetainsOriginalIntent()
    {
        var handler = Slice(RemoteSource(),
            "private async Task<StartChatPreparationResult> StartChatHeadlessFromIntentAsync",
            "private static bool TryResolveStartSubfolder");
        StringAssert.Contains(handler, "new StartChatPreparationRequest(command.intentId,");
        StringAssert.Contains(handler, "await _archive.PrepareStartChatAsync(request)");
        StringAssert.Contains(handler, "await _archive.MarkStartChatAppliedAsync(request, detail, generation)");
        StringAssert.Contains(handler, "await _archive.MarkStartChatFailedAsync(request, detail)");
        StringAssert.Contains(handler, "StartChatCoordinator.ExecuteAsync(request, Prepare, Finalize, frame => LocalMuxdRequestAsync(frame))");
        AssertOrdered(handler, "async Task<StartChatPreparationResult> Prepare()",
            "await _syncGate.WaitAsync();", "finally { _syncGate.Release(); }",
            "async Task<StartChatPreparationResult> Finalize", "await _syncGate.WaitAsync();",
            "finally { _syncGate.Release(); }");
        foreach (var forbidden in new[] { "Guid.NewGuid", "Process.Start", "SessionLaunchGovernor", "SessionLaunchClaims" })
            Assert.IsFalse(handler.Contains(forbidden, StringComparison.Ordinal), forbidden);
        AssertOrdered(OperationsSource(), "Store.PendingNewChats.Add(new PendingNewChat",
            "IntentId = resultId", "PendingIntentId = resultId", "parent.State = \"ready\";",
            "await SaveAsync();");
    }

    private static string RemoteSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(), "native", "CodexLocalRetrieval.Native", "MainPage.Remote.cs"));

    private static string OperationsSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(), "native", "CodexLocalRetrieval.Core", "Services", "ArchiveService.StartChatOperations.cs"));

    private static void AssertOrdered(string source, params string[] markers)
    {
        var offset = 0;
        foreach (var marker in markers)
        {
            var index = source.IndexOf(marker, offset, StringComparison.Ordinal);
            Assert.IsTrue(index >= 0, $"missing or out-of-order marker: {marker}");
            offset = index + marker.Length;
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"missing marker: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.IsTrue(end > start, $"missing marker: {endMarker}");
        return source[start..end];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexLocalRetrieval.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate CodexLocalRetrieval.sln.");
    }
}
