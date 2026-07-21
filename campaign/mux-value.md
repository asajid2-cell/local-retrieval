# PRONG: mux-value — the feature/value pass on multiplex + local retrieval

*Planner: Fable, 2026-07-21. Read-only analysis of `Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval`
(`relay/`, `app/`, `muxd/`). Builds on `relay/full-review.md`, `relay/state-and-next.md`, `app/PLAN.md`,
`app/REMOTE.md`, `muxd/PLAN.md`. Plans against INTENDED post-integration behavior: the lease/ack
`/api/app-commands` API, `relay/durable-state.js`, and `relay/public/intent-journal.js` exist and are
the delivery substrate — every leaf EXTENDS them, never reinvents them.*

## The user and the workflows (why these features and not others)

Ahmed drives a fleet of long-running Claude/Codex sessions on his PC from a phone and a desktop
browser. Concrete workflow gaps, each traceable to the product docs or the code:

- **W1 — "Is anything waiting on me?" is a polling job.** The relay already computes per-session
  `agentState` ∈ {working, attention, stopped, …} and `needsAttention` (server.js ~L388–440), but the
  only consumer is a colored dot you must be looking at. `state-and-next.md` N3 says it outright:
  "Alert, don't just color a dot." Long autonomous turns end, agents sit at confirm prompts, autoheal
  gives up — all silently.
- **W2 — Typing real prompts on the phone is the worst part of the product.** Input goes only through
  the xterm attach websocket (no HTTP send path exists — verified by grep); mobile IME over a raw
  terminal is chronically bad (muxd/PLAN.md lists "mobile IME … as separate frontend work"). muxd
  already has fingerprinted, replay-safe, durably-terminalized **input intents** (muxd.py ~L2651–2720)
  and the browser has `intent-journal.js` — the exactly-once composer is 90% substrate, 0% product.
- **W3 — You can't steer a busy agent.** Mid-turn thoughts ("after this, run the tests") have nowhere
  to go; you either interrupt or set a phone alarm. At-least-once delivery + the agentState transition
  stream make "deliver when the agent next goes idle" buildable for the first time.
- **W4 — Triage means tapping through N tabs.** With ~9 live sessions, "what is every agent doing"
  requires visiting each tab. All the data (state, label, last-output age, tail) already exists
  per-session server-side.
- **W5 — The two halves of the product don't meet on the phone.** The archive side can search and
  resume chats (app/REMOTE.md) but its "resume" opens a terminal **on the PC**; the mux side can host
  sessions but can't search the archive. The `startmux` opaque-intent command (resolved against the
  local archive on the PC — app/PLAN.md item 5/6) exists precisely to bridge this, and has no
  phone-facing UI.
- **W6 — Reading what an agent did, from the phone, means scrolling terminal scrollback.** The
  `transcript` app-command opens the transcript in a window **on the PC** (server.js ~L1249) — useless
  remotely. The app's Core engine already renders clean transcripts (including half-written live
  files — app/README.md "Why it's hard").

Cut for having no nameable workflow: voice input, LLM fleet-summarizer, session share links,
desktop split-view, scrollback full-text search. (See Deferred.)

## Win condition (measurable)

Driving the fleet from the phone becomes **push instead of poll, composed instead of typed, unified
instead of split**. Concretely, all of:

1. A hosted session entering `needsAttention` (or autoheal give-up / host-link-down) produces a push
   notification on Ahmed's phone within 60s, exactly once per episode — proven by an automated relay
   test and one live drill.
2. A prompt composed in a real textarea on the phone reaches the session's composer **exactly once**
   even across a network blip mid-POST — proven by an idempotency test against the fake-host harness.
3. A message queued against a busy session is delivered automatically on its next working→attention
   transition, and survives a relay restart while queued — proven by a restart test.
4. One screen shows every session's state + label + last-output snippet in a single request.
5. From the phone: search the archive → tap → the chat is running as a **hosted mux tab** (not a PC
   terminal), and a live session's clean transcript is readable in a reader view.
6. Every leaf's verifier is wired into the existing gate (`tools/run_gates.ps1` relay-tests /
   dotnet-tests) and the whole suite is green.

First cut = items 1–4 (relay+web only). Item 5–6 = wave 2 (crosses into the C# app).

## Verifier infrastructure (shared by all leaves)

`relay/tests/relay.test.js` already boots the **real server.js on a free port with a scripted fake
muxd host** (node:test + ws). Every relay leaf below adds a `relay/tests/<leaf>.test.js` reusing that
harness (factor the fake host into `relay/tests/harness.js` — leaf V0). All tests are bounded by the
harness's own `waitFor(…, timeoutMs)` pattern; invocation form:

```
cd mux-local-retrieval/relay && node --test tests/<leaf>.test.js     # must exit < 120s
```

App-side leaves verify via `dotnet test` with a `--filter` (bounded, no UI), matching the existing
dotnet-tests gate. UI-behavior leaves use the existing DOM-in-vm pattern
(`relay/tests/client-layout.test.js` loads index.html into a vm context).

---

## Leaves

### V0 — Extract the fake-host test harness  *(enabler, tiny)*
- **goal**: Factor the fake muxd host + server-boot helpers out of `relay/tests/relay.test.js` into
  `relay/tests/harness.js` so new feature tests don't copy 200 lines each.
- **area/files**: `relay/tests/relay.test.js`, new `relay/tests/harness.js`. No server.js changes.
- **verifier**: `cd relay && node --test tests/relay.test.js` — the existing suite still passes
  entirely, now importing the harness (grep gate: `require('./harness')` present, old inline fake-host
  functions gone).
- **deps**: none. Run FIRST; everything else imports it.

### V1 — Attention push notifications (ntfy)  ★ highest value
- **goal**: On a hosted session's transition into `needsAttention` (debounced: state must hold ≥ a
  configurable settle window, default 20s), and on autoheal give-up and host-link-down > 5 min, POST
  a push (session name, agentLabel, deep link) to `MUX_NTFY_URL`; re-notify only after the state
  clears and re-enters (one push per episode).
- **area/files**: `relay/server.js` (a small `notify.js` module + hooks in the attention computation
  ~L388–440 and the autoheal watchdog ~L810); episode state persisted via `durable-state.js` so a
  relay restart doesn't re-fire every degraded session.
- **verifier**: `cd relay && node --test tests/notify.test.js` — harness boots server with
  `MUX_NTFY_URL` pointed at a local http capture server; fake host drives
  working→attention→working→attention; asserts: exactly 1 POST after settle for episode 1, 0 during
  working, 1 for episode 2; flapping under the settle window → 0 POSTs; restart server mid-episode →
  no duplicate POST.
- **deps**: V0. Parallel with V3a, V6.
- **effort**: S–M. **value**: converts the whole product from poll to push; the docs' own #1 ops ask.

### V2 — "Needs attention" chip + per-session notification toggle
- **goal**: A distinct visual state (chip/label, not just a red dot — full-review S7) for
  `needsAttention` sessions in the tab bar and fleet view, plus a per-session server-side mute
  (`POST /api/sessions/:name/notify {on}`) persisted durably; muted sessions never push.
- **area/files**: `relay/public/index.html` (tab render ~L1652), `relay/server.js` (toggle endpoint +
  check in notify.js), durable-state file.
- **verifier**: `cd relay && node --test tests/notify-toggle.test.js` — mute a session → drive
  attention episode → 0 POSTs; unmute → next episode → 1 POST; toggle survives server restart. DOM
  assert (vm pattern): a `needsAttention` session in the /api/sessions fixture renders the chip class.
- **deps**: V1.

### V3a — Durable send endpoint (`POST /api/sessions/:name/send`)
- **goal**: Owner-gated HTTP endpoint that delivers text to a hosted session's composer via muxd's
  existing durable **input-intent** protocol: body carries `{text, intentId}`; same intentId replayed
  → one delivery (relay forwards the intent id to muxd, which already terminalizes them); bounded
  wait for `input-ok`; host down → 503 (retryable per intent-journal's `retryableStatus`).
- **area/files**: `relay/server.js` (new route; reuse the host-send + confirm-wait pattern of
  `/api/sessions/:name/relaunch` ~L403–441). No muxd changes — it is the protocol's existing server.
- **verifier**: `cd relay && node --test tests/send-endpoint.test.js` — fake host records input
  intents and acks `input-ok`: double-POST with same intentId → exactly one host delivery; two
  different intentIds → two; host offline → 503; non-hosted session → 4xx; oversized text → 4xx
  (bounded per muxd input caps).
- **deps**: V0. Parallel with V1, V6.
- **effort**: S–M. **value**: the load-bearing primitive for W2/W3/V4/V5/V7.

### V3b — Phone/desktop composer UI
- **goal**: An expandable real textarea (from the keybar / mobile nav): compose with the native IME,
  send via `intent-journal.js postIntent` → V3a (exactly-once over flaky mobile network), local send
  history with recall, clear busy/failed states. Terminal keybar stays for raw keys.
- **area/files**: `relay/public/index.html` only (composer panel + wiring; uses the already-shipped
  `intent-journal.js`).
- **verifier**: `cd relay && node --test tests/composer-ui.test.js` (DOM-in-vm pattern): composer
  element exists in phone and desktop layouts; send invokes `postIntent` exactly once with the
  session name + text (mocked fetch); a failed retryable response leaves the journal record (retry
  possible); history recall restores the last sent text.
- **deps**: V3a. Serialize with other index.html leaves (V2, V5, V6 UI) — one owner at a time.

### V4 — Send-when-idle (queued steering)
- **goal**: Composer option "deliver when idle": relay stores the message durably per session (FIFO,
  bounded depth, cancellable), watches the V1 transition detector, and on working→(attention|idle)
  delivers via the V3a path exactly once, then (if V1 enabled) the notification says "queued message
  delivered". UI shows a "queued (n)" chip with cancel.
- **area/files**: `relay/server.js` (queue on `durable-state.js`; share V1's transition watcher),
  `relay/public/index.html` (option + chip).
- **verifier**: `cd relay && node --test tests/deferred-send.test.js` — queue while fake host reports
  working → no delivery; flip to attention → exactly one delivery, queue drains; queue two → FIFO
  order; **restart server.js while queued** → message survives and still delivers once; cancel →
  never delivers.
- **deps**: V1 + V3a. **effort**: M. **value**: high — first genuinely new capability (steer a busy
  agent), unlocked specifically by the at-least-once foundation.

### V5 — Quick-reply chips (prompt templates)
- **goal**: Editable one-tap canned prompts ("continue", "status?", "run the tests", …) above the
  composer; stored per browser (localStorage) with sane defaults; tap = V3a send with journal.
- **area/files**: `relay/public/index.html` only.
- **verifier**: `cd relay && node --test tests/quick-replies.test.js` (DOM-in-vm): default chips
  render; tap triggers exactly one postIntent with the chip text; edited set persists to
  localStorage and re-renders.
- **deps**: V3b. **effort**: S.

### V6 — Fleet glance screen
- **goal**: One screen (phone home): every session as a row — state chip, `agentLabel`, last-output
  age, last ~2 lines of tail, autoheal badge; tap → attach. Server: one bulk endpoint
  (`GET /api/fleet`) returning /api/sessions data + bounded tail snippets in a single round trip
  (reuse the existing `tail` host command, cap snippet bytes, cache briefly).
- **area/files**: `relay/server.js` (bulk endpoint composing existing session list + tail), 
  `relay/public/index.html` (screen; reuse mobile-nav slot).
- **verifier**: `cd relay && node --test tests/fleet-view.test.js` — endpoint returns all sessions
  with snippet ≤ cap in one request against the fake host (host offline → rows degrade to state-only,
  endpoint still 200s fast — bounded); DOM assert: fixture renders N rows with state class + snippet
  text.
- **deps**: V0 (server part); UI part serializes behind other index.html leaves. Parallel with V1/V3a.
- **effort**: S–M. **value**: medium-high (daily triage).

### V7a — Archive index push (app → relay)
- **goal**: The PC side (bridge/app, which already pushes `projects.json` while open) additionally
  pushes a compact **recent-chats index** (chat id, title, tool CX/CL, workspace cwd, last-active,
  resumable flag) — say top ~500 by recency; relay accepts it on the existing loopback-trusted push
  path, persists via `durable-state.js`, serves it owner-gated at `GET /api/archive-index`.
- **area/files**: app C# (`CodexLocalRetrieval` bridge/server component that owns the projects push),
  `relay/server.js` (accept + serve).
- **verifier**: two-sided: (1) `dotnet test --filter ArchiveIndex` — index built from a fixture store
  has the shape/caps and excludes non-resumable chats; (2) `cd relay && node --test
  tests/archive-index.test.js` — loopback push accepted, non-loopback rejected, index served
  owner-gated, survives restart.
- **deps**: none on other leaves, but **land after the integration lane freezes the app tree** (seam
  below). If the app's push plumbing turns out gnarly, this leaf is the one flagged
  **kind: plan** — decompose into (index projection in Core) + (push transport in bridge).
- **effort**: M.

### V7b — Resume-from-archive picker (phone)
- **goal**: In the mux web UI: a search box over `/api/archive-index` (client-side filter is enough at
  ~500 rows) → tap a chat → enqueue the existing **opaque `startmux` intent** (sessionId + tool +
  muxName; the PC resolves the real resume command from the local archive — never a command over the
  wire) through the **lease/ack app-commands API** with an intent-journal id → poll the command
  outcome → select the new hosted tab. Errors surface the ack detail (e.g. "local copy already
  running" 409, which the relay already produces).
- **area/files**: `relay/public/index.html` (picker UI), `relay/server.js` only if the lease API needs
  an intentId passthrough (it should already be idempotent post-integration).
- **verifier**: `cd relay && node --test tests/resume-picker.test.js` — seed archive index; simulate
  the loopback app consuming the lease and acking done + a session appearing on the fake host; assert
  the flow: one enqueued command per double-tap (journal dedupe), outcome polled, tab list contains
  the new session; app-offline path shows the queued/failed state rather than silently nothing.
- **deps**: V7a + integration lane's finalized lease/ack semantics. **value**: high — this is the
  "one product" moment (W5).

### V8a — Clean-transcript projection for a live session (app side)
- **goal**: A bridge-handled command (`transcriptfetch`) that, given a sessionId, renders the chat via
  the Core reader (noise stripped, tolerant of the half-written live tail) into bounded, paged JSON
  and returns it to the relay (via the ack-payload or the existing upload store), with the same
  redaction posture as REMOTE.md (`CLR_REMOTE_REDACT_READS` semantics).
- **area/files**: app C# (Core projection + bridge handler); `relay/server.js` command allow-list +
  storage of the fetched pages.
- **verifier**: `dotnet test --filter TranscriptFetch` — projection from a fixture rollout (including
  a truncated in-flight last line) yields expected page structure, size caps, and redacted secrets;
  `cd relay && node --test tests/transcript-fetch.test.js` — command round-trips via fake loopback
  app and pages are served owner-gated.
- **deps**: land after integration-lane freeze of the app tree. Parallel with V7a.

### V8b — Reader view in the web UI
- **goal**: "Read as transcript" on a session tab (and on archive-picker rows): a clean reader
  (bubbles, roles, tool-call collapse) with "refresh" while the session is live; falls back to a
  helpful message when the PC bridge is offline.
- **area/files**: `relay/public/index.html` (or a new `reader.html` to avoid bloating the 160KB
  file — implementer's call, but a separate page is preferred; see seams).
- **verifier**: `cd relay && node --test tests/reader-ui.test.js` (DOM-in-vm): fixture pages render
  N message nodes with role classes; refresh re-fetches; offline fixture renders the fallback.
- **deps**: V8a.

---

## Dependency graph & parallelism

```
V0 ──┬── V1 ──┬── V2
     │        └── V4 (also needs V3a)
     ├── V3a ─┬── V3b ── V5
     │        └── V4
     ├── V6
     │
     ├── V7a ── V7b        (wave 2; after integration-lane freeze)
     └── V8a ── V8b        (wave 2; after integration-lane freeze)
```

- **Parallel set A (after V0)**: V1, V3a, V6-server — independent server.js areas (notify hook /
  new route / new route). If orch runs them truly concurrently, use worktree isolation or serialize
  merges into server.js; the areas don't overlap logically.
- **Parallel set B**: V2, V4, V3b, V5, V6-UI — but **index.html is one 160KB file: serialize all UI
  leaves** (order: V3b → V2 → V6-UI → V5 → V4-UI).
- **Wave 2 (parallel)**: V7a ∥ V8a, then V7b ∥ V8b.

## Ranking (value ÷ effort) and the first cut

| # | Leaf | Value | Effort | Note |
|---|------|-------|--------|------|
| 1 | V1 notify | ★★★★★ | S–M | poll→push; docs' own top ask |
| 2 | V3a send endpoint | ★★★★★ | S–M | primitive for everything below |
| 3 | V3b composer | ★★★★ | M | kills the worst phone friction |
| 4 | V4 send-when-idle | ★★★★ | M | new capability; shows off the lease/journal foundation |
| 5 | V6 fleet glance | ★★★☆ | S–M | daily triage |
| 6 | V2 chip+mute | ★★★ | S | completes V1 |
| 7 | V5 quick replies | ★★☆ | S | cheap sugar on V3 |
| 8 | V7a+V7b resume-from-archive | ★★★★ | M–L | the unification; crosses into C# |
| 9 | V8a+V8b transcript reader | ★★★ | M–L | crosses into C# |

**FIRST CUT (bounded, recommended)**: V0 → {V1, V3a, V6} → V3b → V4 → V2. Seven leaves, relay+web
only, zero C# writes, zero muxd writes, no dependence on the integration lane's in-flux app tree, and
it lands win-condition items 1–4. V5 rides along only if a UI agent has slack.

**Wave 2** (schedule only after the integration lane declares the app tree frozen and gates green):
V7a, V7b, V8a, V8b.

## Deferred (explicitly out)

- Voice input; LLM co-pilot fleet summarizer ("what are my agents doing" in prose); desktop
  split-view; full-text search over live scrollback; session share/guest links — no workflow strong
  enough per the mandate, or speculative effort.
- De-app'ing uploads / the queue consumer (state-and-next N4) and fleet migration to hosted +
  process-truth state (N1/N2) — reliability work, not value work; belongs to the
  integration/adversarial prongs.
- Web Push/PWA as the notification transport — ntfy first (an evening vs. a service-worker + VAPID
  project); revisit only if ntfy is rejected in daily use.
- Relay-side archive search beyond the pushed index (live queries into the PC archive) — the ~500-row
  index covers the resume workflow; a query channel is speculative.

## Cross-prong seams (for the reconcile step)

1. **`relay/server.js`** — every prong will want in. mux-value adds: notify module + hook, `/send`,
   deferred-send queue, `/api/fleet`, `/api/archive-index`, notify-toggle. These are additive
   routes/modules; the adversarial prong should ATTACK them after they land, not co-edit. One owner
   per merge window.
2. **`relay/public/index.html`** — single 160KB file; highest collision risk in the campaign.
   Propose: mux-value owns it for wave 1 (serialized leaf order above); any other prong's UI edits
   queue behind. (V8b deliberately targets a separate `reader.html` to stay off this seam.)
3. **`/api/app-commands` lease/ack semantics** — owned by the integration lane. V7b/V8a are pure
   *clients*; if the lease API surface shifts, only those two leaves rebase.
4. **App C# tree (`app/native/…`)** — shared with native-parity / win32-perf prongs and currently
   in-flux under the integration lane. V7a/V8a must land after freeze; reconcile should assign one
   owner for the bridge push/handler files.
5. **`muxd/`** — mux-value makes NO muxd changes (V3a is a client of the existing input-intent
   protocol). If native-parity touches muxd's input path, the V3a fake-host tests keep mux-value
   verifiable independently; a live end-to-end drill belongs to the final acceptance pass.
6. **`tools/run_gates.ps1` / test registration** — all prongs add tests; reconcile should have one
   lane own gate-file edits and take registrations as a list.
