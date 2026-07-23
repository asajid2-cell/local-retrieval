# max-adv refined leaf set — mux-adversarial prong (remoting hardening)

Lane: max-adv. Validated against HEAD 7968259 (tree clean except untracked `campaign/research/`),
2026-07-22. Base plan: `campaign/mux-adversarial.md` (defects A–I, leaves L1–L10c) + cold-correctness
review (6 findings, 2026-07-22). All file:line cites below are from the CURRENT tree unless marked
"was".

## Validation

### File sizes (drift baseline)
- `relay/server.js` = 2334 lines (base-plan recon saw 2350 mid-flux; now settled).
- `relay/public/index.html` = 2626 lines. `muxd/muxd.py` = 3896 lines.

### Base-plan anchors — verified / drifted
| Anchor | Status now |
|---|---|
| `sendViewer` backpressure | HOLDS, drifted −2: `server.js:2116` (was :2118); limit check :2118-2123; `VIEWER_HIGH_WATER_BYTES` :2049, `VIEWER_REPLAY_BURST_BYTES` :2050. Burst-during-scrollback queue-flush now at :1867-1899 (CLEAR_SCREEN + replay, `sbWaiters` drain). |
| `paneAgentState` | HOLDS: `server.js:339` (was :341); `hostAgentStatus` :385, `attentionStatusForHosted` :399. Still footer-regex; no `agentTruth` anywhere in server.js (grep: 0 hits). |
| No SIGTERM handler (defect C) | CONFIRMED: `grep SIGTERM\|SIGINT relay/server.js` → no matches. |
| No alerts (defect H2) | CONFIRMED: `grep -i ntfy\|webhook\|alert relay/server.js` → no matches. `relay/notify.js` does not exist yet (max-value lane will create it). |
| `package.json` test stub + no engines (defect H1) | CONFIRMED: `relay/package.json:7` = stock `echo "Error: no test specified" && exit 1`; no `engines`, `main` still `index.js`. |
| durable-state wiring | HOLDS: `server.js:11` require, `persistenceBlocked` gate :20-37. |
| muxd durable primitives | HOLD exactly: `_durable_commit_bytes` `muxd.py:92`, `durable_json_write` :144, `write_live_tabs` :2130. |
| `live_tabs_snapshot` / `sess_list` stale emission (defect A) | STILL LIVE: `live_tabs_snapshot` `muxd.py:2109-2128` and `sess_list` :2301-2302 iterate ALL of `sessions` with no custody/TTL filter. `session_payload` :2266-2288 (was ":2285 region") has `lifecycle`, `childPid` — no custody expiry fields. |
| Boot restore | DRIFTED + PARTIAL FIX: region now :3341-3444. NEW since base plan: `user_killed` and resolved stop-intent records ARE removed at boot (:3395-3413 `remove_record` → `sessions.pop` + manifest save). Remaining gap = no custody TTL: fresh/old dormant+failed records persist forever and are re-announced on every relay hello (`:3734` — `"protocol": PROTOCOL, "caps": CAPS, "sessions": sess_list()`). |
| Launch claims (defect B) | HOLDS: `CLAIM_ROOT` `muxd.py:900`, `acquire_launch_claim` :1122, lazy same-id expiry cleanup ~:1149-1151, no sweeper. |
| `RelayOutQueue` (defect F) | DRIFTED SEMANTICS, defect still live: class at `muxd.py:1211-1223`, one shared queue `maxsize=64`; `put_nowait` returns False when full → frame silently discarded (tail-drop + `dropped` counter, logged at :3727-3732). The base plan's "mid-stream trim at :1496-1499" no longer exists in that form — the defect is now silent tail-drop with no resync frame, shared across all sessions. Leaf L7 goal text updated accordingly. |
| `session_agent_status` regexes (defect D) | HOLD: `muxd.py:2231`, shell-prompt regex :2225-2228. |
| muxd tests | `muxd/tests/test_muxd_state.py` (66 KB) + 5 other test files exist; unittest-style. |
| relay tests | `relay/tests/{relay,wheel-scroll,client-layout}.test.js`. Sibling-lane correction adopted: `relay.test.js` does NOT hang — it takes ~72-78 s, tripping file-level timeouts < ~80 s. All verifiers below stay bounded anyway. |
| Gates runner | `app/tools/run_gates.ps1` confirmed. dotnet invocation :77-83 (`dotnet test native\CodexLocalRetrieval.Native.Tests\... --filter 'TestCategory!=RealStore&TestCategory!=LiveCodex' --blame-hang-timeout 3m`). **relay-tests gate (:93-99) is UNBOUNDED `node --test tests\relay.test.js` and runs against `C:\Users\Ahmed\multiplex-app-patch`, muxd gate against `C:\Users\Ahmed\muxd` (:7-8) — external copies, NOT the repo dirs.** |
| Deploy scripts | `scripts/deploy-relay.sh`, `scripts/deploy-muxd.ps1` (repo-root `scripts/`, NOT `relay/scripts/` as base plan L2/L10c wrote). |
| Glyph recovery (defect I) | DRIFTED lines, path intact: `terminalPaintLooksBlank` `index.html:797`, `recoverTerminalPaint` :818 (clearTextureAtlas :827/:830), `schedulePaintHeal` :842. Still only static string tests. |

### Base-plan assumptions now FALSE (post-merge)
1. **Defect G (relay lifecycle races) is substantially FIXED — L6 becomes a tests-only regression
   drill.** Evidence: create is no longer optimistic — `pendingHostCreates` map (`server.js:169`),
   `requestHostCreate` :226-263 resolves only on a validated `createResult` frame (:1832-1850, name
   identity-checked); host link loss fails all pendings (:1919-1921). Kill waits for muxd
   confirmation: `DELETE /api/sessions/:name` :699-710 (`waitForHostState(() => !hostSessions.has(name), 6000)`).
   `hello`/`sessions` frames are schema-validated and wholesale-replace `hostSessions`
   (:1780-1831), so a stale pre-kill frame cannot durably resurrect. Audit findings 13/14 are
   stale relative to code.
2. **Defect E (intent-journal bypass) is ~70% FIXED — L8 shrinks to a residue leaf.**
   `index.html:696` loads `intent-journal.js`; create/relaunch/rename(app-command path)/settabcolor/
   addtocollection/cleartabhistory/fetchfile all go through `postIntent` (:1864,:1957,:2028,:2069,
   :2100,:2126,:2169,:2173,:2308). Remaining RAW mutations: autoheal POST :1977, kill DELETE :1993
   and :2066 (kill-all loop), rename PATCH :2180, uploads keep PATCH :2321, uploads DELETE :2322,
   raw upload POST :2287. Note `postIntent` (`intent-journal.js:79-99`) is POST-only — DELETE/PATCH
   need a journal extension or intent-accepting server routes.
3. **"Relay protocol lacks version negotiation" (audit #21/#25F, base-plan deferred) is now
   PARTIALLY IMPLEMENTED.** `REQUIRED_HOST_PROTOCOL = 4` (`server.js:163`), exact-match check +
   `REQUIRED_HOST_CAPS` subset check (`hostProtocolOk` :265-270, `hostSupportsCap` :271-273); muxd
   announces `PROTOCOL = 4` and a 16-entry `CAPS` list (`muxd.py:906-907`). Consequence: any leaf
   that BUMPS the protocol number bricks the pairing until both sides deploy — additive fields +
   additive caps are the only campaign-safe mechanism (see adv-proto-registry and Open questions).

### Cold-correctness findings vs current tree
1. **fetchfile contract mismatch — FIXED client-side by the merge; keep only a regression
   assertion.** Current `index.html:2305-2319`: `insertUploadIntoPrompt` always sends
   `insert: insert||'path'` (:2309); `c.detail` is used ONLY as an opaque status/success token
   (:2298-2299, :2313-2314); no `pcPath` storage, no copy-as-path action remains (grep `pcPath` →
   0 hits; workspace actions :2274-2279 are Add-to-prompt/Add-as-image/keep/Delete). App side
   unchanged (`RemoteUploadTransfer.cs:88-90` returns "downloaded to PC" only when insert is
   empty) — which is now unreachable from the shipped client but is the contract to pin.
2. **uploadId path escape — STILL LIVE** (== audit 25H, `MUX_SYSTEM_AUDIT.md:519`).
   `RemoteUploadTransfer.cs:24` nonblank-check only; raw `Path.Combine(destRoot, uploadId)` :31;
   raw in scp remote path :43. `..\outside` escapes the upload root. Filename is sanitized
   (:142-151) but the ID is not.
3. **Intent-fenced without lease/intent — STILL LIVE, anchors drifted.** The app project moved:
   `MainPage.Remote.cs` lives in `app/native/CodexLocalRetrieval.Native/` (base brief said
   `CodexLocalRetrieval.App`). `RemoteCommandProtocol.IsReplaySafe` (`RemoteCommandProtocol.cs:19-36`)
   still checks only type/policy; pollers dispatch on it alone (`MainPage.Remote.cs:387`,
   `RemoteBridge.cs:180`); acks serialize possibly-empty `c.leaseToken` (`MainPage.Remote.cs:474`,
   `RemoteBridge.cs:236`); local-intent minting fallback now at `MainPage.Remote.cs:1091-1093`
   (was ~:895-908).
4. **HTML injection sink — STILL LIVE, drifted.** Sink is `setStatus` `index.html:1559`
   (`$('#statustext').innerHTML=text`, was :1466); `flash` :1999 forwards raw strings (was :1919)
   and its restore branch interpolates `esc(current)` into trusted markup; unescaped taint reaches
   it via collection name (:1951-1965 `'Added "'+s.name+'" to "'+name+'"'`), API/bridge error
   detail (:1968, :1987-1993 `apiError` composition), filenames (:2307). == audit 25A.
5. Terminal input dropped while reconnecting — native lane's; seam only (do not double-own).
6. **ws control frames unvalidated — STILL LIVE, drifted.** `onControl` `index.html:1254-1261`
   (was :1165-1170): any JSON after `'d'`; `m.cols`/`m.rows` go straight to `term.resize` :1259;
   `m.clients||[]` accepted without `Array.isArray` (later `(sizeClients||[]).filter` :1266 throws
   on a truthy non-array). == audit 25B.

### Other verified inputs
- App test project: `app/native/CodexLocalRetrieval.Native.Tests/` (MSTest) with existing
  `RemoteUploadTransferTests.cs`, `RemoteCommandProtocolTests.cs`, `RemoteApiTests.cs`,
  `SessionLaunchClaimTests.cs` — new app-side tests slot into these classes/files.
- `relay/tests/relay.test.js` spawns the server with a `MUX_STATE_DIR` sandbox (:97) and already
  has intent-dedup + lease tests (:612-705) — the harness patterns every relay leaf copies.
- MUX_SYSTEM_AUDIT findings 1–8B at `relay/MUX_SYSTEM_AUDIT.md:35-206`; deployment-constraint fix
  order at :939-975. Finding 1 is marked RESOLVED in-file; 2-8B remain.

## Leaves

```json
[
  {
    "id": "adv-proto-registry",
    "title": "Host-link protocol field/cap registry + conformance test",
    "kind": "build",
    "tier": "default",
    "goal": "Create docs/protocol-fields.md: a registry of every muxd<->relay ws frame type, field, and capability string, seeded from the current tree (muxd.py PROTOCOL=4/CAPS at :906-907, server.js REQUIRED_HOST_PROTOCOL/REQUIRED_HOST_CAPS at :163 and :265-270, hello/sessions/createResult/sb/o frame shapes). Registry rule stated in the doc: campaign leaves add FIELDS and CAPS additively and never bump the protocol number (relay requires exact protocol equality, so a bump bricks the pairing until both sides deploy). Add relay/tests/protocol-registry.test.js (node:test, static-read style of tests/client-layout.test.js) asserting: every cap literal in muxd.py CAPS and every entry in server.js REQUIRED_HOST_CAPS appears in the registry doc, and the registry's protocol number matches both sides.",
    "deps": [],
    "reads": ["muxd/muxd.py", "relay/server.js", "relay/tests/client-layout.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/protocol-registry.test.js",
    "size": "S",
    "notes": "muxd.py: read only :1-60 (frame doc header) and :900-910, :3520-3540, :3720-3740. server.js: :140-290, :1770-1860. This leaf OWNS docs/protocol-fields.md; adv-custody-janitor, adv-agent-truth-muxd, adv-output-fairness append entries to it."
  },
  {
    "id": "adv-custody-janitor",
    "title": "muxd custody TTL: dormant-record GC + stale-emission filter (defect A)",
    "kind": "build",
    "tier": "default",
    "goal": "Session manifest records in muxd get an explicit custody lifecycle: add lastAliveUtc + custodyExpiresUtc to session records (schema-additive; valid_session_records at muxd.py:2010 tolerates unknown keys), refresh on the status pump for alive sessions, and GC dormant/failed records whose custody expired (default TTL 24h, env MUX_CUSTODY_TTL_S) at boot (:3341-3444 region, after the existing user_killed removal at :3395-3413) and periodically. live_tabs_snapshot (:2109-2128) and sess_list (:2301-2302) stop emitting custody-expired records entirely (contract: filter, not label — the relay keeps showing unexpired dormant rows for relaunch exactly as today). Never GC a record holding a live launch claim or identityPending within grace. Extend muxd/tests/test_muxd_state.py: (a) boot over manifest {alive, fresh-dormant, expired-dormant, user_killed} retains exactly alive+fresh-dormant; (b) live_tabs_snapshot and sess_list contain no expired entries; (c) TTL refresh on simulated status pump; (d) claim-holder and identityPending records survive. Append the custody fields to docs/protocol-fields.md.",
    "deps": ["adv-proto-registry"],
    "reads": ["muxd/muxd.py", "muxd/tests/test_muxd_state.py", "docs/protocol-fields.md"],
    "verifier": "cd muxd && timeout 240 python -m unittest tests.test_muxd_state -v",
    "size": "L",
    "notes": "Centerpiece. muxd.py regions: :1983-2060 (manifest schema), :2109-2135, :2266-2302, :3341-3470 (boot+heal tick), status pump ~:3700-3740. Partial fix already landed: user_killed boot removal — do not re-implement. muxd deploys freely (campaign call 3)."
  },
  {
    "id": "adv-claim-sweeper",
    "title": "Launch-claim sweeper in muxd (defect B)",
    "kind": "build",
    "tier": "default",
    "goal": "Add sweep_launch_claims() to muxd: on boot and on a divided status-pump cadence (~60s), delete claim files under CLAIM_ROOT (muxd.py:900) whose self-described ExpiresUtc <= now, or whose OwnerProcess=='muxd' with a dead owner pid; quarantine unparseable claims by renaming to .claim.bad (never delete them); never touch a claim younger than its TTL even if the owner is unverifiable. Zero app-side edits (SessionLaunchClaims.cs belongs to the app lane; claim files are self-describing so the muxd sweeper covers the shared directory). Extend muxd/tests/test_muxd_state.py with claim-sweep tests: expired deleted; live-owner unexpired kept; dead-muxd-pid deleted; malformed quarantined; sweep idempotent.",
    "deps": [],
    "reads": ["muxd/muxd.py", "muxd/tests/test_muxd_state.py"],
    "verifier": "cd muxd && timeout 120 python -m unittest tests.test_muxd_state -k claim -v",
    "size": "S",
    "notes": "muxd.py regions :895-1160 (claims), status pump ~:3700-3740. Branch-per-leaf: no serialization dep on adv-custody-janitor (merge resolves)."
  },
  {
    "id": "adv-agent-truth-muxd",
    "title": "Process-truth agent state, muxd producer (defect D)",
    "kind": "build",
    "tier": "default",
    "goal": "muxd reports whether the agent process is actually alive: add agentTruth {procAlive, cpuActiveRecent, exe, checkedUtc} plus agentStateSource ('process'|'heuristic') to session_payload (muxd.py:2266-2288, which already carries childPid), fed by a bounded probe over the session's childPid descendants (reuse _pid_descends_from machinery at :1042). Executor discipline is mandatory (audit #6): probes on a small dedicated executor, results cached >=10s, per-probe timeout <=3s, never under the state lock; on stale/failed probe, agentStateSource degrades to 'heuristic' and the existing session_agent_status (:2231) output stands. Announce cap 'agentTruth' in CAPS (muxd.py:907) and append the fields to docs/protocol-fields.md. New muxd/tests/test_agent_truth.py: real dummy child (python -c 'import time; time.sleep(60)') under a fake session -> procAlive true; kill it -> flips false within one probe interval; fault-injected hung probe (env pattern like MUX_TEST_PERSIST_FAULT_FILE) leaves a status call responsive <1s and source 'heuristic'.",
    "deps": ["adv-proto-registry"],
    "reads": ["muxd/muxd.py", "muxd/tests/test_muxd_state.py", "docs/protocol-fields.md"],
    "verifier": "cd muxd && timeout 180 python -m unittest tests.test_agent_truth -v",
    "size": "M",
    "notes": "Do NOT bump PROTOCOL (relay requires ==4); additive field + additive cap only. Regex anchors: shell-prompt death regex :2225-2228."
  },
  {
    "id": "adv-agent-truth-relay",
    "title": "Process-truth agent state, relay consumer (defect D)",
    "kind": "build",
    "tier": "default",
    "goal": "Relay state dots and autoheal prefer host agentTruth when present: precedence = fresh host agentTruth > host heuristic > relay pane-regex (paneAgentState server.js:339, hostAgentStatus :385, attentionStatusForHosted :399); autoheal death-recovery for hosted sessions may fire only on procAlive===false or explicit host dormant, never on regex alone; absent field = exactly today's behavior (old muxd stays compatible via hostSupportsCap('agentTruth'), server.js:271). New relay/tests/agent-truth.test.js (relay.test.js MUX_STATE_DIR harness style): fake host ws pushes (a) procAlive:false + healthy-looking footer -> red/heal-eligible; (b) procAlive:true + footer matching death regex -> not red, no heal; (c) no agentTruth field -> legacy behavior preserved.",
    "deps": ["adv-agent-truth-muxd"],
    "reads": ["relay/server.js", "relay/tests/relay.test.js", "docs/protocol-fields.md"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/agent-truth.test.js",
    "size": "M",
    "notes": "server.js regions :330-470 (state fns), :1770-1860 (host frames), autoheal decision sites (grep autoheal/heal). relay.test.js :1-130 for the harness fixture."
  },
  {
    "id": "adv-output-fairness",
    "title": "muxd per-session output queues + explicit resync frame (defect F)",
    "kind": "build",
    "tier": "default",
    "goal": "One flooding session can no longer starve or gap other sessions' relay output: replace the single shared RelayOutQueue (muxd.py:1211-1223, maxsize 64, silent tail-drop with a dropped counter logged at :3727-3732) with per-session byte-bounded queues (~1MiB each) drained round-robin into the relay ws; on per-session overflow, drop only that session's backlog to a boundary and enqueue a {t:'resync', s} frame. Add the <=15-line relay-side handler for t:'resync' that triggers the existing CLEAR+scrollback replay (requestSessionScrollback, server.js:2139) for that session's viewers, and register frame+cap ('resync') in docs/protocol-fields.md and muxd CAPS. New muxd/tests/test_output_fairness.py: session A floods 10MiB while B emits 100 numbered lines -> all 100 B frames egress in order; force A over budget -> exactly one resync for A, none for B; round-robin interleaving asserted. New relay/tests/resync-handler.test.js: fake host sends resync -> relay issues an sb request / CLEAR+replay to that session's viewers only.",
    "deps": ["adv-proto-registry"],
    "reads": ["muxd/muxd.py", "relay/server.js", "docs/protocol-fields.md", "relay/tests/relay.test.js"],
    "verifier": "cd muxd && timeout 240 python -m unittest tests.test_output_fairness -v && cd ../relay && node --test --test-force-exit --test-timeout=20000 tests/resync-handler.test.js",
    "size": "L",
    "notes": "Base-plan drift folded in: the old mid-stream trim (:1496-1499) is gone; current defect is silent tail-drop on the shared queue. muxd regions :1205-1230, relay egress ~:3700-3740. Single leaf owns the frame end-to-end (both sides) per base-plan seam S1 note."
  },
  {
    "id": "adv-burst-drill",
    "title": "Burst/backpressure regression drill (defect lock-in, tests only)",
    "kind": "build",
    "tier": "default",
    "goal": "Lock in the existing viewer-backpressure fix as a reproduced-failure suite; NO server.js changes (if a live defect surfaces, report it upward as a new leaf instead of patching in place). New relay/tests/burst-backpressure.test.js using the relay.test.js MUX_STATE_DIR + fake /host ws harness: (a) flood one session >=16MiB with a never-reading slow viewer attached -> slow viewer is TERMINATED (not gapped), fast viewer receives a byte-complete stream (sendViewer server.js:2116-2132); (b) burst arriving while scrollback is pending -> queue-flush path emits CLEAR + full replay in order (:1867-1899); (c) replay burst just under/over VIEWER_REPLAY_BURST_BYTES (:2050) -> boundary behavior asserted.",
    "deps": [],
    "reads": ["relay/server.js", "relay/tests/relay.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/burst-backpressure.test.js",
    "size": "M",
    "notes": "server.js regions :1860-1910, :2040-2160. Keep the suite <60s wall."
  },
  {
    "id": "adv-drain",
    "title": "SIGTERM deploy drain + client restarting toast (defect C)",
    "kind": "build",
    "tier": "default",
    "goal": "Deploys become invisible: server.js (which today has NO signal handler) gains a SIGTERM path that stops accepting upgrades, closes viewer sockets with code 1012 reason 'restarting' (host link closed last), flushes durable state, and hard-exits within 5s (setTimeout(...,5000).unref()). Client index.html maps close code 1012 in its onclose/closeRefusal path (closeRefusal at :1614, used at :1680) to an immediate 500ms-backoff reconnect plus a 'relay restarting...' toast via existing flash() (:1999). scripts/deploy-relay.sh restarts SIGTERM-first. New relay/tests/drain.test.js: spawn server as a child process with MUX_STATE_DIR sandbox, attach viewer + fake host ws, send SIGTERM -> (a) viewer gets close 1012 within 2s, (b) process exits 0 within 5s, (c) post-SIGTERM upgrade attempt refused; plus one static-source assertion (client-layout.test.js style) that index.html handles 1012.",
    "deps": [],
    "reads": ["relay/server.js", "relay/public/index.html", "scripts/deploy-relay.sh", "relay/tests/relay.test.js", "relay/tests/client-layout.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/drain.test.js",
    "size": "M",
    "notes": "Base-plan drift: deploy script is scripts/deploy-relay.sh at repo root, not relay/scripts/. index.html regions :1600-1700 (ws lifecycle), :1990-2005 (flash)."
  },
  {
    "id": "adv-lifecycle-drill",
    "title": "Relay lifecycle regression drill: create-confirm + kill-confirm (defect G, now fixed — lock it)",
    "kind": "build",
    "tier": "default",
    "goal": "Defect G (create-timeout phantoms, kill/status resurrection) was fixed by the integration merge: creates resolve only on validated createResult via pendingHostCreates (server.js:169, :226-263, :1832-1850) and DELETE /api/sessions waits for muxd removal confirmation (:699-710). Turn that into a permanent tests-only drill in relay/tests/lifecycle-confirm.test.js: (a) POST /api/sessions with a host that never sends createResult -> request fails after timeout AND /api/sessions lists no phantom row; (b) kill confirmed by host removal -> row gone; replaying a pre-kill 'sessions' frame naming the dead session then a corrected frame -> steady state stays dead; (c) createResult with mismatched name identity is rejected (:1837-1841); (d) host-link drop fails all pending creates (:1919-1921). NO production changes expected.",
    "deps": [],
    "reads": ["relay/server.js", "relay/tests/relay.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/lifecycle-confirm.test.js",
    "size": "S",
    "notes": "Downgraded from base-plan L6 (fix) to drill: audit findings 13/14 are stale vs code. If the drill finds a residual race, file it upward as a new fix leaf."
  },
  {
    "id": "adv-intent-residue",
    "title": "Intent-journal coverage residue: kill/autoheal/rename/uploads (defect E remainder)",
    "kind": "build",
    "tier": "default",
    "goal": "The merge already routed create/relaunch/rename-via-app-command/settabcolor/addtocollection/fetchfile through postIntent (index.html:696 loads intent-journal.js). Close the remainder: autoheal POST (index.html:1977), kill DELETE (:1993, :2066), session rename PATCH (:2180), uploads keep PATCH (:2321) and DELETE (:2322). Extend relay/public/intent-journal.js with a method-aware sendIntent(method,url,payload,prefix) (POST-only today, :79-99), and make the relay routes for these mutations accept+dedupe an intentId (kill/autoheal/rename in server.js :650-729; uploads :1704-1729) following the existing app-commands dedup pattern (relay.test.js:612-705). Keep UX (flash/confirm) unchanged; read-only GETs stay raw. New relay/tests/intent-coverage.test.js: (a) static-source allowlist assertion that index.html contains no raw mutating fetch() outside {raw byte upload POST :2287}; (b) integration: same kill/autoheal/rename/upload-delete replayed with one intentId -> exactly one execution.",
    "deps": [],
    "reads": ["relay/public/index.html", "relay/public/intent-journal.js", "relay/server.js", "relay/tests/relay.test.js", "relay/tests/client-layout.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/intent-coverage.test.js",
    "size": "M",
    "notes": "Much smaller than base-plan L8. Raw upload POST stays raw (byte body; re-upload is idempotent-enough) but gets an allowlist entry + comment."
  },
  {
    "id": "adv-glyph-harness",
    "title": "Glyph-blank recovery browser harness (defect I)",
    "kind": "build",
    "tier": "default",
    "goal": "Give the Chromium glyph-blank recovery path (terminalPaintLooksBlank index.html:797, recoverTerminalPaint :818 with clearTextureAtlas, schedulePaintHeal :842) a real-browser test so layout refactors cannot silently break it. New relay/tests/browser/glyph-blank.spec.js (Playwright, headless Chromium) + tiny static-serve/stub-ws fixture: fill the terminal via stub ws, toggle the display:none layout paths (mobile sessions view / projects overlay classes), toggle back, then assert within 2s that the page's own terminalPaintLooksBlank() returns false AND direct canvas pixel readback is non-blank; also assert repeated toggles do not wedge the recovery throttle. Expose the needed hooks under a window.__muxTest shim only. The test asserts the RECOVERY INVARIANT (post-relayout repaint is non-blank), which regresses if the heal path breaks regardless of whether the upstream Chromium bug fires headlessly. Add devDependency playwright + npm run test:browser in relay/package.json — kept OUT of npm test so the deploy gate stays dependency-light.",
    "deps": [],
    "reads": ["relay/public/index.html", "relay/package.json", "relay/tests/client-layout.test.js"],
    "verifier": "cd relay && npx playwright test tests/browser/glyph-blank.spec.js --reporter=line --timeout=30000",
    "size": "L",
    "notes": "index.html regions :750-980 (detector/recovery), CSS display toggles (search 'display:none' + mobile-view classes; base-plan :452/:215 anchors drifted — re-locate). package.json scripts seam with adv-npm-gate: additive keys only."
  },
  {
    "id": "adv-npm-gate",
    "title": "relay npm test = real bounded deploy gate + engines pin (defect H1)",
    "kind": "build",
    "tier": "default",
    "goal": "Make relay/package.json's scripts.test a real bounded gate: node --test --test-force-exit --test-timeout=20000 over tests/*.test.js (excluding tests/browser/), replacing the stock error stub (:7); add engines {node: '>=22'} and fix main. Fix or document the non-terminating teardown in tests/relay.test.js (orphaned child server.js processes) — if a clean fix is out of reach in one sitting, keep --test-force-exit and write the why into package.json comments/README section. Wire scripts/deploy-relay.sh to run npm test before restart and abort on failure. NOTE: the file-level suite takes ~72-78s (relay.test.js alone), so any wrapper timeout must be >=180s.",
    "deps": ["adv-burst-drill", "adv-drain", "adv-lifecycle-drill", "adv-agent-truth-relay", "adv-intent-residue", "adv-alerts"],
    "reads": ["relay/package.json", "relay/tests/relay.test.js", "scripts/deploy-relay.sh"],
    "verifier": "cd relay && timeout 300 npm test && bash -c '! pgrep -f \"node.*relay.server.js\"'",
    "size": "S",
    "notes": "Runs LAST among relay leaves (gates them). Does NOT touch app/tools/run_gates.ps1 — max-value lane owns widening the run_gates relay-tests gate (currently UNBOUNDED node --test tests\\relay.test.js against an external copy). Deps here are semantic-ordering (gate must enclose the finished suites)."
  },
  {
    "id": "adv-alerts",
    "title": "Ops alerts: push on degraded-health edge transitions (defect H2)",
    "kind": "build",
    "tier": "default",
    "goal": "The relay currently has zero alerting (no ntfy/webhook code). Add a small relay/health-alerts.js module watching the existing degraded computation (server.js :1170-1190 region), persistenceBlocked, host-link-down duration, and gave-up heals; fire on EDGE transitions only (healthy->degraded sustained >2min; host link down >5min; heal gave-up; persistenceBlocked), one push per condition per 30min dedupe window, plus a recovery push. Transport: use the shared ntfy transport module relay/notify.js OWNED BY THE MAX-VALUE LANE — inject it (constructor/function param) so tests use a mock; do not rebuild transport, secrets stay in /etc/multiplex-app.env (document, never commit). New relay/tests/alerts.test.js: drive synthetic health states with a mock transport; assert fire-on-edge, dedupe window, recovery notice, and NO fire on a 30s transient blip.",
    "deps": ["EXT:maxval-notify"],
    "reads": ["relay/server.js", "relay/notify.js", "relay/tests/relay.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/alerts.test.js",
    "size": "M",
    "notes": "EXT:maxval-notify = the max-value lane's relay/notify.js leaf (id per that lane's deliverable) — orchestrator must bind this dep at reconcile. If notify.js's interface is not yet merged when this leaf starts, build against a one-function contract post(topic,title,body)->Promise and record any mismatch upward."
  },
  {
    "id": "adv-backup-state",
    "title": "Off-box relay state backups (defect H3)",
    "kind": "build",
    "tier": "default",
    "goal": "Relay durable state (projects.json, app-commands.json, pins.json, uploads-meta.json, rename-intents.json and .bak siblings under the relay state dir) survives VPS loss. New scripts/backup-state.sh (repo-root scripts/, beside deploy-relay.sh): tar the state files and scp to the PC over the existing 'win' ssh route; supports --dry-run-to <dir> (local archive, no scp) and --verify (restore-roundtrip against the local archive). Committed systemd-timer unit under relay/ops/. Installation on the VPS is an ops step for the deploy owner — this leaf delivers script + unit + test.",
    "deps": [],
    "reads": ["scripts/deploy-relay.sh", "relay/server.js", "relay/durable-state.js"],
    "verifier": "cd relay && bash ../scripts/backup-state.sh --dry-run-to /tmp/mux-backup-test --state-dir tests/fixtures/backup-state && tar -tzf /tmp/mux-backup-test/*.tgz | grep -q pins.json",
    "size": "S",
    "notes": "Ship a tiny fixture state dir under relay/tests/fixtures/backup-state so the verifier is hermetic. server.js read only for STATE_DIR resolution (~:734 PROJECTS_FILE region)."
  },
  {
    "id": "adv-uploadid-containment",
    "title": "uploadId token grammar + resolved-path containment (cold finding 2 / audit 25H)",
    "kind": "build",
    "tier": "default",
    "goal": "RemoteUploadTransfer.FetchAndInsertAsync (app/native/CodexLocalRetrieval.Core/Remote/RemoteUploadTransfer.cs) accepts any nonblank uploadId (:24) and uses it raw in Path.Combine(destRoot, uploadId) (:31) and in the scp remote path (:43), so '..\\outside' escapes the upload root. Enforce a strict token grammar (e.g. ^[A-Za-z0-9._-]{1,64}$, no leading dot) AND a resolved-path containment check (Path.GetFullPath(destDir) must be strictly under Path.GetFullPath(destRoot)) before either use; refuse with a clear Detail string otherwise. Extend the existing app/native/CodexLocalRetrieval.Native.Tests/RemoteUploadTransferTests.cs: traversal IDs ('..', '..\\x', absolute, drive-qualified, UNC, 260-char, empty-after-trim) are refused before any filesystem/scp side effect; a plain token still resolves inside the root.",
    "deps": [],
    "reads": ["app/native/CodexLocalRetrieval.Core/Remote/RemoteUploadTransfer.cs", "app/native/CodexLocalRetrieval.Native.Tests/RemoteUploadTransferTests.cs"],
    "verifier": "cd app/native && dotnet test CodexLocalRetrieval.Native.Tests/CodexLocalRetrieval.Native.Tests.csproj -c Release --filter \"FullyQualifiedName~RemoteUploadTransferTests\" --blame-hang --blame-hang-timeout 3m",
    "size": "S",
    "notes": "Server side already constrains the relay-visible id, but the bridge must not trust it (defense at the executing end). Mirror the relay-side upload id grammar if one exists (check server.js /api/upload id minting)."
  },
  {
    "id": "adv-intent-fence",
    "title": "Enforce lease+intent envelope for intent-fenced bridge commands (cold finding 3)",
    "kind": "build",
    "tier": "default",
    "goal": "Intent-fenced commands (fetchfile, startmux) currently execute after only a type/replayPolicy check: RemoteCommandProtocol.IsReplaySafe (app/native/CodexLocalRetrieval.Core/Remote/RemoteCommandProtocol.cs:19-36) is the sole gate in both pollers (app/native/CodexLocalRetrieval.Native/MainPage.Remote.cs:387; app/native/CodexLocalRetrieval.Core/Remote/RemoteBridge.cs:180), deserialized leaseToken/intentId default to empty, acks echo the possibly-empty leaseToken (MainPage.Remote.cs:474; RemoteBridge.cs:236), and a missing command intent is replaced by a freshly minted local intent (MainPage.Remote.cs:1091-1093) which breaks redelivery dedup. Add a protocol-level envelope check (e.g. RemoteCommandProtocol.RequiresIntentEnvelope(type) + ValidateEnvelope(intentId, leaseToken)): for intent-fenced types, a nonblank well-formed leaseToken AND a stable nonblank intentId are required BEFORE any side effect in BOTH pollers; violations ack as failed without executing; the local-intent-minting fallback applies only to genuinely GUI-originated starts, never to polled remote commands. Add tests to RemoteCommandProtocolTests.cs plus poller-level tests (RemoteApiTests.cs style): missing/blank lease or intent -> refused, no side effect, failure ack; valid envelope -> executes once; same intentId redelivered -> deduped.",
    "deps": [],
    "reads": ["app/native/CodexLocalRetrieval.Core/Remote/RemoteCommandProtocol.cs", "app/native/CodexLocalRetrieval.Native/MainPage.Remote.cs", "app/native/CodexLocalRetrieval.Core/Remote/RemoteBridge.cs", "app/native/CodexLocalRetrieval.Native.Tests/RemoteCommandProtocolTests.cs", "app/native/CodexLocalRetrieval.Native.Tests/RemoteApiTests.cs"],
    "verifier": "cd app/native && dotnet test CodexLocalRetrieval.Native.Tests/CodexLocalRetrieval.Native.Tests.csproj -c Release --filter \"FullyQualifiedName~RemoteCommandProtocol|FullyQualifiedName~RemoteApi\" --blame-hang --blame-hang-timeout 3m",
    "size": "M",
    "notes": "MainPage.Remote.cs is large — poller region ~:340-480, start path ~:1060-1100. Also add one regression assertion pinning cold finding 1's contract: an insert-mode-less fetchfile returns the status Detail WITHOUT prompt insertion (RemoteUploadTransfer.cs:88-90) — client-side half lives in adv-client-frame-guard."
  },
  {
    "id": "adv-client-frame-guard",
    "title": "Validate ws control frames + fetchfile-contract regression (cold findings 6 + 1)",
    "kind": "build",
    "tier": "default",
    "goal": "index.html's onControl (relay/public/index.html:1254-1261) accepts any JSON after the 'd' prefix and feeds m.cols/m.rows straight into term.resize while m.clients is used without an array check ((sizeClients||[]).filter at :1266 throws on truthy non-arrays). Add shape/range validation before mutating terminal state: cols/rows must be finite integers within sane bounds (e.g. 2..1000 cols, 2..500 rows) else the frame is ignored; clients must pass Array.isArray else []; mode/pinLabel coerced to expected primitive types. Extend the static client suite (relay/tests/client-layout.test.js style, new file tests/frame-guard.test.js — vm-extract the onControl function and drive it with malformed frames): non-numeric/huge/NaN dims never reach term.resize; non-array clients never throws. Include the fetchfile regression lock (cold finding 1 is FIXED client-side): static assertions that the fetchfile postIntent payload always carries an insert mode (:2309) and that pollUploadCmd's detail is used only as opaque status, never inserted into the terminal or used as a path.",
    "deps": [],
    "reads": ["relay/public/index.html", "relay/tests/client-layout.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/frame-guard.test.js",
    "size": "M",
    "notes": "== audit 25B (client half). Server-side 'd' frames are relay-generated, so this is defense against version skew and a compromised/buggy relay, not the primary trust boundary."
  },
  {
    "id": "adv-status-sink-escape",
    "title": "Escape the status/flash HTML sink (cold finding 4 / audit 25A)",
    "kind": "build",
    "tier": "default",
    "goal": "setStatus (relay/public/index.html:1559) writes its text argument via innerHTML, and flash (:1999) forwards arbitrary strings into it; unescaped taint reaches the sink via collection names (:1951-1968), API/bridge error details (:1968, :1987-1993), and filenames (:2307). Split the API: setStatus becomes text-only (textContent), and the small number of trusted-markup call sites (e.g. flash's live-status restore template with <span class=\"name\">, which already escapes via esc()) move to an explicit setStatusHtml used ONLY with literal templates whose dynamic parts are esc()-wrapped. Audit every setStatus/flash caller so no user/bridge-derived string reaches innerHTML unescaped. New relay/tests/status-sink.test.js (static + vm-extract): flash('<img src=x onerror=1>') produces no element markup in the status node; every setStatusHtml call site in the source matches an allowlist regex proving esc()-wrapping of interpolated values.",
    "deps": [],
    "reads": ["relay/public/index.html", "relay/tests/client-layout.test.js"],
    "verifier": "cd relay && node --test --test-force-exit --test-timeout=20000 tests/status-sink.test.js",
    "size": "M",
    "notes": "Other innerHTML sites (:1771, :1814, :1853, :2086, :2416) already esc() their dynamic parts — verify-in-passing, but this leaf's scope is the status/flash sink; a full innerHTML sweep belongs to the audit plan node if wanted."
  },
  {
    "id": "adv-secfix-plan",
    "title": "Plan node: decompose the MUX_SYSTEM_AUDIT security fix-order (findings 2-8B)",
    "kind": "plan",
    "tier": "deep",
    "goal": "Decompose the deferred security workstream into build leaves. Source of truth: relay/MUX_SYSTEM_AUDIT.md, High findings at lines 35-206 — finding 1 (ws Origin) is marked RESOLVED in-file; still open: 2 (relay grants owner-equivalent access to any same-host process), 3 (muxd loopback control permits unauthenticated command execution), 4 (hosted sandbox group can read the relay host credential / muxd.env ACL), 5 (wedged-but-alive muxd left unrecovered), 6 (muxd default-executor starvation), 7 (logoff destroys muxd), 8 (relay-to-muxd LAN link plaintext + token-in-URL), 8A/8B (incompatible command protocols / relay queue treated as PC action authority without end-to-end provenance). The file's own 'Suggested fix order by deployment constraint' section (lines 939-975: safe-anytime / needs-VPS-firewall-or-nginx / needs-relay-restart / needs-muxd-restart) is the deploy-coordination constraint every emitted leaf must carry: muxd restarts are free (campaign call 3), relay restarts should ride the adv-drain leaf once it lands, firewall/nginx changes need explicit operator sign-off collected in the end-of-campaign human checklist. Emit leaves in this prong's schema with bounded executable verifiers; anything requiring live-VPS mutation becomes a scripted, reversible ops leaf with a dry-run verifier.",
    "deps": ["adv-drain"],
    "reads": ["relay/MUX_SYSTEM_AUDIT.md", "relay/server.js", "muxd/muxd.py", "scripts/deploy-relay.sh", "scripts/deploy-muxd.ps1"],
    "verifier": "n/a (plan node - output is a leaf set in this schema)",
    "size": "M",
    "notes": "Kept kind:plan per brief Task 3. dep on adv-drain is soft-semantic: the fix-order's relay-restart tier should ship behind a drain-capable relay so security deploys stop dropping viewers."
  }
]
```

## Ordering

Branch-per-leaf; every leaf on its own worktree/branch; deps above are semantic-only (the base
plan's muxd.py L3→L4→L7→L5a same-file chain is dropped per campaign call 1).

```
Wave-free graph (arrows = semantic deps):

adv-proto-registry ──┬─> adv-custody-janitor
                     ├─> adv-agent-truth-muxd ──> adv-agent-truth-relay ─┐
                     └─> adv-output-fairness                             │
                                                                         ├─> adv-npm-gate (LAST)
adv-burst-drill ─────────────────────────────────────────────────────────┤
adv-drain ───────────────────────────────┬───────────────────────────────┤
adv-lifecycle-drill ─────────────────────┼───────────────────────────────┤
adv-intent-residue ──────────────────────┼───────────────────────────────┤
EXT:maxval-notify ──> adv-alerts ────────┼───────────────────────────────┘
                                         └─> adv-secfix-plan (emits second-cut leaves)

Fully independent, start anytime: adv-claim-sweeper, adv-backup-state, adv-glyph-harness,
adv-uploadid-containment, adv-intent-fence, adv-client-frame-guard, adv-status-sink-escape,
adv-burst-drill, adv-drain, adv-lifecycle-drill, adv-intent-residue.
```

**Recommended first cut (value × unblock-fanout):**
1. `adv-proto-registry` (S — unblocks 3 leaves; do it first)
2. `adv-custody-janitor` (the centerpiece; muxd deploys freely)
3. `adv-drain` (every later relay deploy stops hurting; soft-gates the security plan node)
4. `adv-uploadid-containment` + `adv-intent-fence` (HIGH correctness, app-side, fully parallel)
5. `adv-claim-sweeper`, `adv-backup-state` (small independents)
6. Then the rest in parallel; `adv-npm-gate` strictly last among relay leaves;
   `adv-secfix-plan` may run as soon as adv-drain merges.

Value/effort ranking: custody-janitor > drain > proto-registry > intent-fence >
uploadid-containment > claim-sweeper > burst-drill > agent-truth-muxd > intent-residue >
output-fairness > npm-gate > status-sink-escape > client-frame-guard > agent-truth-relay >
alerts > lifecycle-drill > glyph-harness > backup-state.

## Seams

| Seam | Files/contract | Owner proposed | Notes |
|---|---|---|---|
| ntfy transport | `relay/notify.js` | **max-value lane** (already claimed) | `adv-alerts` DEPENDS on it (`EXT:maxval-notify`), injects it, never rebuilds it. Orchestrator binds the concrete leaf id at reconcile. |
| Gates runner | `app/tools/run_gates.ps1` | **max-value lane** (this lane cedes) | Their widening must bound the relay-tests gate (`node --test tests\relay.test.js` is unbounded today and the suite runs 72-78 s) and decide the external-copy question (Open q. 1). `adv-npm-gate` touches only `relay/package.json` + `scripts/deploy-relay.sh`. |
| Host-link protocol fields | `docs/protocol-fields.md` (new), `muxd.py` CAPS :907, `server.js` REQUIRED_HOST_CAPS | **this lane** (`adv-proto-registry`) | Registry rule: additive fields + additive caps, protocol number frozen at 4 for the campaign. Native lane's terminal-model sidecar must register any frame/field it adds here. |
| `relay/public/index.html` | drain close-code map, intent residue, frame guard, status sink, glyph shim | shared under branch-per-leaf | 5 leaves touch it (adv-drain, adv-intent-residue, adv-client-frame-guard, adv-status-sink-escape, adv-glyph-harness) + value/native lanes. All edits are small and regional; merge-conflict resolution at merge per campaign call 1. Reconcile should name ONE merge sequencer for index.html across prongs. |
| Launch-claims dir semantics | `%LOCALAPPDATA%\CodexLocalRetrieval\launch-claims\*`, `SessionLaunchClaims.cs` | app-side = integration/app lane; muxd-side sweeper = **this lane** (`adv-claim-sweeper`) | Sweeper is additive + fail-safe over the shared dir; zero app-side edits. |
| Cold finding 5 (input dropped while reconnecting) | `index.html` input path ~:1444-1462 | **native lane** (per brief) | Not owned here; adv-client-frame-guard must not touch the input queue path. |
| muxd executor/lock discipline | `muxd.py` probe executors | win32-perf prong (if it exists) / else this lane's L5a discipline only | `adv-agent-truth-muxd` applies executor discipline to its OWN probes only; the systemic refactor (audit #6/#17) is not claimed here. |
| Security fix-order output | leaves emitted by `adv-secfix-plan` | **this lane** plans; orch assigns builders | Deploy-coordination tiers from MUX_SYSTEM_AUDIT.md:939-975 ride along on every emitted leaf. |

## Open questions

1. **Gates test external copies, not the repo.** `app/tools/run_gates.ps1:7-8` points relay-tests at
   `C:\Users\Ahmed\multiplex-app-patch` and muxd-tests at `C:\Users\Ahmed\muxd`. Campaign leaves
   merge into THIS repo — the gate as written never sees their tests until someone syncs the
   external copies. Who owns that sync (deploy scripts? max-value's run_gates widening?), and should
   the gate be repointed at the repo dirs?
2. **Custody emission contract (adv-custody-janitor):** I pinned "filter expired records out of
   live-tabs/sess_list entirely" (no relay/app change needed). Alternative — emit them with a
   `custody:'expired'` label so the web can offer relaunch-from-history — needs a relay+app consumer
   and a UX decision. Confirm the filter-only contract or reassign the labeled variant.
3. **Protocol freeze ratification:** I set the campaign rule "protocol number stays 4, additive
   caps/fields only" (relay requires exact equality, `server.js:266`). The native lane's
   terminal-model sidecar is the likeliest to want a bump — the head should ratify the freeze or
   define a coordinated-bump procedure now.
4. **`EXT:maxval-notify` binding:** the alerts leaf depends on the max-value lane's `relay/notify.js`
   leaf whose id I cannot know; the orchestrator must bind it (and its interface —
   assumed `post(topic,title,body) -> Promise`) at reconcile.
5. **Stale audit entries:** MUX_SYSTEM_AUDIT findings 13/14 (and arguably 21) are fixed/partially
   fixed in code but not annotated in the file. May adv-lifecycle-drill append "RESOLVED IN CURRENT
   WORKTREE" annotations, or is the audit file frozen as a historical document?
6. **Playwright dependency:** adv-glyph-harness adds the campaign's first browser-test dependency to
   `relay/package.json` (devDependency, separate `test:browser` script, excluded from the deploy
   gate). Confirm that is acceptable for the VPS deploy flow (npm ci size) or whether the harness
   should live outside `relay/` entirely.
