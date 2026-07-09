using System.Collections.ObjectModel;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ProjectionContractProbe <output-json>");
    return 2;
}

var service = new ArchiveService(useBundledStore: true);
service.Store.Sessions.Clear();
service.Store.Collections.Clear();
service.Store.Decks.Clear();

const string id = "contract-session";
service.Store.Sessions[id] = new ArchiveSession
{
    Id = id,
    Aliases = new ObservableCollection<string> { "contract-alias" },
    Tool = "codex",
    Title = "Projection contract",
    Workspace = @"C:\Users\Ahmed\private-contract-workspace",
    WorkspaceName = "private-contract-workspace",
    SourcePath = @"C:\Users\Ahmed\.codex\sessions\private-contract-rollout.jsonl",
    UpdatedAt = "2026-07-09T00:00:00.0000000Z"
};
service.Store.Collections["contract-collection"] = new ArchiveCollection
{
    Id = "contract-collection",
    Name = "Contract",
    SessionIds = new List<string> { id }
};

var json = service.BuildProjectsProjectionJson(
    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    Array.Empty<ArchiveService.RunningSessionInfo>(),
    runningVerified: true,
    runningVerificationDetail: "");

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, json);
Console.WriteLine(output);
return 0;
