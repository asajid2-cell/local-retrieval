# PLAN — web-parity campaign

STATUS: v3 FINAL — deep review CONVERGED (sol-xhigh + independent Claude counterplan,
ledger: tandems/web-parity-counterplan/TANDEM.md). Supersedes all draft waves below the
v3 block. Draft v1 wave text kept only as history.

## v3 DECISIONS (converged, evidence-cited — binding)
1. **Discovery = PC API through nginx `/remote/`, DIRECT.** No relay proxy, no relay/server.js
   edits for discovery, no index enrichment. New `/remote/api/discovery` on CodexArchiveRemote
   reusing ChatFilter (ArchiveModels.cs:282) + FilterChats (ArchiveService.cs:2880), server-side
   filtering + stable pagination + facets. picker.js migrates to it;
   PushArchiveIndexAsync is REMOVED (it is already broken vs canonical: POST /api/archive-index
   requires MUX_BRIDGE_TOKEN bearer the app never sends — do not build credential plumbing for a
   stale 500-row cache).
2. **Search runs on the PC** — metadata search now; NO exhaustive-transcript-search promise yet
   (disk scans cap at newest 2000/3000 sessions: ArchiveService.cs:851,936). Tri-state
   include/exclude is a TAGS feature; phrases are exact `[phrase]` searches + chips.
3. **Relay deploys FIRST; muxd swap HARD-BLOCKED.** Running muxd = protocol 4, 16 caps →
   canonical-compatible ('lease'/'resync' additive). Staged Jul-30 muxd REFUSES unsigned input
   (empty principal registry, no 'iw' handler) while canonical sends typing as unsigned t:'i' —
   a swap breaks web typing. DEFUSED 2026-08-02: ~/muxd rolled back to 3a9e9ae lineage
   (16-cap, no principal machinery); staged copies kept as *.staged-jul30.bak. Completing the
   staged muxd's input.durable principal/iw contract is a SEPARATE future campaign item.
4. **Write-partition (no server.js sharing):** R lane owns relay/server.js + relay tests +
   deploy script. D-PC lane owns new DiscoveryApi + DTOs + tests + archive-index-push removal.
   D-Web lane owns relay/public/chats.html + chats.js + picker.js migration. Apex integrates:
   one endpoint-registration line in Server/Program.cs, nav links, deploys, E2E.
5. **Wave order:** R (fixture test → MUX_DEPLOY_CONFIRM=1 deploy w/ backup → E2E round-trip) ∥
   D-PC + D-Web build; then I: serialized deploy (relay → PC server + nginx /remote/ restore
   [config exists: Server/deploy/nginx-remote.conf; public /remote/ currently 404s on purpose]
   → static assets), E2E desktop+phone incl. auth expiry, tunnel loss, pagination, resume.

## Wave-0 findings (evidence-backed, supersede draft-v1 assumptions)
1. live-only-inventory.md: **18/18 DROP** — nothing in the live fork needs porting. The
   "claude/codex WS handlers" were a cmdline parser (apex grep artifact); PUBLIC_RETURN already
   exists in canonical (line 202); __test trio depends on the relay-owned healer canonical
   deliberately removed (muxd owns healing now). Wave R therefore has NO R1 port lane.
2. Host protocol: deployed muxd (Jul 24) announces PROTOCOL=4 + all REQUIRED_HOST_CAPS —
   canonical relay accepts it. **Relay deploy does NOT wait for the muxd swap** (decision (c)
   resolved). 'lease'/'resync' are host→relay optional; absence degrades gracefully.
3. VPS state (/var/lib/multiplex, svc-multiplex): app-commands.json, projects.json (448KB),
   pins.json, uploads/, rename-intents.json, autoheal.json (live-only leftover, harmless).
   File names match canonical's STATE_DIR contract; canonical's validating loaders may strip
   un-validated live rows — app re-push (30s) self-heals the projection. Deploy runs
   ExecStart=/usr/bin/node /opt/multiplex-app/server.js, EnvironmentFile=/etc/multiplex-app.env.
   ⚠ canonical needs `npm ci` on the VPS if package.json changed (live package.json is Jul 3).
Apex: session c40d9a54 lineage (this campaign's apex writes this file; branch minds write folds
under campaign/web-parity/folds/).

## Waves

### Wave 0 — facts (RUNNING)
- live-only-inventory (junior, luna@max): precise port-or-drop spec for every live-fork-only
  behavior → `live-only-inventory.md`. Route/WS/env diff already established by apex.
- Deep co-plan critique (sol@xhigh): decisions (a)–(d) below.

### Wave R — relay reconciliation (branch mind: `wp-relay`)
Charter line: GOAL.md "Reconcile the fork onto canonical WITHOUT losing any live-only behavior."
- R1: port live-only bits per inventory (WS t==='claude'/'codex', PUBLIC_RETURN, __test trio
  fate) + relay tests for each ported behavior. Write-scope: `relay/server.js`,
  `relay/tests/live-port-*.test.js`.
- R2: host-compat proof: canonical relay × OLD muxd (Jul 24) — verify from code/tests that a
  host that never sends 'resync'/'lease'/'object' still bridges terminals (decides whether VPS
  deploy must wait for the muxd swap). Write-scope: `relay/tests/old-host-compat.test.js` only.
- R3: staged deploy: relay suite green → MUX_DEPLOY_CONFIRM=1 deploy (script backs up on VPS) →
  smoke: /api/health, WS bridge alive, picker loads, app-command round-trip (kill/open from web
  → PC executes → ack). Rollback = restore the VPS backup + systemctl restart. APEX-GATED
  (irreversible junction → deep review or owner waiver before deploy).

### Wave D — discovery parity (branch mind: `wp-discovery`)
Charter line: GOAL.md "see chats, filter them, see phrases, search, paginate — and resume."
DECISION (a) pending deep review — draft position: HYBRID.
- Index (pushed, enriched) stays the LIST/FILTER backbone: enrich rows with tags[], phrases[],
  userMsgCount, hidden, projects[], pinned; raise/rethink ArchiveIndexMaxChats (500 today);
  schema bump both sides. Write-scope: `ArchiveService.ArchiveIndex.cs`, relay archive-index
  handler section, `relay/tests/archive-index-*.test.js`.
- Full-text search + transcript preview PROXY to the PC server through the standing tunnel
  (VPS 127.0.0.1:8765) — no second index on the VPS. Write-scope: new relay proxy route file +
  server-side search endpoint if RemoteApi lacks one.
- New web page `relay/public/chats.html` + `chats.js`: virtualized paginated list, filter panel
  modeled on the desktop panel (tags/phrases include-exclude, sort, date, agent, your-messages
  thresholds 2+/3+/5+/10+/25+, hidden toggle, project filter), phrase chips, search box (index
  substring instant; full-text via proxy), resume via existing app-command queue. Phone-width
  first. Write-scope: `relay/public/chats.*` + a nav link line in projects.html.
- Desktop-app push additions are app-side lanes (Core + Remote push) — no overlap with relay
  lanes' files except the relay archive-index handler, which belongs to wp-discovery alone.

### Wave I — integrate + E2E + deploy
Merged to master under apex hands; relay redeploy if D touched server.js; E2E on phone width;
demonstrate the win condition list in GOAL.md.

## Write-partition (gate-enforced)
- wp-relay: `relay/server.js`, `relay/tests/live-port-*`, `relay/tests/old-host-compat*`.
- wp-discovery: `relay/public/chats.*`, `relay/public/projects.html` (nav link only),
  archive-index sections: `app/native/.../ArchiveService.ArchiveIndex.cs`,
  `app/native/.../MainPage.Remote.cs` (index push only), `relay/tests/archive-index-*`.
- CONFLICT: relay/server.js is wp-relay's exclusively during Wave R. wp-discovery's
  archive-index handler edits QUEUE behind Wave R1 integration (sequenced, not parallel, for
  that one file).
- Apex only: campaign docs, merges, deploys.

## Open decisions for deep review
(a) index-vs-proxy split for discovery data (draft: hybrid above)
(b) where full-text search executes (draft: PC server via tunnel proxy)
(c) VPS deploy sequencing vs muxd swap (draft: R2 decides; if old-host-incompatible, muxd swap
    first — requires the live orch tab to close, owner-assisted)
(d) write-partition above
(e) index cap: 500 → what? (7310 chats; row ~200B enriched → ~1.5MB full; draft: cap 4000
    visible-resumable, matching MaxIndexedFiles)

## Risks (draft)
1. Old-muxd × canonical-relay WS incompat bricks live terminal bridge on deploy → R2 proves
   before R3; deploy gate.
2. Live-only behavior missed by inventory → the inventory lane is exhaustive on a 1477-line
   file + deep reviewer double-reads drifted sections.
3. Index enrichment bloats pushes (30s cadence over ssh) → measure payload; gzip via curl
   --data-binary already; consider push-on-change.
4. hl-auth gating breaks the new chats page (owner-only index) → same isOwner gate as picker;
   test with cookie fixture.
5. VPS deploy regresses something only production traffic exercises → script's timestamped
   backup + documented rollback command in R3.
