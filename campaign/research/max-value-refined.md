# LANE max-value — refined leaf set (mux-value prong)

*Lane: max-value refinement, 2026-07-22. Verified against HEAD 7968259 (clean tree). Base plan:
`campaign/mux-value.md`. All anchors below re-verified in the CURRENT tree.*

## Validation

### Substrate assumptions — verified TRUE
- **`relay/durable-state.js` is wired into server.js.** `server.js:11` requires
  `{durableJsonLoad, durableJsonWrite, durableWrite, fsyncDirectory}`; `writeJsonState` at
  `server.js:24` with the `persistenceBlocked` latch at `server.js:20-22,37,2034`. Used for
  PROJECTS_FILE (899, 1079, 1124), COMMANDS_FILE (1300), UPLOADS_META (1560), PINS_FILE (1949),
  RENAME_INTENTS_FILE (1977). durable-state.js (206 lines) does atomic tmp+fsync+rename with a
  `.bak` sidecar and test fault injection via `MUX_TEST_PERSIST_FAULT_FILE` — restart tests and
  persistence-failure tests are directly supported.
- **`relay/public/intent-journal.js` (103 lines).** Exposes exactly one global:
  `postIntent(url, payload, prefix)` — **POST only** (no method parameter). Semantics: dedupe by
  `signature(url + canonicalized payload)` in localStorage→sessionStorage; same unresolved
  signature ⇒ same `intentId` reused; injects `intentId` into the JSON body; 3 attempts with
  250/750ms backoff; `retryableStatus = 408|425|429|>=500`; journal record cleared only on a
  non-retryable response; cap 256 unresolved records. V3b/V5/V7b build on this as-is.
- **Lease/ack `/api/app-commands` API is real and complete.** `POST /api/app-commands` (enqueue,
  owner-gated, `server.js:1368`) with type allow-list = `COMMAND_REPLAY_POLICY` map
  (`server.js:1201-1211`: kill=refused, transcript=read-only, fetchfile/startmux=intent-fenced,
  rename/setapptitle/addtocollection/cleartabhistory/settabcolor=idempotent). intentId dedupe at
  `server.js:1341-1348`: same intentId + same `commandFingerprint` ⇒ `{deduplicated:true}`; same
  intentId + different fingerprint ⇒ 409 `intentConflict`. `POST /api/app-commands/lease`
  (loopback-only, lease token + expiry + attempt count, `1429-1466`); `POST
  /api/app-commands/:id/ack` (loopback-only, lease-token-fenced, terminal outcomes immutable,
  `1471-1516`); `GET /api/app-commands/:id` (owner poll → `{id,status,detail}`, `1517-1521`).
  Everything commits through `commitCommands` → `writeJsonState`. startmux enqueue keeps the
  local-owner 409 guard (`1410-1412`). App side consumes it in `MainPage.Remote.cs`
  `PollCommandsAsync` (L359-481): lease `{owner, limit:1}` → replay-safe check → flat
  `if/else if` dispatch on `c.type` (L391-473) → ack `{leaseToken, ok, detail, onPc}`.
- **muxd durable input intents exist** — `execute_input_intent` at `muxd.py:2647` /
  `execute_durable_input_intent` at `muxd.py:2672-2723`: fingerprint-bound intent records,
  persisted before and after the PTY write, replay of a completed intent returns the cached
  result, an in-flight/uncertain intent is **never replayed** (returns "input outcome is
  uncertain; the PTY write was not replayed"). Ack frame is `{"t":"input-ok","s":<name>}`.
  BUT see false-assumption 1 below: this protocol is not reachable from the relay today.
- **Fake-host harness in `relay/tests/relay.test.js` confirmed, still inline.** Helpers
  L15-76 (`HOST_CAPS`, `freePort`, `waitFor`, `leaseCommands`, `ackLeased`, `waitForWsText`),
  `class RelayHarness` L78-213 (spawns the **real server.js** child on a free port, temp
  MUX_STATE_DIR, `MUX_TEST_MODE`, has `restart()` and `failPersistence()`), `class FakeHost`
  L215-294 (ws to `/host?token=test-token`, protocol-4 hello with `HOST_CAPS`, sendSessions/
  sendOutput/sendCreateResult/waitFor/assertNo). ~280 lines, no `harness.js` yet — **V0 is
  unchanged and still the right universal first leaf**; its verifier (existing suite green after
  extraction) still holds.
- **`tail` host command exists** (V6's substrate): relay side `server.js:305-313` (rid-correlated
  request, 2.5s timeout); muxd side `muxd.py:3855-3865` (`tail_text(nbytes=200000, lines)` →
  `{"t":"tailr","rid","text"}`).
- **Owner gating is a global middleware** (`server.js:95-105`): every route is owner-gated unless
  the caller `isTrustedLocal` (direct loopback with no X-Forwarded-For — the app over its own
  SSH). New endpoints inherit the gate for free. `express.static` on `relay/public`
  (`server.js:107-115`) serves any new page (e.g. `reader.html`) automatically, with no-cache
  HTML headers and no SPA catch-all to interfere.
- **App→relay push path for V7a exists as a pattern and is REUSABLE (V7a = build).**
  `MainPage.Remote.cs` `PushProjectsAsync` (L320-354): builds JSON via
  `ArchiveService.BuildProjectsProjectionJson` and POSTs it over owner-only SSH as
  `curl --data-binary @-` to loopback `/api/projects` (relay: loopback-only POST at
  `server.js:1069-1086`, owner GET at 1087, `appLive()` 45s freshness at 902). Transport
  primitive `RunSshAsync` (L827-859: 30s hard timeout, 4MiB stdout cap) is generic; a second
  payload type is a ~15-line sibling method + a 30s `DispatcherTimer` hook (`StartProjectSync`
  L263-283). Data side: `ChatProjection` (ArchiveService.cs L3884-3909) already emits id,
  aliases, title, tool, muxName, running, updatedAt, workspaceLabel, collection — pre-filtered
  to resumable via `CanBuildTrustedResumeLaunch` (L3566-3578) and capped `.Take(500)` (L3931).
  Only `cwd` is missing from the projection (exists on the model). Headless Core reader for V8a:
  `ArchiveService.ExtractReaderMessagesAsync` (L3239+) → `List<ArchiveMessage>` {Role, Text,
  Timestamp}, parses both Claude and Codex JSONL including live files. Test side: MSTest project
  `CodexLocalRetrieval.Native.Tests`; gate filter `TestCategory!=RealStore&TestCategory!=LiveCodex`
  confirmed in `default.runsettings`; five existing `BuildProjectsProjectionJson_*` tests in
  `ArchiveServiceTests.cs` (1061-1181) to mirror; new uncategorized test classes auto-run.

### Base-plan assumptions that are FALSE or shifted (with consequences)
1. **"V3a needs no muxd changes" is FALSE — the relay↔muxd host link has no durable input
   frame.** The durable `t:"input"` (+intentId → `input-ok`) handler lives only on muxd's
   **local** websocket server (`muxd.py:3615-3625`, sole call site of `execute_input_intent` at
   3624). The host link's input frame is `t:"i"` — a fire-and-forget PTY write with **no intentId
   and no ack** (`muxd.py:3819-3820`; relay sends it at `server.js:2314`). The host-link loop
   (`muxd.py:3770-3873`) handles only create/heal/i/resize/sb/rename/tail/kill.
   ⇒ V3a splits: **val-v3a-mux** (muxd: accept `t:"input"` with `intentId`+`rid` on the host
   link, route through the existing `execute_input_intent(..., scope="relay")`, reply the result
   frame with `rid`, advertise a new host cap) and **val-v3a-relay** (the `/send` endpoint with a
   rid-correlated confirm wait, mirroring the tail/create patterns at `server.js:305-313` /
   `223-258`). `execute_input_intent` already takes `scope`, so the muxd half is small; campaign
   call 3 (muxd deploys freely) makes it cheap.
2. **No muxd input size cap exists.** The `input` handler only rejects empty/invalid base64
   (`muxd.py:3619-3623`); the websockets library default (~1MB) is the only bound. The base
   plan's "oversized text → 4xx per muxd input caps" becomes **a relay-defined cap** (pinned:
   32KB UTF-8) enforced in val-v3a-relay.
3. **"DOM-in-vm pattern: client-layout.test.js loads index.html into a vm context" is FALSE.**
   `client-layout.test.js` (53 lines) does **string-structure analysis** of index.html source
   (marker-bounded `section()` slices + regex/ordering asserts) — it executes nothing. The real
   vm pattern lives in `relay.test.js:2196-2292` and only works on **loadable JS files** (it
   vm-loads `intent-journal.js` with stubbed fetch/localStorage/crypto). ⇒ Every UI leaf's
   verifier is re-specced: new UI logic ships as a small `relay/public/<leaf>.js` module (loaded
   via `<script src>`), behavior-tested in vm with mocked fetch/postIntent, plus string-structure
   asserts on the index.html wiring. Side benefit: index.html merge conflicts under
   branch-per-leaf shrink to one `<script>` tag + a few wiring lines per leaf.
4. **A composer dialog already exists** (base plan implies none). `#composedlg` + `#composetext`
   at `index.html:657-667`, wired at 2193-2199 to `term.paste(t)` (+ `send('i','\r')`), also the
   clipboard-fallback path (2365). ⇒ V3b is an **upgrade** — route the existing dialog through
   `postIntent` → `/send`, add history/busy/failed states — not a green-field panel. Lower risk.
5. **Anchor drift in index.html; the client never sees `needsAttention`.** Tab render is
   `renderTabs()` at `index.html:1756-1808` (plan said ~1652). The server pre-reduces attention
   into `s.state` (green/yellow/red/white) — zero client-side matches for
   `agentState`/`needsAttention`; the client sets `t.dataset.state` (1762). V2's chip keys off
   `s.state` (or a new pushed field). Server-side attention computation is `server.js:386-440`
   (matches plan). Keybar markup 574 / builder ~2472; mobile nav 575-592; intent-journal include
   696; `loadSessions` 1723-1755 with a 4s poll at 2605.
6. **"Autoheal watchdog ~L810" does not exist; heal give-up has NO relay signal today.** Heal is
   muxd-owned (`server.js:1133` comment); the relay only toggles it (713-728) and `/api/health`'s
   `gaveUp` is a **hardcoded 0 stub** (`server.js:1170`). ⇒ V1 triggers on session-attention
   episodes only; host-link-down and heal-give-up alerting are ops-health edges and move to the
   adversarial prong (seam 1 below). Host-link-down IS observable today (`hostUp()`, `_pcHealth`
   probe 1136-1165, plus the degraded flags in `/api/health` 1165-1180) — the signal belongs to
   ops-health, not to this prong.
7. **`tools/run_gates.ps1` references must change — and the gate does NOT auto-pick-up new relay
   tests.** The runner is `app/tools/run_gates.ps1`. It runs dotnet build / native-app-build /
   `dotnet test … --filter 'TestCategory!=RealStore&TestCategory!=LiveCodex' --blame-hang-timeout
   3m` (new dotnet test classes auto-run), the projection-contract probe, **relay-tests =
   `node --test tests\relay.test.js` ONLY**, and muxd `python -m unittest discover -s tests -p
   'test_*.py'` (new muxd test files auto-run) — and it runs relay/muxd suites in the EXTERNAL
   deployed checkouts `C:\Users\Ahmed\multiplex-app-patch` and `C:\Users\Ahmed\muxd`, not the
   repo tree. ⇒ New `relay/tests/<leaf>.test.js` files are **invisible to the gate** until the
   relay-tests line is widened. Leaf val-vg owns that edit; leaf verifiers are the in-repo
   bounded commands below.
8. **The ack carries no payload end-to-end.** The app sends `{leaseToken, ok, detail, onPc}` and
   its `transcript` arm even puts a 7000-char transcript tail in `detail`
   (`MainPage.Remote.cs:413-417`) — but the relay **discards app-supplied detail** and derives it
   from a fixed label table (`server.js:1491-1497` via `commandOutcomeDetail` 1238-1256). ⇒ V8a
   cannot return pages "via the ack-payload" (base plan's option 1), and the upload store is the
   wrong direction (VPS→PC pull only, `RemoteUploadTransfer.cs`). Pinned design: the app
   **pushes** transcript pages over its SSH loopback channel (mirroring `/api/projects`) to a new
   loopback-only `POST /api/transcripts/:sessionId`; the relay persists bounded pages and serves
   them owner-gated; the `transcriptfetch` ack only signals completion. Note
   `commandFingerprint` hashes a **fixed field set** (`server.js:1224-1236`) — `transcriptfetch`
   keyed by `sessionId` needs no fingerprint extension.
9. **Wave-2 gate is lifted** (c61dbab, 413c13b landed; tree clean at 7968259). **V7a decision:
   BUILD**, not kind:"plan" — the push plumbing is not gnarly (evidence in the TRUE section:
   generic `RunSshAsync`, ~15-line sibling push method, projection reshape of already-running
   code, drop-in MSTest classes). Two binding constraints get pinned into the leaf goals instead
   of a planning leaf: (a) app/PLAN.md items 5-6 — commands carry opaque ids only, everything
   resolved locally on the PC; (b) app/REMOTE.md redaction — `transcriptfetch` must honor
   `CLR_REMOTE_REDACT_READS` when shipping message bodies off-PC. V8b's separate-page preference
   is confirmed and pinned: `relay/public/reader.html` + `reader.js`, served automatically by
   `express.static` — no route needed, stays off the index.html seam.

## Leaves

```json
[
  {
    "id": "val-v0-harness",
    "title": "Extract fake-host test harness into relay/tests/harness.js",
    "kind": "build",
    "goal": "Factor the inline test infrastructure of relay/tests/relay.test.js (helpers L15-76: HOST_CAPS, freePort, waitFor, leaseCommands, ackLeased, waitForWsText; class RelayHarness L78-213; class FakeHost L215-294) into relay/tests/harness.js exporting all of them, and make relay.test.js require('./harness'). Zero behavior change; no server.js edits. Every other relay-test leaf imports this module.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/tests/relay.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/relay.test.js && node -e \"const s=require('fs').readFileSync('tests/relay.test.js','utf8'); if(!/require\\(['\\\"]\\.\\/harness['\\\"]\\)/.test(s)||/class FakeHost/.test(s)) process.exit(1)\"",
    "size": "S",
    "notes": "Universal first leaf. FakeHost/RelayHarness line anchors verified 2026-07-22."
  },
  {
    "id": "val-notify-transport",
    "title": "Shared ntfy push transport module relay/notify.js (cross-prong seam module)",
    "kind": "build",
    "goal": "Create relay/notify.js: createNotifier({url, fetchImpl}) reading MUX_NTFY_URL by default, exposing async push({title, body, tags, priority, click}) that POSTs to the ntfy topic URL with a 5s timeout, returns {ok, status|error} and NEVER throws; a no-op (ok:false, disabled:true) when no URL is configured. Pure transport: no episode/dedupe logic — event-source owners implement their own episode semantics. This module is shared with the adversarial prong's ops-health alerts; keep the API exactly as stated.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/server.js", "relay/durable-state.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/notify-transport.test.js",
    "size": "S",
    "notes": "Test needs no fake host: boot a local http.createServer capture target and point the notifier at it. server.js read is only for env/config conventions (MUX_* + TEST_MODE)."
  },
  {
    "id": "val-v1-attention-push",
    "title": "Attention-episode push notifications",
    "kind": "build",
    "goal": "In relay/server.js, add a session-attention episode detector: on each host sessions update (handler at server.js:1822-1830 feeding hostSessions), classify sessions via the existing attention computation (server.js:386-440); when a hosted session enters needsAttention (attention/stopped) and HOLDS it for a settle window (env MUX_NOTIFY_SETTLE_MS, default 20000; shrinkable in MUX_TEST_MODE), push once via notify.js (session name, agentLabel, deep link); re-notify only after the state clears and re-enters (one push per episode). Persist episode state via writeJsonState so a relay restart mid-episode does not re-fire. Do NOT alert on host-link-down or heal-give-up — those are the adversarial prong's ops-health edges.",
    "tier": "default",
    "deps": ["val-v0-harness", "val-notify-transport"],
    "reads": ["relay/server.js", "relay/notify.js", "relay/tests/harness.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/notify.test.js",
    "size": "M",
    "notes": "Test: harness + capture server; fake host drives working→attention→working→attention: exactly 1 POST per episode after settle, 0 while working, flap under settle window → 0, harness.restart() mid-episode → no duplicate. Human-checklist item: one live ntfy drill on the phone (campaign end). MUX_NTFY_URL provisioning is an ops item, not a leaf blocker."
  },
  {
    "id": "val-v2-chip-mute",
    "title": "Needs-attention chip + per-session notification mute",
    "kind": "build",
    "goal": "Server: POST /api/sessions/:name/notify {on:boolean} (owner-gated by the global middleware), persisted via writeJsonState; muted sessions are skipped by the val-v1 episode notifier; mute state surfaced in /api/sessions rows. UI: render a distinct 'needs attention' chip (not just the colored dot) in renderTabs (index.html:1756-1808) keyed off s.state==='yellow'||'red', plus a mute toggle in the session UI; ship chip/toggle logic in a small relay/public/attention-ui.js module wired from index.html.",
    "tier": "default",
    "deps": ["val-v1-attention-push"],
    "reads": ["relay/server.js", "relay/public/index.html", "relay/tests/harness.js", "relay/tests/client-layout.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/notify-toggle.test.js",
    "size": "S",
    "notes": "Test: mute → drive episode → 0 POSTs; unmute → next episode → 1; toggle survives harness.restart(). UI side: vm-load attention-ui.js (pattern relay.test.js:2196-2292) + string-structure asserts on index.html wiring (client-layout section() pattern). Client has NO needsAttention field today — key off s.state or add a mute/attention field to /api/sessions rows."
  },
  {
    "id": "val-v3a-mux",
    "title": "muxd: durable input-intent frame on the relay host link",
    "kind": "build",
    "goal": "In muxd.py's relay host-link loop (muxd.py:3770-3873), handle t:'input' frames {s, d: base64, intentId, rid}: route through the existing execute_input_intent(first, session, data, scope='relay') (defined muxd.py:2647; durable machinery 2672-2723 already handles fingerprint binding, replay of completed intents, and never-replay of uncertain ones), and reply the result frame ({t:'input-ok', s, rid} or {t:'err', m, rid}). Advertise a new capability 'input' in the host hello caps so the relay can feature-gate. No caps/limits muxd-side beyond existing base64 validation (the relay owns the size cap).",
    "tier": "default",
    "deps": [],
    "reads": ["muxd/muxd.py", "muxd/tests/"],
    "verifier": "cd muxd && timeout 120 python -m unittest discover -s tests -p \"test_host_input_intent*.py\"",
    "size": "S",
    "notes": "muxd.py regions: host-link loop 3770-3873 (mirror the tail handler 3855-3865 for rid plumbing), local input handler 3615-3625 as the template. Test asserts: same intentId replay → cached result, different payload on same intentId → err, no-intentId frame rejected or treated per contract. muxd deploys freely (campaign call 3). New muxd test files auto-run in the gate's unittest discover."
  },
  {
    "id": "val-v3a-relay",
    "title": "Relay durable send endpoint POST /api/sessions/:name/send",
    "kind": "build",
    "goal": "New owner-gated route in relay/server.js: POST /api/sessions/:name/send {text, intentId} → validate (session hosted+alive else 409/404; intentId matches the app-command id charset; text non-empty UTF-8 ≤ 32KB else 413), forward {t:'input', s: name, d: base64(text), intentId, rid} over the host link, wait rid-correlated for input-ok/err bounded at 5s (mirror requestHostTail server.js:305-313 and requestHostCreate 223-258), map: input-ok → 200, err → 502 with detail, host down or cap missing → 503 (retryable per intent-journal retryableStatus). Relay is a faithful conduit: it forwards intentId unchanged; exactly-once is muxd's contract (val-v3a-mux). Extend the harness FakeHost to record input frames and script input-ok/err/cached replies.",
    "tier": "default",
    "deps": ["val-v0-harness", "val-v3a-mux"],
    "reads": ["relay/server.js", "relay/tests/harness.js", "relay/public/intent-journal.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/send-endpoint.test.js",
    "size": "M",
    "notes": "Test: double-POST same intentId → both forwarded with the SAME intentId and fake host's cached-result reply maps to 200 (dedupe proven muxd-side in val-v3a-mux); two intentIds → two deliveries; host offline → 503; non-hosted session → 4xx; >32KB → 413. Dep on val-v3a-mux is the CONTRACT (frame shape + cap name); implementation can proceed against the pinned contract if scheduled concurrently."
  },
  {
    "id": "val-v3b-composer",
    "title": "Upgrade the existing composer dialog to the durable send path",
    "kind": "build",
    "goal": "Upgrade the EXISTING composer (#composedlg/#composetext, index.html:657-667, handlers 2193-2199 currently term.paste-based): add 'send to session' via postIntent(base+'/api/sessions/'+name+'/send', {text}, 'send') for exactly-once over flaky mobile networks, busy/failed/sent states, and a local send-history with recall (localStorage, cap ~50). Keep the raw term.paste path as an explicit fallback button. Put the new logic in relay/public/composer.js (vm-testable); index.html only adds the <script> tag and wiring.",
    "tier": "default",
    "deps": ["val-v3a-relay"],
    "reads": ["relay/public/index.html", "relay/public/intent-journal.js", "relay/public/composer.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/composer-ui.test.js",
    "size": "M",
    "notes": "Test: vm-load composer.js (pattern relay.test.js:2196-2292) with mocked postIntent — exactly one call with session+text; retryable failure surfaces failed state and leaves the (mocked) journal record; history recall restores last text. Plus string asserts: index.html includes composer.js, dialog keeps #composedlg id. Keybar anchor if needed: builder ~index.html:2472, markup 574. Human-checklist: phone IME feel."
  },
  {
    "id": "val-v4-send-when-idle",
    "title": "Send-when-idle: durable queued steering",
    "kind": "build",
    "goal": "Composer option 'deliver when idle': relay stores queued messages per session (FIFO, bounded depth ~8, cancellable via DELETE, persisted with writeJsonState) with a server-generated per-entry intentId persisted alongside; the val-v1 transition detector fires delivery on working→(attention|idle) through the val-v3a-relay host input path exactly once (restart-safe because the persisted intentId makes redelivery a muxd-side dedupe no-op). UI: 'queued (n)' chip with cancel, in composer.js/attention-ui.js. If val-v1 notifications are enabled for the session, the episode push mentions 'queued message delivered'.",
    "tier": "default",
    "deps": ["val-v1-attention-push", "val-v3a-relay"],
    "reads": ["relay/server.js", "relay/tests/harness.js", "relay/public/composer.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/deferred-send.test.js",
    "size": "M",
    "notes": "Test: queue while working → no delivery; flip to attention → exactly one delivery, queue drains; two queued → FIFO; harness.restart() while queued → survives, delivers once (fake host asserts single intentId); cancel → never delivers."
  },
  {
    "id": "val-v5-quick-replies",
    "title": "Quick-reply chips above the composer",
    "kind": "build",
    "goal": "Editable one-tap canned prompts ('continue', 'status?', 'run the tests', ...) rendered above the composer; stored per browser in localStorage with sane defaults; tap = one send through the composer's postIntent /send path. Implement in composer.js (or quick-replies.js if cleaner); index.html gets wiring only.",
    "tier": "default",
    "deps": ["val-v3b-composer"],
    "reads": ["relay/public/composer.js", "relay/public/index.html"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/quick-replies.test.js",
    "size": "S",
    "notes": "vm test: default chips render; tap → exactly one postIntent with chip text; edited set persists to (stubbed) localStorage and re-renders."
  },
  {
    "id": "val-v6-fleet",
    "title": "Fleet glance screen + GET /api/fleet",
    "kind": "build",
    "goal": "Server: GET /api/fleet (owner-gated) returning every session's /api/sessions row PLUS a bounded tail snippet (last ~2 lines, ≤2KB each) fetched via the existing tail host command (server.js:305-313, 2.5s timeout; short in-memory cache ~5s; host offline → rows degrade to state-only and the endpoint still 200s fast). UI: a fleet view in the mobile-nav slot (index.html:575-592) — one row per session: state chip, agentLabel, last-output age, snippet, autoheal badge; tap attaches. Logic in relay/public/fleet.js; index.html wiring only.",
    "tier": "default",
    "deps": ["val-v0-harness"],
    "reads": ["relay/server.js", "relay/public/index.html", "relay/tests/harness.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/fleet-view.test.js",
    "size": "M",
    "notes": "Test: all sessions in one request with snippet ≤ cap against fake host; host offline → 200 with state-only rows, bounded latency; vm/string asserts for fleet.js rendering N rows with state class + snippet."
  },
  {
    "id": "val-v7a-app",
    "title": "App: recent-chats archive index projection + push",
    "kind": "build",
    "goal": "Core: ArchiveService.BuildArchiveIndexJson — a reshape of the existing ChatProjection pipeline (ArchiveService.cs:3884-3909, resumable-gated by CanBuildTrustedResumeLaunch 3566-3578, recency-ordered, cap 500) emitting {schemaVersion:1, host, chats:[{id, title, tool, cwd, workspaceLabel, updatedAt, muxName, resumable:true}]}; add cwd (on the model, not yet projected). Native: a sibling of PushProjectsAsync (MainPage.Remote.cs:320-354) POSTing it over RunSshAsync curl-stdin to loopback POST /api/archive-index on the same 30s sync timer (StartProjectSync L263-283). Opaque ids only (app/PLAN.md items 5-6): no commands, no local paths beyond cwd display strings.",
    "tier": "default",
    "deps": [],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.Remote.cs", "app/native/CodexLocalRetrieval.Core/Services/ArchiveService.cs", "app/native/CodexLocalRetrieval.Native.Tests/ArchiveServiceTests.cs", "app/PLAN.md"],
    "verifier": "dotnet test app/native/CodexLocalRetrieval.Native.Tests/CodexLocalRetrieval.Native.Tests.csproj -c Release --filter \"FullyQualifiedName~ArchiveIndexTests\" --blame-hang --blame-hang-timeout 3m",
    "size": "M",
    "notes": "DECISION: build, not plan — push transport is generic (RunSshAsync L827-859), data pipeline already runs every 30s; mirror the five BuildProjectsProjectionJson_* tests (ArchiveServiceTests.cs:1061-1181). New MSTest class with no RealStore/LiveCodex category auto-runs in the gate. ArchiveService.cs is a 5694-line god-file: touch only the projection region ~3855-3990."
  },
  {
    "id": "val-v7a-relay",
    "title": "Relay: accept + serve the archive index",
    "kind": "build",
    "goal": "Mirror the /api/projects pattern (server.js:1069-1087): loopback-only POST /api/archive-index validating shape ({schemaVersion, chats[]} with id/title/tool/updatedAt; cap 500 rows, reject oversized bodies), persisted via writeJsonState; owner-gated GET /api/archive-index serving it with an updatedAt/appLive-style freshness field (appLive() pattern at server.js:902). Contract pinned in val-v7a-app's goal — keep field names identical.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/server.js", "relay/tests/harness.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/archive-index.test.js",
    "size": "S",
    "notes": "Test: loopback push accepted (harness requests are loopback — use the X-Forwarded-For header to simulate non-loopback rejection, cf. isTrustedLocal server.js:88-94), served owner-gated, survives harness.restart(), oversized/invalid shape rejected."
  },
  {
    "id": "val-v7b-resume-picker",
    "title": "Phone resume-from-archive picker",
    "kind": "build",
    "goal": "Web UI picker (relay/public/picker.js + index.html wiring): search box client-filtering GET /api/archive-index rows; tap a chat → postIntent(base+'/api/app-commands', {type:'startmux', sessionId, tool, muxName}, 'resume') using the EXISTING intent-fenced startmux command (allow-listed server.js:1201-1211; enqueue validation 1407-1413 incl. the local-owner 409) → poll GET /api/app-commands/:id → on done, refresh sessions and select the new hosted tab; surface ack detail on failure (e.g. 'local copy is already running') and a queued/offline state when the app is not live (appLive-style freshness from val-v7a-relay).",
    "tier": "default",
    "deps": ["val-v7a-app", "val-v7a-relay"],
    "reads": ["relay/public/index.html", "relay/public/intent-journal.js", "relay/tests/harness.js", "relay/server.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/resume-picker.test.js",
    "size": "M",
    "notes": "Test: seed index via loopback POST; double-tap → second enqueue returns deduplicated:true (intent-journal reuses the intentId; server.js:1341-1348); lease+ack via harness leaseCommands/ackLeased; fake host adds the session → tab list contains it; app-offline → queued/failed state shown, not silence. vm-test picker.js; string-assert index.html wiring. This is the W5 'one product' moment."
  },
  {
    "id": "val-v8a-app",
    "title": "App: transcriptfetch command → paged clean-transcript push",
    "kind": "build",
    "goal": "Add a 'transcriptfetch' arm to the app command dispatch (MainPage.Remote.cs PollCommandsAsync L391-473): given the leased command's opaque sessionId, project the chat via the headless Core reader ArchiveService.ExtractReaderMessagesAsync (ArchiveService.cs:3239+, tolerant of half-written live tails) into paged JSON ({schemaVersion:1, sessionId, page, pages, messages:[{role, text, ts}]}, ≤200KB/page, ≤32 pages newest-first), honoring app/REMOTE.md redaction (redact message bodies when CLR_REMOTE_REDACT_READS=1); push each page over RunSshAsync curl-stdin to loopback POST /api/transcripts/:sessionId on the relay, then ack ok. Opaque ids only (PLAN items 5-6).",
    "tier": "default",
    "deps": [],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.Remote.cs", "app/native/CodexLocalRetrieval.Core/Services/ArchiveService.cs", "app/REMOTE.md", "app/PLAN.md"],
    "verifier": "dotnet test app/native/CodexLocalRetrieval.Native.Tests/CodexLocalRetrieval.Native.Tests.csproj -c Release --filter \"FullyQualifiedName~TranscriptFetchTests\" --blame-hang --blame-hang-timeout 3m",
    "size": "M",
    "notes": "Do NOT return pages via ack detail — the relay discards app-supplied detail (server.js:1491-1497). Test fixtures: Claude + Codex JSONL incl. a truncated in-flight last line; assert page structure, size caps, redaction-on. Existing reader tests to mirror: TranscriptShareTests, RolloutAndLiveMappingTests."
  },
  {
    "id": "val-v8a-relay",
    "title": "Relay: transcriptfetch command type + transcript page store",
    "kind": "build",
    "goal": "(1) Register 'transcriptfetch' in COMMAND_REPLAY_POLICY (server.js:1201-1211) as 'read-only', require sessionId at enqueue (mirror the transcript rule at 1388), add outcome labels (commandOutcomeDetail 1238-1256); no commandFingerprint change needed (sessionId already hashed). (2) Loopback-only POST /api/transcripts/:sessionId accepting the paged JSON from val-v8a-app (validate shape, bound total bytes ~8MB/session, keep latest fetch only), persisted under STATE_DIR via writeJsonState; owner-gated GET /api/transcripts/:sessionId?page=N.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/server.js", "relay/tests/harness.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/transcript-fetch.test.js",
    "size": "M",
    "notes": "Test: enqueue transcriptfetch → lease/ack via harness helpers (simulating the loopback app); page push loopback-accepted, X-Forwarded-For rejected; pages served owner-gated; survive harness.restart(); oversized page rejected. Contract (page JSON shape) pinned identically in val-v8a-app."
  },
  {
    "id": "val-v8b-reader",
    "title": "Reader view: relay/public/reader.html",
    "kind": "build",
    "goal": "A SEPARATE page relay/public/reader.html + reader.js (pinned: not index.html — express.static serves it automatically at /reader.html, server.js:107-115): renders GET /api/transcripts/:sessionId pages as a clean reader (role-classed bubbles, tool-call collapse, newest-last), 'refresh' triggers a new transcriptfetch enqueue via postIntent + repolls, PC-bridge-offline renders a helpful fallback. Entry points: a 'Read as transcript' link on session tabs and picker rows (few-line index.html wiring only).",
    "tier": "default",
    "deps": ["val-v8a-relay"],
    "reads": ["relay/public/reader.html", "relay/public/intent-journal.js", "relay/public/index.html", "relay/tests/harness.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/reader-ui.test.js",
    "size": "M",
    "notes": "vm-load reader.js with fixture pages → N message nodes with role classes; refresh re-fetches (mocked); offline fixture → fallback text. Human-checklist: reader readability on the phone."
  },
  {
    "id": "val-vg-gate-registration",
    "title": "Widen the relay-tests gate to the whole tests directory",
    "kind": "build",
    "goal": "Edit app/tools/run_gates.ps1 relay-tests gate (currently `node --test tests\\relay.test.js`) to run the full suite bounded: `node --test --test-force-exit --test-timeout=20000 tests\\` so every campaign-added relay/tests/*.test.js is gate-visible; keep the '# pass ' positive marker. Do not change the dotnet/muxd gates (they auto-discover). Verify the widened invocation is green against the repo relay tree.",
    "tier": "default",
    "deps": [],
    "reads": ["app/tools/run_gates.ps1", "relay/tests/"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/ && grep -q -- \"--test-force-exit\" ../app/tools/run_gates.ps1",
    "size": "S",
    "notes": "Schedule LAST (after wave-1 relay leaves merge) — no semantic dep since it globs the dir, but running it early proves nothing. HAZARD: the known hanging test (stripMouseReports area) makes --test-force-exit mandatory. The gate executes in the EXTERNAL checkout C:\\Users\\Ahmed\\multiplex-app-patch — the acceptance runner must sync/deploy before the real gate run (seam 6). If the adversarial prong claims gate-file ownership, convert this leaf to a registration request to them."
  }
]
```

## Ordering

Branch-per-leaf; only semantic deps below (file-sharing serialization dropped per campaign call 1
— in particular the base plan's index.html chain V3b→V2→V6-UI→V5→V4-UI is GONE; each UI leaf ships
its own `public/<leaf>.js` module + minimal index.html wiring, so merges are near-trivial).

```
start immediately (no deps):
  val-v0-harness          val-notify-transport    val-v3a-mux
  val-v7a-app             val-v7a-relay           val-v8a-app     val-v8a-relay

after val-v0-harness:            val-v6-fleet
after v0 + notify:               val-v1-attention-push
after v0 + v3a-mux:              val-v3a-relay
after v1:                        val-v2-chip-mute
after v3a-relay:                 val-v3b-composer
after v1 + v3a-relay:            val-v4-send-when-idle
after v3b:                       val-v5-quick-replies
after v7a-app + v7a-relay:       val-v7b-resume-picker
after v8a-relay:                 val-v8b-reader
last (all relay leaves merged):  val-vg-gate-registration
```

Dep sanity check (each is a true contract/data dep):
- v1←notify (calls the module), v1←v0 (test harness). v2←v1 (mute gates v1's pushes).
- v3a-relay←v3a-mux (wire contract: frame shape + host cap; the exactly-once guarantee lives
  muxd-side). v3b←v3a-relay (posts to /send). v5←v3b (sends through the composer path).
- v4←v1 (transition detector) + v3a-relay (delivery path).
- v6←v0 only (uses existing tail command; no v1 dependency).
- v7b←v7a-app+v7a-relay (index content + endpoint). v8b←v8a-relay (page store API).
  v7a-app∥v7a-relay and v8a-app∥v8a-relay pair up via contracts pinned identically in both
  goals — no cross-dep needed, they can land in either order.
- v0 remains the universal first relay leaf: verified the harness is still inline
  (relay.test.js L15-294) and the extraction verifier holds against the current suite.

First cut recommendation (win-condition items 1-4): v0, notify, v3a-mux in parallel → v1, v3a-relay,
v6 → v3b, v4, v2 → v5. Wave 2 (items 5-6) can start day one — v7a-app/v7a-relay/v8a-app/v8a-relay
have no deps.

## Seams

1. **Notification transport (RESOLVED as proposed):** ONE shared module `relay/notify.js`
   (leaf `val-notify-transport`, owned by THIS prong) + two event-source owners:
   - mux-value owns **session-attention episodes** (val-v1: attention/stopped with settle
     window + episode dedupe, per-session mute via val-v2);
   - adversarial owns **ops-health edges** (its L10b: host link down >5min, persistenceBlocked,
     heal-give-up) as a *dependent* of val-notify-transport — it imports the module, never
     builds a second transport. Note for L10b: `hostUp()`/`_pcHealth` (server.js:1136-1165) and
     `persistenceBlocked` are real signals today, but heal-give-up is NOT (`gaveUp` is a
     hardcoded 0 at server.js:1170) — that alert needs a muxd→relay signal first.
2. **`relay/server.js`** — mux-value adds: notify hook, /send, deferred-send queue, /api/fleet,
   /api/archive-index, /api/transcripts, notify-toggle, transcriptfetch registration. All
   additive routes/modules; branch-per-leaf merges are textual, conflicts mechanical. The
   adversarial prong should attack these after they land, not co-edit.
3. **`relay/public/index.html`** — no longer single-owner-serialized. Convention (binding for
   all prongs): new UI logic goes in per-feature `relay/public/*.js` modules; index.html edits
   are limited to `<script>` tags + minimal wiring. V8b stays off the seam entirely
   (reader.html).
4. **`/api/app-commands` semantics** — owned by the integration lane. val-v7b is a pure client;
   val-v8a-relay makes a minimal additive registration (COMMAND_REPLAY_POLICY entry + enqueue
   validation line + outcome labels). If the lease API shifts, only v7b/v8a-relay rebase.
5. **muxd host-link protocol** — val-v3a-mux adds one frame type + one advertised cap. If the
   native-parity prong touches muxd's input path, coordinate there; the relay-side tests stay
   green regardless (fake host). The harness FakeHost's HOST_CAPS must gain the new cap
   (done in val-v3a-relay's harness extension).
6. **Gates** — `app/tools/run_gates.ps1` runs relay/muxd suites in EXTERNAL checkouts
   (`C:\Users\Ahmed\multiplex-app-patch`, `C:\Users\Ahmed\muxd`). Two consequences the
   orchestrator must own: (a) one owner for gate-file edits — proposed here as val-vg (this
   prong), reassign to adversarial if they claim it with their hang fix; (b) the acceptance
   runner must sync repo → external checkouts before the real gate run, else green leaves are
   invisible to CURRENT.md.

## Open questions

1. **Heal-give-up signal**: `/api/health.gaveUp` is a stub (server.js:1170) and muxd does not
   report give-up over the host link. Which prong builds the muxd→relay signal (sess_list field
   or a dedicated frame)? mux-value excluded it from val-v1 on this evidence; adversarial's
   L10b needs it.
2. **Gate-file ownership**: val-vg vs the adversarial prong (who presumably fixes the hanging
   stripMouseReports test that makes `--test-force-exit` necessary). One owner, orchestrator's
   call. Also: should the widened gate run `tests\` before that hang is fixed, given
   `--test-force-exit` masks (rather than fixes) it?
3. **Ack-detail discard**: the app's `transcript` arm returns a 7000-char tail in ack `detail`
   (MainPage.Remote.cs:413-417) but the relay overwrites detail from a label table
   (server.js:1491-1497) — the payload is dead end-to-end. Was that hardening intentional?
   V8a routes around it either way; flagging because the app-side code implies a contract the
   relay no longer honors.
4. **ntfy provisioning**: MUX_NTFY_URL (topic + VPS env) is an ops step outside any leaf;
   needs a human/deploy action before the live drill at campaign end.
5. **Host-link-down alert ownership confirmed?** Base plan had it in V1; this refinement moves
   it to adversarial ops-health (it is an infra edge, not a session episode). If adversarial
   rejects it, val-v1 can absorb it back with a one-line scope note.
