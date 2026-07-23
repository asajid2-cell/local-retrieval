# Plan design revision — five closed decisions (native / value / perf batches)

*2026-07-22. Inputs: `campaign/research/orch-plan.json` (batches r.2 value, r.3 native, r.4 perf),
`max-native-refined.md`, `max-value-refined.md`, `max-perf-refined.md`. The reliability batch
(r.1) was deliberately NOT read — it is being handled separately; where a decision touches its
territory the boundary is stated, nothing is incorporated.*

*Tier mapping is the plan's own rule: `deep` = fable (planning/judging), `default` = opus (build).
Every leaf added here is a build leaf at `default`. No new planning leaves were added — all five
decisions are closed in this document; the only genuinely evidence-dependent residue (which resync
payload backpressure uses) is folded into the EXISTING deep judge (r.3 child 11), not deferred to
a new plan node.*

Ingestion summary (in-batch 1-based indexes per orch's schema):

| Batch | Change |
|---|---|
| r.3 | AMEND child 11 (judge: + BACKPRESSURE_RESYNC key), AMEND child 12 (H2: flag freeze + ordered snapshot emission), ADD children 16 (input lease), 17 (viewer backpressure), 18 (divergence detector) |
| r.2 | ADD children 18 (relay state retention), 19 (muxd intent-journal retention), 20 (three-process e2e smoke) |
| r.4 | no changes |

---

## 1. Backpressure policy — decided now, parameterized by the spike, not blocked on it

**Decision.** The policy core is fixed today, for every spike outcome:

1. The PTY/child is NEVER blocked or throttled on behalf of any viewer.
2. A slow viewer NEVER silently loses screen truth. On overflow of that viewer's bounded
   outbound queue, raw output for that viewer stops queueing and the viewer is marked
   **desynced**; when its socket drains it receives a **forced resync before any further raw
   output**, plus a visible gap-marker line in its scrollback ("— output dropped while this
   viewer lagged —") so the hole is represented, not hidden.
3. The spike outcome selects only the **resync payload**:
   - **Model GO** (any candidate pinned by nat-H1J; session flag-on and model live): resync =
     model snapshot in the pinned SNAPSHOT_FORMAT, applied client-side as a **total
     replacement** (`term.reset()` + write — never a merge). This is the correct form of
     "coalescing": once an authoritative server-side screen model exists, the intermediate
     states a lagging viewer missed are redundant by construction.
   - **Model NO-GO / flag-off session / model dead or lagging**: resync = the existing raw-ring
     `sb` attach replay. Same policy, cruder payload: the ring already is the system's
     resync-from-history primitive, and replay-at-written-width is honest (it repaints exactly
     what the ring holds; the width-garble caveat is nat-H3's separate problem).

Ordering is made exact rather than hoped-for: while desynced the client **discards** incoming raw
frames until the rid-correlated resync payload arrives, and muxd emits snapshot responses through
the per-session ordered output path (pinned in the r.3 child 12 amendment below), so
snapshot-then-raw on a FIFO socket is exact, no sequence numbers needed.

**Rejected alternatives.**
- *Blocking* (propagate viewer backpressure into the pseudo-terminal): couples the child process
  — and every other viewer of the shared session — to the slowest phone on the worst network; it
  also violates muxd/PLAN.md's bounded-resources contract ("output bounded and drained"). One
  viewer's radio conditions must never stall a running build.
- *Silent discard*: cheapest to build, and exactly the failure the campaign exists to remove —
  the viewer believes it is looking at the screen while a window of it is missing. Fidelity means
  the screen may be *late* or *visibly gapped*, never *wrong without saying so*.
- *Unconditional coalescing now*: wrong for a raw byte stream (dropping arbitrary VT bytes
  corrupts parser state, not just content). Coalescing only becomes correct through the model —
  which is precisely what the GO branch ships.
- *Deferring the decision until after the spike*: rejected — the spike changes one parameter
  (resync source), not the policy. Waiting would leave the invariants unstated during exactly the
  window other leaves (input continuity, fleet tails, e2e smoke) are being built against them.

**Boundary note (reliability batch).** The reliability prong's existing leaf characterizes current
behavior with tests and changes no policy. Those tests stay the current-behavior baseline; the
leaf below defines the target policy. The orchestrator owns sequencing the two at merge; nothing
from that batch is incorporated here.

**Tree changes.**

AMEND r.3 child 11 (judge, kind `judge`, tier `deep` — fable), append to goal:

> Additionally pin BACKPRESSURE_RESYNC: the resync payload a desynced viewer receives — model
> snapshot in the pinned SNAPSHOT_FORMAT for flag-on sessions with a live model; raw-ring 'sb'
> replay otherwise — with any candidate-specific bounds (max snapshot bytes, capture cost under
> flood). The policy core (never block the PTY; never silently drop without a forced resync + a
> visible gap marker) is FIXED by campaign decision and is NOT re-open for debate; the judge
> chooses payload/format only.

and replace its verifier with:

```
python -c "import re;s=open('campaign/research/decisions/nat-h-model.md',encoding='utf-8').read();assert all(re.search(k+r':\\s*\\S+',s) for k in ('EMULATOR','SNAPSHOT_FORMAT','REFLOW','MEMORY_BOUND','PROCESS_MODEL','BACKPRESSURE_RESYNC'))"
```

ADD r.3 child 17:

```json
{
  "title": "viewer backpressure: bounded per-viewer queue, drop-and-resync, never block the PTY",
  "kind": "build",
  "tier": "default",
  "deps": ["12"],
  "goal": "Implement the pinned backpressure policy at the relay fan-out. relay/server.js: per-viewer bounded outbound budget (ws bufferedAmount + queued bytes, cap MUX_VIEWER_BUF_CAP default 1MB, injectable small in MUX_TEST_MODE); on breach stop queueing raw output for THAT viewer only and mark it desynced; the host link and other viewers are never paused. On drain: emit a rid-correlated resync BEFORE any newer raw output — request the muxd snapshot when the session advertises a live model (caps from r.3 child 12), else the existing raw-ring 'sb' replay. Client side (relay/public/index.html terminal-behavior block + relay/public/resync.js, vm-testable): while desynced, discard incoming raw frames until the rid'd resync arrives; apply snapshot as TOTAL replacement (term.reset + write, never merge) or replay the sb payload; insert a visible gap-marker line into scrollback on every resync so the dropped window is represented; bump window.__muxBuild. The reliability batch's backpressure characterization tests are the current-behavior baseline — coordinate supersession at merge (orchestrator seam); do not co-edit that batch. Tests (tests/backpressure.test.js, harness + FakeHost): (1) viewer's raw TCP socket paused, FakeHost floods 5MB -> relay keeps consuming host frames at full rate (processed-frame count grows; FakeHost sees no stall) while the paused viewer's queue stays under cap (gauge exposed in /api/health under MUX_TEST_MODE); (2) socket resumed -> viewer receives resync payload BEFORE any post-flood raw bytes, then live bytes; (3) a second healthy viewer receives the entire flood uninterrupted throughout; (4) vm test of resync.js: desynced state drops raw until the rid matches, applies reset+write, emits exactly one gap marker per episode.",
  "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/backpressure.test.js",
  "reads": [
    {"path": "relay/server.js", "span": "1800-1840"},
    {"path": "relay/public/index.html", "span": "1170-1210"},
    {"path": "relay/tests/harness.js"},
    {"path": "campaign/research/decisions/nat-h-model.md"}
  ]
}
```

**Honesty mechanism.** Three layers: (a) the judge verifier now hard-fails unless
BACKPRESSURE_RESYNC is pinned in `nat-h-model.md` — the decision cannot silently stay open;
(b) the leaf's test pair encodes both invariants directly — host-link throughput asserted *while*
a viewer is stalled (no blocking), and resync-before-raw + gap marker asserted on recovery (no
silent misrepresentation); (c) the divergence detector (item 2) runs immediately after every
resync event, so a resync that produced a wrong screen is caught at runtime, not just in fixtures.

---

## 2. One authoritative interpreter — commit: optimistic local rendering with a defined reconciliation protocol

**Decision.** The browser client **remains an interpreter** (xterm.js consuming the raw byte
stream) in steady state. The server-side model is **authoritative at defined sync points only**,
and its authority is exercised through exactly one operation: **total replacement**. Pinned
protocol:

1. Snapshots are applied only at defined resync points: attach with width mismatch (nat-H3),
   backpressure recovery (item 1), and divergence correction (below). Never speculatively, never
   on a timer.
2. Snapshot application is `term.reset()` + write of the snapshot payload — a full state
   replacement. Blending a snapshot into a live locally-interpreted terminal is forbidden; that
   ad-hoc hybrid is the "divergence in disguise" this decision exists to exclude.
3. Stream consistency at a resync point comes from ordering, not merging: muxd captures the
   snapshot under the session output lock and emits it through the per-session ordered output
   path (r.3 child 12 amendment); the client discards raw frames from resync-request until the
   rid'd snapshot arrives, then resumes interpreting raw from that exact point.
4. Between sync points, divergence is **detected and corrected**, not assumed away: a canonical
   screen digest compared client-vs-model, with self-heal via snapshot repaint.

**Rejected alternative: client as pure painter of model snapshots/deltas.** It does remove the
second interpreter — and it also removes xterm.js's renderer, selection, IME handling, and
scrollback, i.e. rewrites the client pipeline this same prong is currently polishing (nat-C/D,
input continuity) and regresses the shipped native-feel work. Steady-state it makes every painted
cell dependent on a server round trip through the VPS, so degradation on flaky mobile links is
strictly worse than raw-stream + local interpretation. And it is disproportionate: the model's
campaign duties are attach repaint, sbtext export, and resync — boundary events, not frame-rate
rendering. The divergence the painter prevents structurally, we instead detect with bounded
staleness and correct with the same snapshot machinery. If the detector's production mismatch
counter ever shows steady-state divergence is real and frequent, THAT number is the evidence a
painter redesign would need — the detector is how we would find out, either way.

**Tree changes.**

ADD r.3 child 18:

```json
{
  "title": "screen-state divergence detector: client-vs-model digest with self-heal",
  "kind": "build",
  "tier": "default",
  "deps": ["12"],
  "goal": "Pin ONE canonical screen digest for every interpreter: visible rows as plain text, trailing spaces trimmed, trailing blank rows dropped, rows joined with '\\n', UTF-8, sha256 hex; cursor {row,col} carried alongside. Attributes are excluded — text+cursor is the honesty floor; attribute fidelity stays covered by the spike corpus percentages. muxd: {t:'digest',s,rid} -> {t:'digest',rid,hash,cursor} served from the terminal model, flag-on sessions only, capability advertised beside 'snapshot', response emitted through the same ordered output path. Client: relay/public/divergence.js with a pure digestFromTerm(term) over xterm's buffer API + index.html wiring in the terminal-behavior block; when the digest cap is present, compare on a slow idle cadence (default every 30s of viewer idle, env MUX_DIVERGE_CHECK_MS, shrinkable in test) and IMMEDIATELY after every resync event; on mismatch increment window.__muxDivergence, log both hashes + cursors, and self-heal via a snapshot repaint (total replacement per the pinned reconciliation protocol); >3 mismatches in 10 minutes surfaces a persistent status chip instead of silent repaint-thrashing. Bump window.__muxBuild. Cross-interpreter fixtures: every muxd/spike/vt-fidelity fixture gains an expected-digest file; the node test computes digests via @xterm/headless and the python test via the pinned model emulator, BOTH must reproduce the expected values — corpus-level agreement is proven, the runtime detector covers everything the corpus misses. Tests: node — digestFromTerm deterministic over a fake buffer, fixture digests match expected, mismatch path triggers exactly one (stubbed) snapshot repaint, chip threshold logic pure-tested; python — model digest matches expected per fixture, digest of a flag-off session refused.",
  "verifier": "node --test --test-force-exit --test-timeout=20000 relay/tests/divergence.test.js && timeout 120 python -m pytest muxd/tests/test_model_digest.py -q",
  "reads": [
    {"path": "campaign/research/decisions/nat-h-model.md"},
    {"path": "muxd/muxd.py", "span": "3600-3680"},
    {"path": "muxd/muxd.py", "span": "3810-3870"},
    {"path": "relay/public/index.html", "span": "1530-1600"},
    {"path": "muxd/spike/vt-fidelity/fixtures"}
  ]
}
```

**Honesty mechanism.** The digest is defined once, byte-precisely, and both interpreters must
reproduce shared expected-digest fixtures — the two implementations cannot quietly drift apart in
what they even mean by "the screen". At runtime the detector is flag-gated but always on for
model sessions, fires after every resync (the exact moments authority changes hands), counts
mismatches in `window.__muxDivergence`, and escalates repeated mismatch to a visible chip. A
hybrid that lies gets caught by its own plumbing.

---

## 3. Input arbitration — a soft write lease at the relay: last-writer-wins with a freshness window, explicit steal

**Decision.** The relay serializes input across attached web viewers with a per-session **soft
write lease**: any viewer's typing acquires the lease if it is free or stale (no input from the
holder for `MUX_INPUT_LEASE_MS`, default 3000 ms); while the lease is fresh, other viewers' input
frames are dropped at the relay and the sender is told (`{t:'lease', holder, active:true}`),
rendering a read-only banner with a one-tap explicit takeover (`{t:'lease-steal'}`). Deliberate
server-originated writes — `POST /api/sessions/:name/send` and send-when-idle queue deliveries —
carry steal semantics (a deliberate act by the owner outranks a stale keyboard). The lease is
in-memory soft state: relay restart clears it and the first typer reacquires; nothing durable,
nothing muxd-side.

This targets the actual correctness hazard: two devices typing **concurrently** interleave bytes
mid-word or mid-escape-sequence into one PTY — unrecoverable garbage. It deliberately does not
add friction to the dominant real scenario, which is one human alternating between phone and
desktop: walk to the other device, start typing, the lease follows after 3 idle seconds (or one
tap immediately).

**Rejected alternatives.**
- *Strict single-writer with explicit handoff only*: safest on paper, but it taxes every device
  switch of a single user — the common case — to guard simultaneous typing by two humans, the
  rare case. Friction that predictable gets disabled, and a disabled arbiter is no arbiter.
- *Status quo free-for-all*: the interleaving hazard above, plus it silently contradicts the
  "shared model makes output converge" story — converged output with garbled merged input is not
  a coherent session.
- *Enforcing at muxd*: muxd sees ONE host-link connection for all web viewers and cannot tell
  them apart; giving it per-viewer identity means widening the frozen protocol 4 (additive field,
  but pure cost — the relay is the natural fan-in point and already owns per-ws identity).
  Local muxctl attaches are explicitly out of scope this campaign (single-seat PC); recorded as a
  known seam, not solved speculatively.

**Tree changes.**

ADD r.3 child 16:

```json
{
  "title": "relay input write-lease: one writer across viewers + two-viewer test",
  "kind": "build",
  "tier": "default",
  "deps": [],
  "goal": "relay/server.js: assign each viewer ws a connection id at attach; per-session lease {holderId, lastInputTs} in memory. An 'i' frame from a non-holder is forwarded only if the lease is free or stale (now - lastInputTs > MUX_INPUT_LEASE_MS, default 3000, shrinkable in MUX_TEST_MODE) — which transfers the lease; otherwise the frame is DROPPED and the sender receives {t:'lease', holder, active:true}. {t:'lease-steal'} transfers immediately. POST /api/sessions/:name/send and send-when-idle deliveries acquire the lease (steal semantics — a deliberate action). Client: relay/public/input-lease.js (vm-testable banner/state logic) + index.html wiring in the terminal-behavior block — read-only banner 'another viewer is typing — tap to take over', tap sends lease-steal; bump window.__muxBuild. Local muxctl attaches OUT OF SCOPE (protocol 4 frozen; relay is the only point that can distinguish web viewers). Seam: imports relay/tests/harness.js (r.2 child 1 contract, pinned in goal text per crossProngPolicy). Tests (tests/input-lease.test.js): two viewer ws clients on one session — A types -> FakeHost receives A's bytes; B types inside the window -> FakeHost receives NOTHING from B and B got the lease notice; B steals -> B's next keys arrive and A is now rejected; idle past the window -> A types successfully; POST /send during A's fresh lease -> 200, delivered, and A's next immediate keystroke is rejected (lease moved). During the contention phase FakeHost's recorded input stream contains bytes from exactly one writer (no interleaving). vm test: banner state machine (active/read-only/steal). Section assert: index.html routes 'i' sends through the lease-aware path.",
  "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/input-lease.test.js",
  "reads": [
    {"path": "relay/server.js", "span": "2280-2330"},
    {"path": "relay/public/index.html", "span": "1170-1210"},
    {"path": "relay/tests/harness.js"}
  ]
}
```

**Honesty mechanism.** The two-viewer contention test asserts the core claim at the only place it
can be proven — FakeHost's recorded input stream contains exactly one writer's bytes during
contention — and asserts every transition (implicit acquire, rejection notice, explicit steal,
staleness expiry, /send steal). The section assert prevents a future input-path edit from quietly
bypassing the lease gate.

---

## 4. Model sidecar flag semantics — evaluate at session creation, frozen for the session's lifetime

**Decision.** `MUXD_TERM_MODEL` is read once when a session is created and stamped immutably on
the session (`model_enabled`). No mid-session transition exists in either direction. Flag flips
affect new sessions only. `snapshot`/`sbtext`/`digest` against a frozen-off session return a
`model-off` error for that session's entire lifetime, even if the daemon-level flag is now on;
capability/protocol info reports **per-session** model state so a mixed population is visible to
clients and operators rather than inferred.

**Rejected alternative: dynamic per-operation evaluation.** Enable-mid-session hands out a model
that never ingested the session's earlier bytes — its screen is wrong by construction, so
snapshot, attach-repaint, and sbtext would all lie precisely when someone flips the flag to start
trusting them; fixing that means a warm-from-ring bootstrap plus reconciliation, i.e. rebuilding
item 2's divergence problem inside the daemon. Disable-mid-session invalidates in-flight rids and
strands attached clients that feature-detected the capability at attach. Freezing deletes the
entire transition state space for the cost of one restart-to-adopt (and muxd deploys/restarts are
free this campaign — governing call, `scripts/deploy-muxd.ps1` + MuxdSessionHostRestart — so
adoption latency is minutes, not a real cost).

**Tree changes.** AMEND r.3 child 12 (H2 sidecar leaf, build, `default`), append to goal:

> FLAG FREEZE: MUXD_TERM_MODEL is evaluated ONCE at session creation and stamped immutable on the
> session (model_enabled); flips affect NEW sessions only; snapshot/sbtext/digest on a frozen-off
> session return a 'model-off' err for the session's whole lifetime even if the daemon-level flag
> is now on; protocol/session info reports per-session model state so mixed populations are
> visible. No mid-session enable (a model attached mid-stream has not ingested prior bytes — its
> screen would be a lie) and no mid-session disable (invalidates in-flight rids).
> ORDERING: snapshot (and digest) responses are captured under the session output lock and
> emitted through the per-session ordered output path, so a consumer that discards raw frames
> until a rid'd snapshot arrives gets exact snapshot-then-raw consistency.
> test_terminal_model.py additions: (5) create session with flag off, then flip the effective
> flag -> session still refuses model ops and never starts ingesting (ingest-tap call count stays
> 0); (6) per-session model state present in protocol/session info for both populations;
> (7) ordering proof — snapshot requested during a concurrent flood: applying snapshot then the
> subsequent raw frames reproduces the oracle screen.

Verifier unchanged (`timeout 300 python -m pytest muxd/tests/test_terminal_model.py muxd/tests/test_muxd_state.py -q`) — the new cases live in the same file the verifier already runs.

**Honesty mechanism.** Test (5) is the freeze proven from the outside (behavior + ingest-tap call
count, not a config assertion); test (6) makes mixed populations observable so nobody debugs a
"broken flag" that is actually the freeze working as designed; test (7) pins the ordering
guarantee items 1 and 2 lean on to the leaf that owns the emitting code.

---

## 5a. Unbounded growth — retention, pruning, and size caps with tests

**Decision.** Two retention leaves — one per accumulating process — plus an explicit audit of
which stores are already bounded and need assertion only. Caps are count+size (not TTL-only:
a burst of activity blows through any TTL, and a TTL silently deletes the only copy of something
still useful; "newest N + total bytes" matches the stores' latest-fetch-only semantics).

Audit of the named accumulators, within these three batches:

| Store | Status | Action |
|---|---|---|
| Relay transcript page store (r.2 child 7) | bounded per session (~8MB, latest fetch only), **unbounded across sessions** | prune: newest ≤20 sessions AND total ≤256MB (r.2 child 18) |
| Relay app-commands journal (pre-existing, extended by this batch) | grows without limit | prune terminal-outcome commands >14 days, floor keep newest 200; never prune non-terminal (r.2 child 18) |
| muxd durable input-intent journal (routine after r.2 child 3) | grows per session | keep completed/error records max(7 days, newest 512)/session; NEVER prune uncertain; drop with session (r.2 child 19) |
| Rendered-history export (sbtext, r.3 child 14) | **never persisted server-side** — request-scoped, response-size-capped in test_sbtext; overlay holds one tail in browser memory | no leaf; the cap assert already exists. If the head knows a persistence path for exports, that's new information — name it and child 18 absorbs it |
| Relay archive index (r.2 children 4/5) | single file, 500-row cap, replaced per push | assert-only in child 18's sweep |
| Episode/notify state, deferred-send queue | single small file / depth 8 | assert-only |
| Client intent-journal / send history (localStorage) | capped 256 / 50 | none needed |
| durable-state `.bak` sidecars | 1:1 per state file | none needed; STATE_DIR gauge covers the aggregate |
| Off-box backups of STATE_DIR | ops/deploy territory (and the reliability prong's ops-health domain) | out of these batches; child 18's `stateDirBytes` gauge is the number any backup rotation will be sized from |

**Rejected alternative:** one campaign-wide "retention framework" leaf. The stores live in two
processes with two test rigs and two owners; a shared framework is a new cross-prong seam for
zero shared code (the relay side is JSON files under STATE_DIR, the muxd side is intent records
inside the durable machinery). Two small leaves, each testable in its own suite, each behind the
existing gate.

**Tree changes.**

ADD r.2 child 18:

```json
{
  "title": "Relay state retention: transcript-store and command-journal caps + STATE_DIR gauges",
  "kind": "build",
  "tier": "default",
  "deps": ["7"],
  "goal": "relay/retention.js wired into server.js: a retention pass at boot and every 6h (timer; interval shrinkable in MUX_TEST_MODE), plus inline enforcement at transcript-push accept time. (1) Transcript page store: global caps across sessions — keep newest <=20 sessions by fetch time AND total <=256MB (both env-tunable); prune oldest whole-session stores beyond either cap; the per-session bounds from the transcript-store leaf are unchanged. (2) App-commands journal: prune terminal-outcome commands older than 14 days, always retaining the newest 200; NEVER prune a command in a non-terminal state. Dedupe-horizon note, stated and tested: pruning a terminal record re-enables replay of its intentId — acceptable because the client intent-journal's retry horizon is minutes (3 attempts, 250/750ms backoff, 256-record cap) and 14 days is orders of magnitude beyond any real redelivery. (3) Archive index, episode state, deferred-send queue: already bounded — the sweep asserts their invariants, prunes nothing. (4) /api/health gains stateDirBytes plus per-store size/count gauges so growth is observable before it is a problem. All mutations through writeJsonState/unlink under the existing persistence discipline (persistenceBlocked respected). Tests (tests/retention.test.js, harness): fabricate 25 session transcript stores with skewed ages/sizes -> sweep -> newest 20 kept, total under cap, oldest deleted; fabricate 500 aged terminal commands + 5 in-flight -> in-flight all survive, newest-200 floor holds; a deduplicated:true response still returned for an intentId inside the horizon; gauges present and accurate; harness.restart() mid-state -> sweep converges to the same bounds.",
  "verifier": "cd relay && node --test --test-force-exit --test-timeout=60000 tests/retention.test.js",
  "reads": [
    {"path": "relay/server.js", "span": "1190-1530"},
    {"path": "relay/durable-state.js"},
    {"path": "relay/tests/harness.js"}
  ]
}
```

ADD r.2 child 19:

```json
{
  "title": "muxd durable input-intent journal retention",
  "kind": "build",
  "tier": "default",
  "deps": ["3"],
  "goal": "Prune the persisted input-intent records that the host-link input leaf makes routine. Per session: keep completed/error records for max(7 days, newest 512); NEVER prune a record in the uncertain/in-flight state — refusing unsafe replay is its entire purpose and it must survive any sweep; drop a session's records when the session is deleted. Sweep runs at daemon start and on session GC, bounded work per pass, inside the same persistence discipline as the durable machinery (muxd.py:2672-2723). Replay semantics unchanged inside the horizon. Tests: fabricated journal (N old completed + 1 uncertain + fresh completed) -> sweep -> uncertain and fresh survive, old completed gone; replay of a surviving completed intentId returns the cached result; replay of the uncertain intentId still refuses; replay of a PRUNED intentId re-executes (the documented horizon behavior, asserted so it is a decision, not a surprise); session delete removes its records.",
  "verifier": "cd muxd && timeout 120 python -m unittest discover -s tests -p \"test_intent_retention*.py\"",
  "reads": [
    {"path": "muxd/muxd.py", "span": "2600-2760"},
    {"path": "muxd/tests/test_muxd_state.py"}
  ]
}
```

**Honesty mechanism.** Every cap is asserted from fabricated over-limit state, including the two
dangerous edges people skip: the never-prune classes (in-flight commands, uncertain intents) are
asserted to survive a sweep, and the dedupe-horizon consequence (a pruned intentId re-executes)
is asserted explicitly so the tradeoff is on the record. The `/api/health` gauges make growth a
number the ops-health side can alert on instead of a disk-full surprise. Both test files enter
the standing gates automatically (widened relay `tests\` glob from r.2 child 17; muxd unittest
discover).

## 5b. Whole-path coverage — a three-process smoke test

**Decision.** One hermetic test that runs the real trio — real `server.js`, real `muxd.py` with a
real ConPTY child, and a plain ws client speaking the browser protocol — and drives a keystroke
and a durable send all the way around. Every existing "end-to-end" test covers both sides of one
frame against a fake peer; this is the only test that proves the two protocol halves actually
compose.

**Rejected alternatives.** A live-VPS smoke (the `MUXD_VPS_TESTS=1` pattern): not hermetic, needs
the deployed relay, can't gate merges. A browser-automation (Playwright) smoke: adds a heavy
dependency to assert what the ws protocol already exposes; the browser-specific rendering layer
is covered by the vm-harness suites, and item 2's digest fixtures tie that layer to the model.
Skip-if-muxd-absent semantics: rejected outright — a skippable smoke test is decorative; it fails
red if muxd cannot be located.

**Tree changes.**

ADD r.2 child 20:

```json
{
  "title": "Three-process e2e smoke: browser-protocol client -> relay -> muxd -> ConPTY and back",
  "kind": "build",
  "tier": "default",
  "deps": ["1", "9"],
  "goal": "tests/e2e-smoke.test.js: spawn the REAL relay server.js on a free port with temp STATE_DIR (RelayHarness); spawn a REAL muxd (python muxd/muxd.py on a free local port) configured to connect its host link to the local relay with the test token — muxd resolved via MUXD_DIR env, else ../muxd relative to the relay tree, else C:/Users/Ahmed/muxd; FAIL RED if none exists, never skip. Connect a plain ws client speaking the browser protocol. Assert in order: host hello arrives with caps; create a session running a real ConPTY child (python -u line-echo loop); client attach receives the replay; client sends 'i' keystrokes carrying a unique marker -> output frames echoing the marker arrive back at the client; resize round-trips without error; POST /api/sessions/:name/send with a second marker (durable path) -> 200 and that marker echoes to the client too; kill the session -> clean teardown. Bounded everywhere: hard per-step timeouts inside the file-level budget, both child processes killed in finally on every exit path, free ports only. Gate entry is automatic via the widened tests/ glob (r.2 child 17 owns the gate file — no edit here); in the external gate checkout the MUXD_DIR fallback chain resolves to C:/Users/Ahmed/muxd. Scope: this smoke proves composition, not features — new capabilities get their asserts in their own suites, not appended here (keep it under ~60s so the widened 120s file budget holds).",
  "verifier": "cd relay && node --test --test-force-exit --test-timeout=120000 tests/e2e-smoke.test.js",
  "reads": [
    {"path": "relay/tests/harness.js"},
    {"path": "muxd/muxd.py", "span": "3770-3873"},
    {"path": "relay/server.js", "span": "200-340"},
    {"path": "muxd/tests/test_vps_smoke.py"}
  ]
}
```

**Honesty mechanism.** No fakes anywhere in the loop, no skip path, hard-red on missing muxd, and
automatic membership in the widened relay gate — so from the moment it lands, every campaign merge
re-proves the full browser→relay→PC→terminal→back circle, not just per-frame halves. The scope
sentence in the goal stops it from decaying into a slow kitchen-sink suite.

---

## Consolidated ingestion notes

- New leaves are appended so existing in-batch dep indexes stay valid: r.2 gains 18/19/20,
  r.3 gains 16/17/18. New deps used: r.2 18→["7"], 19→["3"], 20→["1","9"]; r.3 16→[],
  17→["12"], 18→["12"].
- Amendments touch only r.3 children 11 (goal + verifier) and 12 (goal; verifier unchanged).
- Cross-batch relationships are pinned in goal text, not deps, per the plan's crossProngPolicy
  (harness.js contract for r.3 children 16/17; gate ownership stays with r.2 child 17; the
  reliability batch's backpressure characterization supersession is an orchestrator merge seam).
- Every new leaf is `kind: build`, `tier: default` (opus). The only deep/fable work added is
  inside the existing judge, which was already deep.
