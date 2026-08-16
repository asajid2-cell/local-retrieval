namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class RemoteStartChatContractTests
{
    [TestMethod]
    public void StartChat_CommandUsesTheExistingIntentFenceBeforeItsHandler()
    {
        var source = RemoteSource();
        var poller = source.IndexOf("private async Task PollCommandsAsync()", StringComparison.Ordinal);
        var translation = source.IndexOf(
            "if (string.Equals(c.type, \"startchat\", StringComparison.OrdinalIgnoreCase))",
            poller,
            StringComparison.Ordinal);
        var assignment = source.IndexOf("c.type = \"startmux\";", translation, StringComparison.Ordinal);
        var admission = source.IndexOf(
            "_commandIntents.Admit(c.type, c.replayPolicy, c.intentId, c.leaseToken, out var gated)",
            assignment,
            StringComparison.Ordinal);
        var restoration = source.IndexOf("c.type = commandType;", admission, StringComparison.Ordinal);
        var handler = source.IndexOf(
            "res = await StartChatHeadlessFromIntentAsync(c);",
            poller,
            StringComparison.Ordinal);

        Assert.IsTrue(poller >= 0, "poller marker is missing");
        Assert.IsTrue(translation > poller, "startchat admission translation must be inside the poller");
        Assert.IsTrue(translation < assignment && assignment < admission);
        Assert.IsTrue(admission < restoration && restoration < handler);
    }

    [TestMethod]
    public void StartChat_DtoCarriesOnlyPickerIdentitiesAndDisplayMetadata()
    {
        var source = RemoteSource();
        var dto = Slice(
            source,
            "private sealed class AppCommand",
            "private async Task<(bool ok, string detail)> StartChatHeadlessFromIntentAsync");

        foreach (var field in new[]
        {
            "checkpointId", "workspaceId", "subfolder", "phrase",
            "deckId", "collectionId", "collection", "title", "tool", "muxName", "launchMode",
        })
            StringAssert.Contains(dto, $"string? {field}");

        Assert.IsFalse(dto.Contains("commandLine", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(dto.Contains("executable", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(dto.Contains("workingDirectory", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CheckpointStart_FilesTheSpawnBeforeUsingTheExistingMuxLaunch()
    {
        var handler = HandlerSource();
        var spawn = handler.IndexOf("_archive.SpawnTemplateAsync(checkpoint)", StringComparison.Ordinal);
        var rename = handler.IndexOf("_archive.RenameSessionAsync(", spawn, StringComparison.Ordinal);
        var phrase = handler.IndexOf("_archive.SetSpecialPhrasesAsync(", rename, StringComparison.Ordinal);
        var filing = handler.IndexOf("_archive.AddToCollectionByIdAsync(", phrase, StringComparison.Ordinal);
        var mux = handler.IndexOf("StartMuxHeadlessFromIntentAsync(", filing, StringComparison.Ordinal);

        Assert.IsTrue(spawn >= 0, "checkpoint spawn is missing");
        Assert.IsTrue(spawn < rename && rename < phrase && phrase < filing && filing < mux);
    }

    [TestMethod]
    public void BlankStart_QueuesFilingThenUsesGovernedMuxWithoutMintingAnIntent()
    {
        var handler = HandlerSource();
        var queue = handler.IndexOf("_archive.QueuePendingNewChatAsync(", StringComparison.Ordinal);
        var command = handler.IndexOf("_archive.BuildMultiplexStartCommand(", queue, StringComparison.Ordinal);
        var mux = handler.IndexOf("StartMuxHeadlessCommandFromIntentAsync(", command, StringComparison.Ordinal);
        var cancel = handler.IndexOf("_archive.CancelPendingNewChatAsync(", queue, StringComparison.Ordinal);

        Assert.IsTrue(queue >= 0, "pending filing queue is missing");
        Assert.IsTrue(queue < command && command < mux);
        Assert.IsTrue(cancel > queue, "launch failures must cancel the pending filing intent");

        var helper = Slice(
            RemoteSource(),
            "private async Task<(bool ok, string detail)> StartMuxHeadlessCommandFromIntentAsync",
            "// /tomux handoff:");
        StringAssert.Contains(helper, "GovernedCreateLocalMuxdSessionAsync(");
        StringAssert.Contains(helper, "allowLocalIntentMint: false");
        Assert.IsFalse(helper.Contains("Process.Start", StringComparison.Ordinal));
    }

    private static string HandlerSource() => Slice(
        RemoteSource(),
        "private async Task<(bool ok, string detail)> StartChatHeadlessFromIntentAsync",
        "private static bool TryResolveStartSubfolder");

    private static string RemoteSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(),
        "native",
        "CodexLocalRetrieval.Native",
        "MainPage.Remote.cs"));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"missing marker: {startMarker}");
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
