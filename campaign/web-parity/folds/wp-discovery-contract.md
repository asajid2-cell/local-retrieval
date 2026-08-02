# Web Discovery Endpoint Contract

Frozen: 2026-08-02
Owner: `wp-discovery`

This contract is the integration boundary for D-PC, D-Web, and the apex-owned route registration.
Discovery is served directly by the PC archive through nginx `/remote/`; the relay does not proxy or
store discovery data.

## Route Registration

The apex adds one registration call in `CodexLocalRetrieval.Server/Program.cs` after `api` and
`archiveRuntime` are constructed:

```csharp
app.MapDiscovery(archiveRuntime, new DiscoveryApi(archive));
```

The endpoint adapter may live in the server project, but request parsing and response construction
remain HTTP-agnostic in `Core/Remote/DiscoveryApi.cs`.

## GET `/remote/api/discovery/chats`

The server route is `/api/discovery/chats`; nginx supplies the public `/remote/` prefix.

Query parameters:

| Name | Default | Accepted values |
|---|---:|---|
| `q` | empty | Metadata text query. Exact `[phrase]` syntax keeps ArchiveService semantics. |
| `include` | empty | Comma-separated tag names to include. |
| `exclude` | empty | Comma-separated tag names to exclude. |
| `match` | `any` | `any` or `all` for included tags. |
| `agent` | empty | empty, `codex`, or `claude`. |
| `date` | empty | empty, `today`, `week`, or `month`; filters by creation date. |
| `minUserMessages` | `0` | `0`, `2`, `3`, `5`, `10`, or `25`. |
| `showHidden` | `false` | Boolean. False hides low-signal one-off chats. |
| `project` | empty | Exact collection/project ID. |
| `sort` | `recent` | `recent`, `created-newest`, `created-oldest`, `last-user`, or `first-user`. |
| `offset` | `0` | Non-negative integer. |
| `limit` | `50` | Integer clamped to `1..100`. |

Unknown enum values fall back to their defaults. Tag parsing trims values, removes blanks, and
deduplicates case-insensitively. `offset` is clamped to zero. The API reuses `ChatFilter` and
`ArchiveService.FilterChats`; it does not promise exhaustive transcript-file search.

Response:

```json
{
  "query": "",
  "offset": 0,
  "limit": 50,
  "total": 123,
  "hasMore": true,
  "rows": [
    {
      "id": "opaque-session-id",
      "title": "Display title",
      "tool": "codex",
      "workspaceLabel": "mux-local-retrieval",
      "updatedAt": "2026-08-02T12:34:56Z",
      "tags": ["active"],
      "phrases": ["web-parity"],
      "userMsgCount": 17,
      "pinned": true,
      "resumable": true,
      "muxName": "display-title-opaque"
    }
  ]
}
```

Rows contain display metadata only. They never contain a command, executable, arguments, transcript
path, source path, or working-directory path. `resumable` is computed with the existing trusted
resume gate. `muxName` is additive to the charter's required row fields and exists only because the
existing relay `startmux` app-command requires a display-safe session name.

Pagination is deterministic for a fixed archive snapshot. Every supported sort receives a final
case-insensitive `id` tie-break. Search relevance order is preserved, with `id` used only to settle
otherwise equal ordering where the underlying sort exposes equal keys.

## GET `/remote/api/discovery/facets`

Accepts the same filtering parameters as `/chats`, excluding `offset` and `limit`. Facets describe
the complete result set after all supplied filters are applied.

Response:

```json
{
  "total": 123,
  "hidden": 41,
  "tags": [
    { "value": "active", "count": 18 }
  ],
  "phrases": [
    { "value": "web-parity", "count": 7 }
  ],
  "projects": [
    { "id": "project-id", "label": "Web Parity", "count": 12 }
  ]
}
```

Tag and phrase counts are case-insensitive and sorted by descending count, then label. Project
counts are based on membership among the filtered chats and sorted the same way. `hidden` is the
number of low-signal chats in the archive before the default hidden filter; it supports the UI label.

## Web Behavior

- `relay/public/chats.html` is phone-width first and uses vanilla HTML/CSS/JavaScript.
- Loading, PC-offline/unreachable, empty, and error states are explicit text states; no indefinite
  spinner is permitted.
- Phrase chips submit the exact query form `[phrase]`.
- Resume posts the existing `{type:"startmux", sessionId, tool, muxName}` payload through
  `/api/app-commands`, then polls `/api/app-commands/{id}` using the existing intent-journal pattern.
- `relay/public/picker.js` reads the first discovery page instead of `/api/archive-index`; its resume
  command behavior remains unchanged.
