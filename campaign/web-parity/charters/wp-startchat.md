# Charter — branch mind `wp-startchat`

Serves GOAL.md: the web must reach "the full corpus of information to be able to resume chats,
discover them, start them and all." Discovery and resume shipped. STARTING is the gap: the web's
`+` asks for a name and a tool; the desktop asks for name, deck, collection, special phrase,
start-from-checkpoint, tool, working folder, and new subfolder — and files the chat as it creates it.

Repo: Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval (master).
Read first: campaign\web-parity\PLAN.md (v3 block), campaign\web-parity\SECURITY-INTEGRATION.md,
then the code named below. You are a persistent tandem peer; fan out your own sealed junior lanes
(luna@max) once you have frozen your contracts, and re-run every lane's verifier yourself.

## The shape (apex decisions — binding)

1. **A chat started from the web runs as a MUX SESSION**, not a bare local terminal. The point is to
   start it and immediately drive it in the browser. `MainPage.Remote.cs` already has
   `StartMuxHeadlessFromIntentAsync` (~line 618) doing exactly this launch for an EXISTING chat;
   reuse it rather than inventing a second launch path. Everything it does — launch claims, the
   intent ledger, at-most-once — must keep applying.
2. **The write path is the app-command queue**, not a POST to the PC API. That queue already has
   intent fencing, lease tokens, replay policy and (since today) a long poll that delivers in
   milliseconds. A new chat must be `intent-fenced`: a retry must never create a second chat.
3. **The pickers are read-only discovery data**, served by the PC through the gate that already
   exists at `/multiplex/pc/api/discovery/` — decks, collections in a deck, checkpoints/templates,
   and workspaces. No new public surface, no second gate.
4. **Never put a command line, executable or absolute path in the queue.** The relay stores it and
   the browser can see it. Pass identity (checkpoint id, deck id, collection id, workspace id from
   the picker) and let the PC resolve paths locally — the same rule `startmux` follows.

## Where the pieces already are (do not reimplement)

- Core ops the desktop Start-chat dialog drives, all present:
  `_archive.Decks`, `.CollectionsInDeck`, `.CreateCollectionAsync`, `.Templates`,
  `.EffectiveSources` (workspaces), `.ActiveDeckId`, `.QueuePendingNewChatAsync`,
  `.BuildStartLaunch`, `.CancelPendingNewChatAsync`. Read
  `app\native\CodexLocalRetrieval.Native\MainPage.StartChat.cs` to see the ORDER it calls them in
  and the rules it enforces (tool/folder disabled when a checkpoint is chosen, etc.) — that file is
  the specification you are porting. Do not modify it.
- Checkpoint spawning: `ArchiveService.Branch.cs` `SpawnTemplateAsync` (a checkpoint start is a
  spawn, then filing, then the mux launch).
- The relay already carries `collection`, `collectionId`, `deck`, `deckId`, `deckName`, `label`,
  `title`, `tool`, `muxName`, `takeover` on a command (`enqueueAppCommand`, relay/server.js ~2003).
  You will need to add the few fields it lacks (workspace identity, phrase, checkpoint id) and a
  `COMMAND_REPLAY_POLICY` entry for the new type. Keep that diff surgical.

## Your write-scope (exclusive)

- `app/native/CodexLocalRetrieval.Core/Remote/DiscoveryApi.cs` (+ a new file if cleaner) — picker
  endpoints. Existing discovery behavior must not change; its 6 tests must stay green.
- `app/native/CodexLocalRetrieval.Native/MainPage.Remote.cs` — the new command handler ONLY.
- `relay/server.js` — the replay-policy entry + passthrough fields ONLY.
- `relay/public/chats.html`, `relay/public/chats.js`, and/or a new `start.*` — the dialog.
- Tests for all of the above.
- NOT yours: `Server/Program.cs` (apex wires routes), nginx, `MainPage.StartChat.cs`, `index.html`.

## Definition of done

A new chat can be started from a phone: name it, pick tool OR a checkpoint, pick workspace, file it
into a deck/collection, stamp a phrase — and it appears as a live mux tab you can type into. A retry
or double-tap creates exactly one chat. Every claim in your fold is something YOU re-ran.
Fold to `campaign\web-parity\folds\wp-startchat.md`; freeze contracts in
`campaign\web-parity\folds\wp-startchat-contract.md` FIRST and pull the apex then, so route wiring
happens early.
