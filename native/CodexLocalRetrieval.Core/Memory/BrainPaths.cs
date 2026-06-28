using System.IO;

namespace CodexLocalRetrieval.Core.Memory;

// The on-disk layout of one collection's brain. The vault IS the git repo and the Obsidian vault
// (canonical); index/ and source-cache/ are SIBLINGS outside the repo so the derived layers can never
// be mistaken for the source of truth.
//
//   brains/{collectionId}/
//     vault/            <- git repo + Obsidian vault (canonical .md)
//       .git/ .gitignore .brain.json
//       00 Atlas.md … overview files
//       cards/canonical/*.md  cards/working/*.md
//       working/  topics/  patches/*.json
//     index/brain.db          <- DERIVED SQLite, not git-tracked
//     source-cache/blocks.jsonl <- optional DERIVED cache (redacted), not git-tracked
public sealed class BrainPaths
{
    public string Root { get; }
    public BrainPaths(string brainsDir, string collectionId)
    {
        Root = Path.Combine(brainsDir, Sanitize(collectionId));
    }

    public string Vault => Path.Combine(Root, "vault");
    public string CardsCanonical => Path.Combine(Vault, "cards", "canonical");
    public string CardsWorking => Path.Combine(Vault, "cards", "working");
    public string WorkingDir => Path.Combine(Vault, "working");
    public string TopicsDir => Path.Combine(Vault, "topics");
    public string PatchesDir => Path.Combine(Vault, "patches");
    public string GitIgnore => Path.Combine(Vault, ".gitignore");
    public string Manifest => Path.Combine(Vault, ".brain.json");

    public string IndexDir => Path.Combine(Root, "index");
    public string Db => Path.Combine(IndexDir, "brain.db");
    public string SourceCacheDir => Path.Combine(Root, "source-cache");
    public string BlocksCache => Path.Combine(SourceCacheDir, "blocks.jsonl");

    public string CardsDirFor(string lane) => lane == Lanes.Canonical ? CardsCanonical : CardsWorking;

    private static string Sanitize(string id)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) id = id.Replace(ch, '-');
        return string.IsNullOrWhiteSpace(id) ? "default" : id;
    }
}
