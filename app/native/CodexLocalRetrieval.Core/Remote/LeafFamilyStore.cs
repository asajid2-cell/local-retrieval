using System.Text.Json;

namespace CodexLocalRetrieval.Core.Remote;

// The GATEWAY owns leaf families; this app only draws them.
//
// A gateway session can spawn child sessions - "leaves". That relationship is recorded in the gateway's own
// store, not in the transcript and not in this app's store:
//
//     <claude config home>/leaf-families/<rootSessionId>.json     the family and its members
//     <claude config home>/leaf-families/index/<sessionId>.json   the per-session pointer into a family
//
// Reading both is what lets one family be drawn as a single lineage instead of as N unrelated chats. It is
// READ-ONLY on purpose: the format belongs to the gateway, which is free to change it, and nothing here may
// write into a store that a running gateway is also writing.
//
// Two ids that are easy to confuse, and the gateway is explicit that they are different things:
//
//   * rootSessionId      the family's STABLE id. It is the CREATOR's session id, it names the file, and it
//                        never moves.
//   * patriarchSessionId the family's DESIGNATED HEAD. A title, which can be conferred and transferred, so it
//                        does move. It lives in the loose index pointer rather than in the family file,
//                        because the family file is a strict schema that an older reader would reject if it
//                        grew a field. Absent - or naming a session that is not a member - the family root IS
//                        the head, so there is no "no patriarch" case.
//
// The head does NOT live only in the file the family is named after: when a patriarch leaves, the gateway
// moves the designation down to the next open member and REPUBLISHES it to every member's pointer, so the
// current head is whatever the pointers say NOW and never what the family was first called. This reader
// therefore reads the pointers on every load rather than remembering a head it saw once - a designation
// that has moved since is the normal case, not an edit to be merged.
//
// So "collapse into the lineage" means: draw one row at the patriarch, and show the rest of the members
// inside it. rootSessionId is the handle the gateway's own tools take, not the head to draw.
public sealed class LeafFamilyStore
{
    private const string FamilyDirectoryName = "leaf-families";
    private const string IndexDirectoryName = "index";

    public sealed class Options
    {
        // The gateway resolves its config home from CLAUDE_CONFIG_DIR and falls back to ~/.claude, and a PTY
        // harness points that variable at a temp root. Read the same home the gateway reads, or a family that
        // exists is reported as missing.
        public string? ConfigHome { get; set; }

        public string EffectiveConfigHome =>
            !string.IsNullOrWhiteSpace(ConfigHome)
                ? ConfigHome!
                : Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
                    ? configured
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        // Injectable for tests, which build a config home rather than reading the user's.
        public string? DirectoryOverride { get; set; }

        public string EffectiveDirectory =>
            !string.IsNullOrWhiteSpace(DirectoryOverride)
                ? DirectoryOverride!
                : Path.Combine(EffectiveConfigHome, FamilyDirectoryName);

        public string EffectiveIndexDirectory => Path.Combine(EffectiveDirectory, IndexDirectoryName);
    }

    private readonly Options _options;
    private readonly Dictionary<string, LeafFamilyMember> _bySessionId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LeafFamily> _byMemberId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LeafFamily> _families = new();

    public LeafFamilyStore(Options? options = null)
    {
        _options = options ?? new Options();
        Load();
    }

    // Every family the gateway has on disk. Most are a single member: a session that never spawned a leaf
    // still gets a family file, and it collapses to itself.
    public IReadOnlyList<LeafFamily> Families => _families;

    // The family a chat belongs to, or null when this chat is not a gateway leaf at all - which is the
    // normal case, and must not be treated as an error.
    public LeafFamily? FamilyFor(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _byMemberId.TryGetValue(sessionId!, out var family) ? family : null;

    public LeafFamilyMember? MemberFor(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _bySessionId.TryGetValue(sessionId!, out var member) ? member : null;

    // Families that have more than one member - the only ones collapsing actually changes anything for.
    public int MultiMemberFamilyCount => _families.Count(f => f.Members.Count > 1);

    public int MemberCount => _bySessionId.Count;

    private void Load()
    {
        try
        {
            if (!Directory.Exists(_options.EffectiveDirectory)) return;
            foreach (var path in Directory.GetFiles(_options.EffectiveDirectory, "*.json"))
            {
                // Only family files live at the top level; the pointers are one directory down. Filter on the
                // file NAME anyway, because this directory belongs to another program and a stray file (a
                // .host.json, a backup) is not a family.
                if (!IsFamilyFileName(Path.GetFileNameWithoutExtension(path))) continue;
                var family = ReadFamily(path);
                if (family is null) continue;
                _families.Add(family);
                foreach (var member in family.Members)
                {
                    _bySessionId[member.SessionId] = member;
                    _byMemberId[member.SessionId] = family;
                }
            }
        }
        catch
        {
            // A store that cannot be read is the same as no leaf families: every chat stays independent,
            // which is exactly today's behaviour. Never let this break the chat list.
        }
    }

    private static bool IsFamilyFileName(string name) => Guid.TryParse(name, out _);

    private LeafFamily? ReadFamily(string path)
    {
        var json = TryParse(path);
        if (json is null) return null;

        var rootSessionId = Text(json.Value, "rootSessionId");
        if (string.IsNullOrWhiteSpace(rootSessionId) || json.Value.TryGetProperty("members", out var membersElement) is false
            || membersElement.ValueKind != JsonValueKind.Array)
            return null;

        var members = new List<LeafFamilyMember>();
        foreach (var element in membersElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var sessionId = Text(element, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId)) continue;
            members.Add(new LeafFamilyMember
            {
                SessionId = sessionId,
                ParentSessionId = Text(element, "parentSessionId"),
                Number = element.TryGetProperty("number", out var number) && number.TryGetInt32(out var n) ? n : 0,
                Name = Text(element, "name"),
                Status = Text(element, "status"),
                Cwd = Text(element, "cwd"),
                TranscriptPath = Text(element, "transcriptPath"),
            });
        }
        if (members.Count == 0) return null;

        // The family file names the root only by id; the loose pointer is where its NAME lives - and the
        // creator's pointer is the authority, because a family can be renamed after the file was written.
        var creatorPointer = ReadPointer(rootSessionId);

        // The creator is an ordinary member row in the current gateway, but it was not always one: a family
        // written before that change has no row for its own root, and the gateway synthesizes one when it
        // reads. So does this reader - otherwise 7 of the 123 families on this box would have no head at
        // all, and the very chat the family is named after would not resolve to its family.
        //
        // Synthesizing it here is a READ: the gateway's own `ensureCreatorMember` writes that row back when
        // a session opens the family. Nothing in this app may write into a store a running gateway is
        // writing, so the row exists only for as long as this process needs it.
        var rootPresent = members.Any(m => string.Equals(m.SessionId, rootSessionId, StringComparison.OrdinalIgnoreCase));
        if (!rootPresent)
            members.Add(new LeafFamilyMember
            {
                SessionId = rootSessionId,
                // Ancestry for the record, never an edge anything walks - the gateway writes its own id here.
                ParentSessionId = rootSessionId,
                // Last, because numbers are never recycled and a member joining late must not displace anyone.
                Number = json.Value.TryGetProperty("nextNumber", out var next) && next.TryGetInt32(out var nextNumber)
                    ? nextNumber
                    : members.Count + 1,
                Name = creatorPointer?.RootName ?? "",
                Status = "open",
                Cwd = creatorPointer?.RootCwd ?? "",
                TranscriptPath = creatorPointer?.RootTranscriptPath ?? "",
                IsCreator = true,
            });

        // The name the family is called by is the head's, and the head is the root until a designation says
        // otherwise - so the name is read from the root's row and, when the pointer has one, from the pointer.
        var rootName = members
            .FirstOrDefault(m => string.Equals(m.SessionId, rootSessionId, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
        if (creatorPointer?.RootName is { Length: > 0 } pointerName) rootName = pointerName;

        var patriarch = ResolvePatriarch(creatorPointer?.PatriarchSessionId, rootSessionId, members);
        // Mark the head on the member row itself, so a roster can say which of the family's chats the family
        // is drawn at without re-deriving it from the pointer on every draw.
        foreach (var member in members)
            if (string.Equals(member.SessionId, patriarch, StringComparison.OrdinalIgnoreCase))
                member.IsPatriarch = true;
        return new LeafFamily
        {
            RootSessionId = rootSessionId,
            RootName = rootName,
            PatriarchSessionId = patriarch,
            Delegated = !string.Equals(patriarch, rootSessionId, StringComparison.OrdinalIgnoreCase),
            Members = members,
        };
    }

    // The designation is only meaningful if it names a member: a stale pointer that names a session the
    // family no longer holds would otherwise draw a head that cannot be opened. The gateway resolves this the
    // same way - fall back to the creator - so the two surfaces never disagree about who heads a family.
    private static string ResolvePatriarch(string? designated, string rootSessionId, IReadOnlyList<LeafFamilyMember> members)
    {
        if (!string.IsNullOrWhiteSpace(designated)
            && members.Any(m => string.Equals(m.SessionId, designated, StringComparison.OrdinalIgnoreCase)))
            return designated!;
        return rootSessionId;
    }

    private LeafPointer? ReadPointer(string sessionId)
    {
        try
        {
            var path = Path.Combine(_options.EffectiveIndexDirectory, sessionId + ".json");
            if (!File.Exists(path)) return null;
            var json = TryParse(path);
            if (json is null) return null;
            // The index directory also holds records this reader has no business in - a host claim, a launch
            // ledger - and they are named apart (a dotted stem), so a plain `<id>.json` is a family pointer.
            // It still has to SAY so: the gateway accepts a pointer only when it carries a rootSessionId, and
            // a file that does not is not a pointer to fall back on.
            var rootSessionId = Text(json.Value, "rootSessionId");
            if (string.IsNullOrWhiteSpace(rootSessionId)) return null;
            return new LeafPointer
            {
                RootSessionId = rootSessionId,
                RootName = Text(json.Value, "rootName"),
                RootTranscriptPath = Text(json.Value, "rootTranscriptPath"),
                RootCwd = Text(json.Value, "rootCwd"),
                PatriarchSessionId = Text(json.Value, "patriarchSessionId"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? TryParse(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            // JsonDocument owns the buffer, so the element has to be cloned to outlive it.
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private sealed class LeafPointer
    {
        public string RootSessionId { get; init; } = "";
        public string RootName { get; init; } = "";
        public string RootTranscriptPath { get; init; } = "";
        public string RootCwd { get; init; } = "";
        public string PatriarchSessionId { get; init; } = "";
    }
}

// One session inside a family. `number` is the family's own display number (the order leaves were created),
// which is what the gateway's own roster shows - so it is carried through rather than recomputed here.
public sealed class LeafFamilyMember
{
    public string SessionId { get; init; } = "";
    public string ParentSessionId { get; init; } = "";
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string TranscriptPath { get; init; } = "";

    // True for the family's creator when the family file has no row for it and this reader synthesized one.
    // It is the family's head on exactly the same footing as any member - the flag only says where the row
    // came from, so nothing downstream has to guess why the creator has no number of its own history.
    public bool IsCreator { get; init; }

    // The family's designated head, marked when the family is read.
    public bool IsPatriarch { get; set; }

    // The name this APP lists the member under, resolved from the store when the family is drawn. The family
    // file's own `name` is whatever the tool had at fork time and is often a placeholder ("Conversation",
    // "1"), so a roster reads the store and falls back to the file when the chat is not in it.
    public string DisplayName { get; set; } = "";

    // One roster line: the family's own number, the name, and the two things a reader needs to tell the rows
    // apart - which one is the head, and whether the chat is still open.
    public string RosterLine
    {
        get
        {
            var name = !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
                : !string.IsNullOrWhiteSpace(Name) ? Name
                : "(unnamed chat)";
            var marks = IsPatriarch ? "  · patriarch" : "";
            if (IsClosed) marks += "  · " + Status;
            return (Number > 0 ? Number + ". " : "") + name + marks;
        }
    }

    // "promoted" is not closed: it is a status a member can be given, and it still names an open session.
    public bool IsClosed => string.Equals(Status, "closed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "archived", StringComparison.OrdinalIgnoreCase);
}

// One gateway family: a stable root, a movable head, and the members between them.
public sealed class LeafFamily
{
    public string RootSessionId { get; init; } = "";
    public string RootName { get; init; } = "";
    public string PatriarchSessionId { get; init; } = "";
    public bool Delegated { get; init; }
    public IReadOnlyList<LeafFamilyMember> Members { get; init; } = Array.Empty<LeafFamilyMember>();

    // The row this family collapses to. The root is always a member, so this cannot be null for a family that
    // was read successfully.
    public LeafFamilyMember? Patriarch =>
        Members.FirstOrDefault(m => string.Equals(m.SessionId, PatriarchSessionId, StringComparison.OrdinalIgnoreCase))
        ?? Members.FirstOrDefault(m => string.Equals(m.SessionId, RootSessionId, StringComparison.OrdinalIgnoreCase));

    // The other sessions of the family - what the lineage shows inside the head.
    public IEnumerable<LeafFamilyMember> Others =>
        Members.Where(m => !string.Equals(m.SessionId, PatriarchSessionId, StringComparison.OrdinalIgnoreCase));

    public bool IsSingleMember => Members.Count <= 1;
}
