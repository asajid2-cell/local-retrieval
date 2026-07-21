# PRONG PLAN — mux-adversarial: adversarially harden the multiplex/muxd remoting system

*Planner: Fable deep planner, 2026-07-21. Read-only recon: `relay/full-review.md`, `relay/state-and-next.md`,
`relay/MUX_SYSTEM_AUDIT.md` (2026-07-15, findings 1–36), plus three code sweeps (relay server, muxd state
lifecycle, browser terminal client). All file:line anchors verified against the current tree on 2026-07-21
unless flagged as drift-prone.*

---

## 0. Ground truth that reshapes the mandate (read before assigning leaves)

Three seeded assumptions are STALE — the plan below plans against verified current reality:

1. **The relay burst-drop is already fixed.** All viewer writes now funnel through
   `sendViewer(name, st, client, data, replayBurst)` (`relay/server.js:2118`) with real backpressure:
   `bufferedAmount + bytes > limit → removeViewer + ws.terminate()` (`:2121-2124`), high-water
   `VIEWER_HIGH_WATER_BYTES` = 4 MiB (`:2051`), a larger replay-burst limit (`:2052`), and a
   queue-then-flush path for bursts arriving while scrollback is pending (`:1867-1878`). The old
   unbounded `c.ws.send(buf)` path exists only in `server.js.backup-20260712-*`. → The leaf is a
   **reproduced-failure regression drill that locks the fix in**, not a fix.
2. **Node 18 EOL is not real.** Production runs Node **v24.18.0** (verified via nginx/service audit,
   `MUX_SYSTEM_AUDIT.md` "Node is v24.18.0"). What IS missing: any `engines` pin, `.nvmrc`, or npm-test
   deploy gate (`package.json` `scripts.test` is still the stock error stub; `npm test` is known to
   hang without `--test-force-exit`). → The leaf is pin + gate, not an upgrade.
3. **The durable-state layer is already wired into the relay.** `server.js:11` requires
   `./durable-state`; `projects.json`, `app-commands.json`, `uploads-meta.json`, `pins.json`,
   `rename-intents.json` all load via `durableJsonLoad` and save via a `writeJsonState` wrapper with a
   fail-closed `persistenceBlocked` gate (`server.js:20-29`). muxd has an equivalent primitive
   (`_durable_commit_bytes`, `muxd.py:92`; `durable_json_write` `:144` with `.bak` + readback-verify),
   and **live-tabs.json already uses it** (`write_live_tabs`, `muxd.py:2130`). → The durable gap is NOT
   live-tabs durability; it is (a) launch-claim files (bare `O_EXCL` writes, no sweeper) and (b) the
   **custody/pruning logic** on top of durable files.

**In-flux warning:** `relay/server.js` grew 1441 → 2350 lines DURING recon (integration lane at work).
All `server.js` line numbers below will drift; every leaf cites a stable function/string anchor
alongside. Relay-touching leaves are gated on the integration lane settling (see §4).

### The open defects this prong owns (ranked)

| # | Defect | Evidence |
|---|---|---|
| A | **Custody staleness (systemic):** `live_tabs_snapshot` (`muxd.py:2109-2128`) and `sess_list` (`muxd.py:2301`) emit **every** manifest entry incl. dormant/dead placeholders; boot restores all of them (`muxd.py:3382`); dormant records are never GC'd (`:3414-3420`). So live-tabs.json lists dead tabs after restart AND stale sessions are re-announced to the relay on every reconnect (`:3733`). | muxd sweep §1/§3/§5 |
| B | **Expired launch reservations linger:** claims dir `%LOCALAPPDATA%\CodexLocalRetrieval\launch-claims\`, TTL 120 s, but cleanup is lazy — only on the next `acquire` for the SAME id (`muxd.py:1149-1151`; C# `SessionLaunchClaims.cs:438-461`). A claim for a never-relaunched session lingers forever. This is the systemic version of the button-level reclaim bug the integration lane is patching. | muxd sweep §2 |
| C | **No deploy drain:** zero `process.on(SIGTERM)` in `server.js`; `scripts/deploy-relay.sh` hard-restarts; every deploy drops all viewers + the muxd host link mid-frame. | relay sweep §2; audit #24 |
| D | **Agent state is footer-regex on BOTH ends:** relay `paneAgentState` (`server.js:341-358`, `/esc to inter|still thinking/i` etc.) and muxd `session_agent_status` (`muxd.py:2231-2256`, shell-prompt regexes `:2225-2227` + 25 s output-freshness). No process-tree truth despite muxd owning the PTY and already reporting `childPid` (`muxd.py:2285`). | relay sweep §7; muxd sweep §4; audit #16; full-review D4 |
| E | **Idempotency journal exists but the main UI bypasses it:** `intent-journal.js` (`postIntent`, dedupe + bounded retry) is loaded ONLY by `projects.html:249`; `index.html` performs ~15 mutating raw `fetch`es (kill `:1883,1910,1984`, create `:1912,2044`, relaunch `:1946,2018`, rename `:2098`, app-commands `:1848,2087,2091,2226`, uploads `:2206,2246-2247`, settabcolor `:1755`, autoheal `:1867`). Retried/refreshed taps on WAN flake = duplicate kills/creates/relaunches. | client sweep §4; relay sweep §5 |
| F | **muxd shared output queue drops bursts silently:** one `RelayOutQueue(maxsize=64)` across ALL sessions (`muxd.py:1205-1217`), full-queue frames silently dropped; overflow deletes the MIDDLE of the stream with no resync marker (`muxd.py:1496-1499`). A noisy session gaps unrelated terminals — this is the surviving burst-drop, now on the muxd side. | audit #18/#20 |
| G | **Session lifecycle races in the relay:** create-timeout leaves a phantom `hostSessions` row (audit #13); kill vs in-flight status rebuild resurrects deleted sessions (audit #14, `server.js` kill/rebuild anchors). Same custody class as A, relay-side. | audit #13/#14 |
| H | **No alerts, no backups, no test gate:** `/api/health` + status dot only (pull-only, `server.js:1167-1187`, no ntfy/webhook code); relay state files have `.bak` siblings but no off-box backup; `npm test` is a stub and hangs without `--test-force-exit`. | relay sweep §6/§8; audit #12; state-and-next N3 |
| I | **Glyph-blank recovery has zero rendering-level tests:** a well-developed recovery path exists (`recoverTerminalPaint` = `clearTextureAtlas` + heal + rAF, `index.html:816-834`; blank-detector canvas sampling `:761-810`; wedge watchdog `:934-944`) but is verified only by static string tests (`tests/client-layout.test.js`). Every refactor of the layout code (`display:none` toggles at `index.html:452`, `:215`) can silently break it. | client sweep §2/§6 |

---

## 1. Win condition

**Every defect A–I above is closed by a reproduced-failure drill turned into a repo-resident test, and the
whole battery is a deploy gate.** Concretely, all of the following pass from a clean checkout, bounded,
no manual steps:

1. `npm test` in `relay/` exits 0 cleanly (no hang, no orphan processes) and includes: burst/backpressure
   drill, SIGTERM drain drill, create-timeout + kill-resurrection generation tests, intent-journal dedup
   coverage for `index.html` mutations, and an alert-fire test.
2. `python -m unittest discover -s tests` in `muxd/` exits 0 and includes: custody-janitor tests (stale
   dormant records pruned from live-tabs + hello re-announce), claim-sweeper tests, per-session output
   queue fairness + resync-marker tests, and process-truth agent-state tests.
3. A restarted muxd with a manifest full of dead sessions produces a live-tabs.json and a relay `hello`
   containing **zero** entries for sessions whose custody expired — verified by test, not by screenshot.
4. The launch-claims directory contains no expired claim older than TTL+sweep-interval after the sweeper
   runs — verified by test.
5. `kill -TERM <relay>` closes viewer sockets with a retryable code within 2 s and exits within 5 s, and
   the browser client reconnects and shows a "restarting" toast — server side verified by test.
6. Agent-state dots for hosted sessions derive from muxd process-truth fields (versioned protocol field),
   with footer-regex demoted to an explicitly-tagged low-confidence fallback — verified by test on both ends.
7. A degraded-health transition (host link down > 5 min, watchdog gave-up, persistenceBlocked) fires
   exactly one deduplicated ntfy push — verified by test with a mocked transport.

---

## 2. Leaves (one-level decomposition)

Conventions: every verifier is bounded (explicit `--test-force-exit` / unittest / timeout). "Size" is a
one-sitting estimate for a fresh cheap agent. Relay tests follow the existing `node:test` + `MUX_STATE_DIR`
harness style in `relay/tests/relay.test.js`; muxd tests follow `muxd/tests/test_muxd_state.py` (importlib
loading, fault injection via env).

### L1 — Burst/backpressure regression drill (lock in the existing fix)
- **goal:** Turn the historical burst-drop failure into a permanent reproduced-failure test suite around
  `sendViewer` so the fix can never silently regress.
- **area/files:** `relay/tests/burst-backpressure.test.js` (new). NO `server.js` changes expected; if the
  drill exposes a live defect, file it back to orch as a new fix leaf rather than editing in-place.
- **what the drill does:** spawn relay with `MUX_STATE_DIR` sandbox + fake `/host` ws; (a) flood one
  session with ≥16 MiB of output while a deliberately-slow viewer (never reads) is attached → assert the
  slow viewer is **terminated** (not silently gapped) and a fast viewer receives a byte-complete stream;
  (b) burst-during-scrollback-wait → assert queue-flush path emits CLEAR + full replay in order (`:1867-1878`
  behavior); (c) replay burst just under/over `VIEWER_REPLAY_BURST_BYTES` → assert the boundary.
- **verifier:** `cd relay && node --test --test-force-exit tests/burst-backpressure.test.js` → exit 0,
  wall clock < 60 s.
- **deps:** integration-lane settle (server.js frozen). Parallel with L2, L6.
- **size:** M (~1 sitting; the relay.test.js harness patterns are copyable).

### L2 — SIGTERM deploy drain + client "restarting" toast
- **goal:** Deploys become invisible: on SIGTERM the relay stops accepting upgrades, notifies viewers with
  a retryable close, flushes state, and exits bounded; the client toasts and fast-reconnects.
- **area/files:** `relay/server.js` (signal handler; small, additive), `relay/public/index.html` (map the
  drain close code in `closeRefusal`/`onclose` `:1512-1517`/`:1572-1578` to an immediate-retry + toast via
  existing `flash()` `:1919`), `relay/tests/drain.test.js` (new), `relay/scripts/deploy-relay.sh` (ensure
  SIGTERM-first restart).
- **design pins:** close viewers with code **1012** (service restart) + reason `restarting`; close the
  `/host` link last; hard-exit deadline 5 s (`setTimeout(…,5000).unref()`); do NOT wait on in-flight
  express requests beyond the deadline. Client: on 1012, reset backoff to 500 ms and `flash('relay
  restarting…')` instead of exponential backoff.
- **verifier:** `cd relay && node --test --test-force-exit tests/drain.test.js` → exit 0, < 45 s. Test:
  spawn server as child process, attach viewer ws + fake host ws, send SIGTERM, assert (a) viewer receives
  close code 1012 within 2 s, (b) process exits with code 0 within 5 s, (c) a post-SIGTERM upgrade attempt
  is refused. Plus one static-source assertion (client-layout style) that index.html handles 1012.
- **deps:** integration-lane settle. Parallel with L1, L6.
- **size:** M.

### L3 — muxd custody janitor: dormant-record GC + stale-emission filter (defect A — the centerpiece)
- **goal:** Dead/expired sessions structurally self-clean: manifest records carry an explicit custody
  lifecycle with expiry, boot GCs expired dormant records, and `live_tabs_snapshot`/`sess_list` stop
  emitting placeholders the PC no longer owns.
- **area/files:** `muxd/muxd.py` (`live_tabs_snapshot` `:2109`, `sess_list` `:2301`, boot restore
  `:3344-3439`, manifest schema `manifest_payload` `:1983` / `valid_session_records` `:2010`),
  `muxd/tests/test_muxd_state.py` (extend).
- **design pins (structural ownership record, not a cron hack):**
  - Add `custodyExpiresUtc` + `lastAliveUtc` to session records (schema stays v2-compatible: unknown
    fields already tolerated by `valid_manifest`; bump only if validation requires).
  - Rules: `alive` sessions refresh `lastAliveUtc` on every status pump; a `dormant`/`failed` record whose
    custody expired (default TTL 24 h, env-overridable `MUX_CUSTODY_TTL_S`) is removed from the manifest
    at boot AND by the periodic status pump; `user_killed` records are removed immediately at next save.
  - `live_tabs_snapshot` emits ONLY sessions that are alive OR within custody TTL; `sess_list`/`hello`
    likewise, OR (if the relay UI needs dormant rows for relaunch) emits them with an explicit
    `custody:'expired'|'held'` field so the relay/app can filter — pick ONE contract and test it on both
    ends of the frame (relay filter is L5b's file; coordinate via the frame field only, do not edit relay
    here).
  - Never GC a record that holds a live launch claim or has `identityPending` within grace.
- **verifier:** `cd muxd && python -m unittest tests.test_muxd_state -v` → exit 0, < 120 s, including
  NEW tests: (a) seed a manifest with 1 alive + 1 fresh-dormant + 1 expired-dormant + 1 user_killed
  record, run boot restore, assert manifest retains exactly alive+fresh-dormant; (b) `live_tabs_snapshot`
  over that state contains no expired entries; (c) `sess_list` hello payload matches the chosen contract;
  (d) custody TTL refresh on simulated status pump.
- **deps:** none (muxd.py is not the integration lane's primary file, but see seam S3). FIRST in the
  muxd.py chain: L3 → L4 → L7 → L5a (same file; serialize).
- **size:** L (one sitting, but the largest; if the emit-contract decision balloons, split the
  `sess_list`/relay-contract half into its own leaf — flagged as the one candidate for `kind: plan`).

### L4 — Launch-claim sweeper (defect B)
- **goal:** Expired launch reservation files are swept automatically on both sides, closing the "Expired
  launch reservation files still present" class systemically (the integration lane owns the button; this
  leaf owns the janitor).
- **area/files:** `muxd/muxd.py` (claims: `acquire_launch_claim` `:1122`, expiry check `:1149-1151`,
  `CLAIM_ROOT` `:900`), `muxd/tests/test_muxd_state.py`. App-side (`SessionLaunchClaims.cs`) is the
  integration lane's — DO NOT touch; muxd's sweeper covers the shared directory for both writers since
  claim files are self-describing (`ExpiresUtc`, `OwnerPid`, `OwnerProcess`).
- **design pins:** a `sweep_launch_claims()` pass on muxd boot + every N minutes (piggyback the existing
  5 s status pump at a divided cadence, e.g. every 60th tick) that deletes claims where
  `ExpiresUtc <= now` OR (`OwnerProcess=='muxd'` AND owner pid dead). Quarantine-on-parse-failure (rename
  to `.claim.bad`) rather than delete, mirroring the app's quarantine tier. Never delete a claim younger
  than TTL even if the owner pid is unverifiable (fail-safe).
- **verifier:** `cd muxd && python -m unittest tests.test_muxd_state -k claim -v` → exit 0, < 60 s, with
  NEW tests: expired claim deleted; live-owner unexpired claim kept; dead-muxd-pid claim deleted;
  malformed claim quarantined not deleted; sweep is idempotent.
- **deps:** after L3 (same file). Seam S2 with integration lane (claims dir semantics — additive only).
- **size:** S.

### L5a — Process-truth agent state, muxd side (defect D, producer half)
- **goal:** muxd reports what is actually true — is the agent process (claude.exe/codex.exe descendant of
  the session's `childPid`) alive, and is it consuming CPU — as a versioned protocol field, so a TUI
  redesign can never again break death detection.
- **area/files:** `muxd/muxd.py` (`session_agent_status` `:2231`, `session_payload` `:2285` region,
  status pump `:3744`), `muxd/tests/` (new `test_agent_truth.py`).
- **design pins:**
  - New payload fields: `agentTruth: {procAlive: bool, cpuActiveRecent: bool, exe: str|null, checkedUtc}`,
    plus `agentStateSource: 'process'|'heuristic'`. Keep the existing heuristic output as fallback when
    the probe is stale/unavailable.
  - **Executor discipline is mandatory** (audit #6 — WMI bursts starved the default executor and froze
    all control ops): probes run on a small dedicated executor, results cached ≥10 s, per-probe timeout
    ≤3 s, never under `state_lock`. Prefer `psutil`-free stdlib approach used elsewhere in muxd; reuse
    `_pid_descends_from` machinery but bounded + cached.
- **verifier:** `cd muxd && python -m unittest tests.test_agent_truth -v` → exit 0, < 90 s: spawn a real
  dummy child (`python -c "import time; time.sleep(60)"`) under a fake session, assert
  `agentTruth.procAlive` true → kill it → assert flips false within one probe interval; assert a
  deliberately-hung probe (fault-injected via env, matching the existing `MUX_TEST_PERSIST_FAULT_FILE`
  pattern) leaves control ops responsive (status call completes < 1 s) and `agentStateSource` degrades to
  `'heuristic'`.
- **deps:** after L4 in the muxd.py chain. Frame contract shared with L5b.
- **size:** M.

### L5b — Process-truth agent state, relay side (defect D, consumer half)
- **goal:** The relay's state dots and autoheal decisions prefer `agentTruth` when present and mark
  regex-derived state as low-confidence; footer regex becomes a visibly degraded fallback, never an
  auto-heal authority.
- **area/files:** `relay/server.js` (`paneAgentState` `:341`, `hostAgentStatus`/`attentionStatusForHosted`
  `:401` region, autoheal decision sites), `relay/tests/agent-truth.test.js` (new).
- **design pins:** precedence = host `agentTruth` (fresh) > host heuristic > relay pane-regex; autoheal
  may only fire a death-recovery on `procAlive === false` or explicit host `dormant` — never on regex
  alone for hosted sessions. Unknown/absent field = today's behavior (backward compatible with an old muxd).
- **verifier:** `cd relay && node --test --test-force-exit tests/agent-truth.test.js` → exit 0, < 45 s:
  fake host ws pushes session frames with (a) `procAlive:false` + healthy-looking footer → state red /
  heal eligible; (b) `procAlive:true` + footer that matches the shell-prompt death regex → state NOT red,
  no heal; (c) no `agentTruth` field → legacy regex behavior preserved.
- **deps:** L5a (frame contract) + integration-lane settle. Parallel with L8/L9.
- **size:** M.

### L6 — Relay session generations/tombstones (defect G)
- **goal:** Kill create-timeout phantoms and kill/status resurrection with per-session operation
  generations, closing the relay-side half of the custody class.
- **area/files:** `relay/server.js` (optimistic insert on `POST /api/sessions`, kill delete, status
  rebuild in the `sessions` frame handler `:1800-1832` region), `relay/tests/lifecycle-generations.test.js`
  (new).
- **design pins:** creation = explicit `pending` state keyed by request id, removed on 15 s timeout;
  kill writes a tombstone `{name, gen, at}` honored by status rebuilds for 60 s (a status frame older
  than the tombstone cannot re-add the session); `hello` full-resync clears tombstones (host is the
  authority on reconnect).
- **verifier:** `cd relay && node --test --test-force-exit tests/lifecycle-generations.test.js` → exit 0,
  < 60 s: (a) create with a host that never confirms → after timeout `/api/sessions` shows no phantom
  row; (b) kill, then replay a pre-kill status frame → session stays dead; (c) kill, then host `hello`
  listing the session → session legitimately returns.
- **deps:** integration-lane settle. Parallel with L1, L2.
- **size:** M.

### L7 — muxd per-session output queues + explicit resync (defect F)
- **goal:** One flooding session can no longer gap other sessions' terminals, and any drop becomes an
  explicit resync (clear + tail replay) instead of a silent mid-stream deletion.
- **area/files:** `muxd/muxd.py` (`RelayOutQueue` `:1205-1217`, overflow trim `:1496-1499` + the
  `OwnerSession` duplicate), `muxd/tests/` (new `test_output_fairness.py`).
- **design pins:** per-session byte-bounded queues (e.g. 1 MiB each) drained round-robin into the ws;
  on per-session overflow, drop that session's backlog to a snapshot boundary and enqueue a
  `{t:'resync', s}` frame → relay already has the CLEAR+tail machinery (`requestSessionScrollback`) to
  honor it; the relay-side handler for `t:'resync'` is a ≤10-line addition — include it here (single
  frame type, coordinate with seam S1).
- **verifier:** `cd muxd && python -m unittest tests.test_output_fairness -v` → exit 0, < 90 s: two fake
  sessions, session A floods 10 MiB while B emits 100 numbered lines → assert all 100 B-frames egress in
  order; force A over its budget → assert exactly one `resync` frame for A and none for B; assert
  round-robin (B frames interleave, not starved behind A's backlog).
- **deps:** after L4 in the muxd.py chain (before or after L5a — orch's choice, same-file serial).
- **size:** M/L.

### L8 — intent-journal coverage for index.html mutations (defect E)
- **goal:** Every mutating action in the main terminal UI is idempotent under WAN flake/retry/refresh, by
  routing it through the EXISTING `postIntent` journal (extend, don't reinvent).
- **area/files:** `relay/public/index.html` (load `intent-journal.js`; convert the raw-fetch mutation
  sites listed in §0-E), `relay/public/intent-journal.js` (only if a gap like DELETE-support appears),
  `relay/tests/intent-coverage.test.js` (new), server dedup where missing.
- **design pins:** kill/relaunch/create/rename/app-commands/uploads-mutations go through `postIntent`;
  server routes that lack `intentId` dedup get it (app-commands enqueue already journals via
  `enqueueAppCommand`'s intent handling — verify; `relaunch` already dedupes for projects.html — reuse).
  Read-only GETs stay raw. Keep index.html's existing UX (flash/confirm) unchanged.
- **verifier:** `cd relay && node --test --test-force-exit tests/intent-coverage.test.js` → exit 0, < 60 s:
  (a) static-source assertions (client-layout.test.js style): index.html loads intent-journal.js and
  contains zero raw `fetch(` POST/DELETE/PATCH calls to the mutation endpoints (allowlist for reads);
  (b) integration: POST the same relaunch/app-command twice with one `intentId` → exactly one execution
  recorded (extends the existing `tests/relay.test.js:2196-2288` journal tests).
- **deps:** integration-lane settle (index.html is shared). Parallel with L5b, L9.
- **size:** M.

### L9 — Glyph-blank rendering harness (defect I)
- **goal:** The Chromium glyph-blank-on-relayout recovery path gets a real-browser test: reproduce the
  `display:none` relayout, assert the blank-detector + `clearTextureAtlas` recovery leaves a non-blank
  canvas — so layout refactors can't silently break it.
- **area/files:** `relay/tests/browser/glyph-blank.spec.js` (new, Playwright), `relay/package.json`
  (devDependency + `test:browser` script), tiny static-serve + stub-ws fixture under `relay/tests/browser/`.
- **design pins:** headless Chromium; serve `public/` with a stub ws feeding deterministic output; drill:
  fill terminal → toggle `mobile-view-sessions` / `projects` classes (the `display:none` paths,
  `index.html:452`, `:215`) → toggle back → sample the xterm canvas via the page's own
  `terminalPaintLooksBlank()` (`:795-810`) and direct pixel readback → assert non-blank within 2 s;
  also assert `recoverTerminalPaint` throttle (`:818`) doesn't wedge under repeated toggles. IMPORTANT:
  the upstream Chromium bug may not reproduce headlessly — the test asserts the RECOVERY INVARIANT
  (post-relayout repaint yields non-blank canvas + detector returns false), which regresses if anyone
  breaks the heal path, regardless of whether the upstream bug fires. Keep this suite OUT of `npm test`
  (separate `npm run test:browser`) so the deploy gate stays dependency-light.
- **verifier:** `cd relay && npx playwright test tests/browser/glyph-blank.spec.js --reporter=line` →
  exit 0, < 120 s (Playwright's default per-test timeout bounds it).
- **deps:** none hard (public/ read-mostly; the small index.html hook — exposing `terminalPaintLooksBlank`
  for the test — must not collide with L8's edits: run after L8 or restrict to `window.__muxTest` shim).
- **size:** M/L.

### L10a — Test gate + engines pin (defect H, part 1)
- **goal:** `npm test` becomes a real, clean, bounded deploy gate; the Node version is pinned so "what
  node is under this" is never a question again.
- **area/files:** `relay/package.json` (`scripts.test` = `node --test --test-force-exit tests/*.test.js`
  or equivalent with clean teardown fixed properly; `engines: {node: ">=22"}`; correct `main`),
  `relay/scripts/deploy-relay.sh` (run `npm test` before restart, abort on fail), fix the known
  non-terminating teardown in `tests/relay.test.js` (orphaned child `server.js` processes) if feasible
  in-sitting — else keep `--test-force-exit` and document why.
- **verifier:** `cd relay && npm test` → exit 0, < 180 s, AND `powershell -c` (or `pgrep`) asserts zero
  orphan `node.*server.js` processes after the run; `node -e "require('./package.json').engines.node ||
  process.exit(1)"` → exit 0.
- **deps:** best LAST among relay leaves (it gates everything that came before). L1/L2/L5b/L6/L8 tests
  join the gate here.
- **size:** S.

### L10b — Alerting: push on degraded transitions (defect H, part 2)
- **goal:** The first sign of trouble is a phone push, not a dead chat: degraded-health transitions fire
  a deduplicated ntfy notification.
- **area/files:** `relay/server.js` (a small `alerts.js` module preferred: watch the existing degraded
  computation `server.js:1176` region + `persistenceBlocked` + host-link-down duration + gave-up heals),
  `relay/tests/alerts.test.js` (new), env config (`NTFY_URL`/topic in `/etc/multiplex-app.env` — document,
  don't commit secrets).
- **design pins:** alert on EDGE transitions only (healthy→degraded sustained > 2 min; host link down
  > 5 min; any heal gave-up; persistenceBlocked), one push per condition per 30 min (dedupe window),
  plus a recovery push. Transport = plain `fetch` POST, injectable for tests. No new dependency.
- **verifier:** `cd relay && node --test --test-force-exit tests/alerts.test.js` → exit 0, < 45 s: drive
  synthetic health states through the module with a mock transport; assert fire-on-edge, dedupe window,
  recovery notice, and NO fire on a 30 s transient blip.
- **deps:** integration-lane settle. Parallel with anything.
- **size:** S/M.

### L10c — Off-box state backups (defect H, part 3)
- **goal:** Relay state (`projects.json`, `app-commands.json`, `pins.json`, `uploads-meta.json`,
  `rename-intents.json` + `.bak`s) survives VPS loss: nightly archived to the PC (the durable side).
- **area/files:** `relay/scripts/backup-state.sh` (new; tar + scp over the existing `win` ssh route or
  drop into an existing synced path), a cron/systemd-timer unit file (committed under `relay/ops/`),
  `relay/tests/backup.test.js` (script logic only, mocked scp).
- **verifier:** `cd relay && bash scripts/backup-state.sh --dry-run-to /tmp/mux-backup-test && tar -tzf
  /tmp/mux-backup-test/*.tgz | grep -q pins.json` → exit 0, < 30 s (script supports a local-target test
  mode; the scp leg is config, verified by the same script's `--verify` restore-roundtrip against the
  local archive).
- **deps:** none. Parallel with anything. (Deploy/cron installation on the VPS is an ops step the
  campaign's deploy lane owns — the leaf delivers script + unit + test.)
- **size:** S.

---

## 3. Dependency graph & parallelism

```
[GATE: integration lane settles server.js/index.html]
        │
        ├─ relay chain (parallel leaves, same repo area, distinct files where possible):
        │     L1 (burst drill)      ─┐
        │     L2 (drain)            ─┤─ all → L10a (test gate, LAST)
        │     L6 (generations)      ─┤
        │     L10b (alerts)         ─┘
        │     L5b (agent-truth consumer) ── needs L5a frame contract
        │     L8 (intent coverage, index.html) ── then L9 (glyph harness; or shimmed parallel)
        │
        └─ muxd chain (muxd.py = ONE file → strictly serialized):
              L3 (custody janitor) → L4 (claim sweeper) → L7 (output fairness) → L5a (agent truth)

L10c (backups): fully independent, can run first.
```

- **Parallel-safe NOW (before the gate):** L3, L4, L7, L5a (muxd, serialized among themselves), L10c, L9
  (fixture scaffolding).
- **Parallel-safe after the gate:** L1 ∥ L2 ∥ L6 ∥ L10b ∥ L8; then L5b (after L5a), then L10a last.
- L1/L2/L6 all add tests + touch different server.js regions, but orch should still serialize their
  server.js WRITE portions (L1 is tests-only; L2 and L6 edit server.js → serialize L2→L6 or vice versa).

## 4. Ranking & first cut

Value/effort ranking: **L3 > L2 > L4 > L5a > L1 > L6 > L8 > L10a > L10b > L7 > L5b > L9 > L10c**.

**BOUNDED FIRST CUT (highest value, lowest collision risk — recommend running now):**
1. **L3 custody janitor** — the mandate's centerpiece, muxd-only, no gate needed.
2. **L4 claim sweeper** — small, rides L3's context, closes the reservation-litter class.
3. **L10c backups** — independent, cheap, closes a real single-copy risk.
4. Then, the moment the integration lane settles: **L2 drain + L1 burst drill**, then **L5a**.

**Deferred explicitly (not in this prong's first cut, some not in this prong at all):**
- The MUX_SYSTEM_AUDIT security fix-order (findings 1–8B: muxd loopback auth, `muxd.env` ACL, relay
  0.0.0.0 bind, ambient loopback trust, LAN `ws://` + token-in-URL, command provenance). Real, HIGH, but
  a distinct security workstream with deploy-coordination constraints — needs its own owner/plan node
  (`kind: plan`, propose name `mux-security-fixorder`), not a cheap-agent leaf.
- muxd executor/lock refactor (audit #6/#17 dedicated-executor + snapshot-outside-lock) beyond the
  discipline L5a applies to its OWN probes — perf-shaped; likely the win32-perf prong's seam.
- Client byte-bounding + frozen-buffer budgets (audit #25C), protocol version negotiation (#21, #25F),
  mobile IME/dedup/viewport (#33, #25D, #25E), X10/1015 wheel (#31) — UX-shaped; value prong's territory.
- Node upgrade — moot (v24.18.0); only the pin (L10a) remains.
- muxd input at-most-once (#19/#22 input intent IDs) — same idempotency family as L8 but muxd-side;
  good SECOND-cut leaf after L8 proves the pattern (would be `L8b`, verifier analogous).

## 5. Cross-prong seams (one owner per seam — for reconcile)

| Seam | Files | This prong's claim | Likely other claimants |
|---|---|---|---|
| S1 | `relay/server.js` | L2, L5b, L6, L10b (bounded, cited regions) + L7's ≤10-line `resync` handler | **Integration lane (current writer — everyone waits for settle)**; value prong (features); win32-perf |
| S2 | `launch-claims/*` semantics + `app/.../SessionLaunchClaims.cs`, `SessionReclaim.cs`, `MainPage.Integrity.cs` | L4 adds a muxd-side sweeper over the SHARED dir; **zero app-side edits** | **Integration lane owns all app-side reclaim code** — L4 must be additive/fail-safe on shared files |
| S3 | `muxd/muxd.py` | L3, L4, L7, L5a (serialized chain) | win32-perf prong (executor/lock work #6/#17) — reconcile should hand them the SAME serialization token |
| S4 | `relay/public/index.html` | L2 (close-code map), L8 (mutation sites), L9 (test shim) | Value prong (UI features), audit XSS fix #25A if anyone takes it |
| S5 | host-link frame protocol (`hello`/`sessions`/`resync`/`agentTruth` fields) | L3 (custody field), L5a/L5b (agentTruth), L7 (resync) | Native-parity prong if it touches the protocol; any protocol-versioning leaf (#21) must supersede all of these — recommend reconcile assigns protocol-field REGISTRY ownership to one prong |
| S6 | `relay/tests/*` + `muxd/tests/*` harness conventions | Nearly every leaf adds tests; L10a owns the npm gate | All prongs — additive files, low collision; keep one-file-per-leaf naming as specced |

## 6. Sources
- `relay/full-review.md` (2026-07-02) — D1–D8, invariants 1–5, phased P0–P6.
- `relay/state-and-next.md` (2026-07-02) — B+/A− scorecard, N0–N5 ranked next steps (N2 = process-truth,
  N3 = ops floor: both are leaves here).
- `relay/MUX_SYSTEM_AUDIT.md` (2026-07-15) — findings #6, #12–#25, #29 cited per-leaf above; Node
  v24.18.0 correction; lease-protocol status.
- Code sweeps 2026-07-21 (this plan's recon): relay server map (sendViewer backpressure, durable wiring,
  lease queue live at `POST /api/app-commands/lease`, no SIGTERM handler, no engines pin); muxd state map
  (durable primitives, live-tabs/sess_list stale emission, claim lifecycle, footer-regex state); browser
  client map (canvas renderer, recovery path, intent-journal gap, no rendering tests).
