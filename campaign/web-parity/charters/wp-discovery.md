# Charter — branch mind `wp-discovery`

Serves GOAL.md line: "see chats, filter them, see phrases, search, paginate — and resume", the
lightweight way: served straight from the PC archive, no second index.

You are a persistent branch mind (tandem peer). Repo: Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval.
Read first: campaign\web-parity\PLAN.md (v3 block — decisions 1, 2, 4 bind you), then the cited
code: Core/Models/ArchiveModels.cs ChatFilter (~line 282), Core/Services/ArchiveService.cs
FilterChats (~2880), Core/Remote/RemoteApi.cs (current /api/chats: q+limit only, clamp 50),
Native/MainPage.Tags.cs (~line 20, tri-state tag filter = the parity target),
relay/public/picker.js + reader.html/js (existing web patterns), Server/Program.cs (route style).

## Your write-scope (exclusive)
- D-PC: NEW file(s) Core/Remote/DiscoveryApi.cs (+ DTOs file if you split), tests
  (Native.Tests/DiscoveryApiTests.cs), REMOVAL of the archive-index push
  (Native/MainPage.Remote.cs: PushArchiveIndexAsync + its call sites ONLY — touch nothing else
  in that file) and Core/Services/ArchiveService.ArchiveIndex.cs (delete or gut).
- D-Web: NEW relay/public/chats.html + chats.js; MIGRATE relay/public/picker.js to the
  discovery endpoint.
- NOT yours: Server/Program.cs (apex adds the one MapDiscovery line), relay/server.js (never),
  nginx (apex at deploy).

## The contract you build to
GET /remote/api/discovery/chats — server-side: text query, tag include/exclude lists, agent,
date range, min-user-messages (2/3/5/10/25 presets), hidden toggle (default hides one-offs),
project filter, sort (recent default), STABLE pagination (cursor or offset+total), each row:
id, title, tool, workspace label, updatedAt, tags, phrases, userMsgCount, pinned, resumable.
GET /remote/api/discovery/facets — tag/phrase/project counts for the filter panel.
Phrases render as chips; exact `[phrase]` search works through the text query (ArchiveService
already special-cases bracket syntax ~line 821). Explicit PC-offline/loading states in the web
UI (the endpoint IS the PC; when it's unreachable the page must say so, not spin).
Resume from a row goes through the EXISTING multiplex app-command queue URL pattern picker.js
uses today. Phone-width first; no framework additions — match the existing vanilla public/ style.

## Doctrine
- D-PC and D-Web can be parallel junior swarms once YOU freeze the endpoint contract in your
  fold file first (campaign\web-parity\folds\wp-discovery-contract.md) — sealed briefs, exact
  file paths, VERIFY commands (dotnet test filter for D-PC; for D-Web a node-based DOM smoke or
  documented manual check).
- Progress = integrated, verified: you re-run every lane's verifier; fold to
  campaign\web-parity\folds\wp-discovery.md (your file alone).
- Pull the apex at: contract frozen (so apex wires Program.cs early), suite green + UI built,
  or any need to touch a file outside your scope.
