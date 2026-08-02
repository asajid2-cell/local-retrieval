# Web Start-Chat Contract

Frozen: 2026-08-02
Owner: `wp-startchat`

This is the integration boundary between the web dialog, relay app-command queue, PC discovery
routes, and native command handler. The relay stores identity and display metadata only. It never
stores a command line, executable, transcript path, snapshot path, or working-directory path.

## App Command

`POST /multiplex/api/app-commands` enqueues:

```json
{
  "type": "startchat",
  "intentId": "startchat-opaque-stable-id",
  "muxName": "display-safe-mux-name",
  "title": "Optional app name",
  "tool": "codex",
  "checkpointId": "",
  "workspaceId": "ws-opaque-id",
  "subfolder": "optional-child-folder",
  "deckId": "main",
  "collectionId": "",
  "collection": "Optional new collection",
  "phrase": "optional searchable phrase"
}
```

Rules:

- Replay policy is `intent-fenced`. The same `intentId` plus the same normalized payload returns the
  existing command. The same `intentId` with a different payload is `409`.
- `muxName` is required and uses the relay's existing strict mux-name validation.
- Exactly one launch source is selected:
  - Blank chat: `checkpointId` is empty; `tool` is `claude` or `codex`; `workspaceId` is required.
  - Checkpoint chat: `checkpointId` is required; `tool`, `workspaceId`, and `subfolder` are empty.
    Tool and workspace are resolved from the checkpoint on the PC.
- `title`, `collection`, `phrase`, and `subfolder` are trimmed and capped at 200 characters.
  `subfolder` is one file-name component, not a path: no separators, rooted value, `.`/`..`, or
  invalid local file-name characters.
- `deckId`, `collectionId`, `checkpointId`, and `workspaceId` are opaque picker identities.
  Unknown or stale identities fail closed; they never fall back to a path, active deck, or another
  checkpoint/workspace.
- `collectionId` selects an existing collection and must belong to `deckId`. When it is empty, a
  non-empty `collection` asks the PC to create or reuse that name in `deckId`. Both empty means no
  collection. `collectionId` and `collection` are mutually exclusive.
- `phrase` is applied as one special phrase. Empty means no phrase.
- The native handler performs the desktop order under the intent fence:
  resolve identities; prepare/create the collection; spawn a checkpoint or queue the pending blank
  chat; apply title/phrase/filing metadata; build the local launch; start a PC-local mux session;
  cancel the pending blank-chat filing intent if launch fails.
- A successful ack means the mux session exists and is ready for browser attachment. The ack detail
  contains display-safe status text only.

Relay persistence, fingerprinting, reload validation, lease delivery, and result labels include all
fields above. `startchat` is rejected by the existing forbidden-remote-field guard if any executable
or path-shaped key is supplied.

## Picker Routes

These routes are GET-only discovery data served by the PC under the existing discovery gate:
publicly `/multiplex/pc/api/discovery/...`, server-side `/api/discovery/...`.

### GET `/api/discovery/start/decks`

```json
{
  "activeDeckId": "main",
  "rows": [
    { "id": "main", "label": "Main" }
  ]
}
```

Rows are sorted case-insensitively by label, then id. `activeDeckId` is present only when it names a
returned row; otherwise it is `main`.

### GET `/api/discovery/start/collections?deckId=main`

```json
{
  "deckId": "main",
  "rows": [
    { "id": "project-id", "label": "Web Parity" }
  ]
}
```

`deckId` is required and must exactly identify a current deck. Unknown values return an empty page
for that requested id, not collections from Main. Rows are limited to that deck and sorted
case-insensitively by label, then id.

### GET `/api/discovery/start/checkpoints`

```json
{
  "rows": [
    {
      "id": "checkpoint-id",
      "label": "Checkpoint display label",
      "sourceTitle": "Source chat",
      "tool": "codex",
      "workspaceLabel": "mux-local-retrieval",
      "createdAt": "2026-08-02T12:34:56Z",
      "messageCount": 42
    }
  ]
}
```

Only current `ArchiveService.Templates()` entries are returned. No source/snapshot/workspace path is
serialized. Ordering follows `Templates()` (newest first).

### GET `/api/discovery/start/workspaces`

```json
{
  "rows": [
    {
      "id": "ws-opaque-id",
      "label": "mux-local-retrieval",
      "tools": ["claude", "codex"]
    }
  ]
}
```

The PC builds a current registry from distinct, existing local working directories already known to
the archive, plus the user-profile default used by the desktop dialog. `id` is a deterministic
opaque SHA-256-derived identity over the PC-normalized path; the path is never returned. Duplicate
paths merge case-insensitively, labels are scrubbed display names, and `tools` lists tools observed
for that workspace. Rows sort case-insensitively by label, then id.

The same registry builder resolves `workspaceId` in the native handler. Resolution additionally
requires that the directory still exists at execution time. Picker refreshes may therefore remove a
stale workspace, and a queued command targeting one fails closed.

## Web Behavior

- The Start chat dialog is reachable from the existing `+` control and is phone-width first.
- Deck changes reload collections. The collection control permits an existing selection or a new
  name, matching the desktop editable combo.
- Selecting a checkpoint disables and clears tool, workspace, and subfolder. Clearing it restores
  blank-chat controls.
- Submit creates one stable intent id per user action, disables duplicate submission while pending,
  enqueues `startchat`, and polls `/api/app-commands/{id}` through the existing command-result flow.
- On success the page attaches/navigates to the new mux tab. Retry after an uncertain response
  reuses the same intent id and payload.
