# Multiplex System Audit

Date: 2026-07-15

Working-tree note: while this restarted audit was running, `server.js` and
`package.json` acquired uncommitted fixes for the WebSocket Origin check,
bounded auth cache, bridge-only `/api/projects`, and the package test/main
entries. Those baseline findings remain documented because they were verified
in the handed-off revision, but their headings below identify the current
working-tree status. No audit process in this turn edited those production
files.

## System map

The browser client (`multiplex-app/public/index.html`) connects through nginx at
`/multiplex/` to the Node relay (`multiplex-app/server.js`, port 7682). The relay
authenticates the owner session, serves the project/session projection, upgrades
browser WebSockets, and maintains a second WebSocket link to the Windows muxd
host. muxd (`C:\Users\Ahmed\muxd\muxd.py`) owns PTYs and durable session state and
exposes a separate loopback control listener on port 7699. The desktop bridge
(`ArchiveService.cs`) periodically projects local Codex data into
`POST /api/projects` and polls relay-side application commands. The important
trust boundaries are therefore browser-to-relay, nginx-to-relay, relay-to-muxd,
local-process-to-muxd, and desktop-bridge-to-relay.

## Confirmed findings

Findings are ordered by severity. The relay and muxd findings in this first pass
were supplied from earlier code-verified audit lanes and retained here with
their evidence. The web-client and desktop-bridge sections are appended below
after the restarted audit.

### High

#### 1. Browser WebSocket accepts cross-site connections without an Origin check [HIGH, RESOLVED IN CURRENT WORKTREE]

- **Evidence:** `multiplex-app/server.js:1130-1138` upgrades `/ws` without
  validating `req.headers.origin`; authorization occurs only when the WSS
  connection is handled around `server.js:1330`, using the automatically sent
  `hl_session` cookie or loopback trust.
- **Why it matters:** WebSocket handshakes are not protected by CORS. A hostile
  page open in the owner's browser can initiate a credentialed WebSocket to the
  Multiplex origin and, given a live session name, gain bidirectional terminal
  access.
- **Fix:** Reject browser upgrades unless `Origin` exactly matches an explicit
  HTTPS origin allowlist. Treat absent `Origin` as non-browser traffic and
  require a separate authenticated protocol for it. Add negative tests for
  foreign, `null`, malformed, and absent origins.
- **Current status:** The uncommitted `server.js` diff now adds
  `ALLOWED_WS_ORIGINS`, `wsOriginOk`, and rejects the `/ws` upgrade before
  `handleUpgrade`. This needs tests and deployment verification.

#### 2. Relay grants owner-equivalent access to any same-host process [HIGH]

- **Evidence:** `multiplex-app/server.js:51-57` defines `isTrustedLocal` as a
  loopback peer with no `X-Forwarded-For`. The relay listener is confirmed as
  `LISTEN 0.0.0.0:7682`, while `/api/projects`, the app-command queue,
  `POST /api/running`, and `/ws` around `server.js:1330` honor this bypass.
  nginx overwrites `X-Forwarded-For` with `$remote_addr`, which prevents a public
  client from spoofing the bypass through nginx but does not protect direct
  loopback access.
- **Why it matters:** Any local/co-tenant process, including a hosted sandbox
  that can reach `127.0.0.1:7682`, can exercise owner control without the owner
  cookie.
- **Fix:** Bind the relay to `127.0.0.1` only and replace ambient loopback trust
  with a capability: a shared secret or mutually authenticated local transport
  used only by the desktop bridge and nginx-facing deployment path. Authorize
  each route by role rather than by source address.

#### 3. muxd loopback control permits unauthenticated command execution [HIGH]

- **Evidence:** `C:\Users\Ahmed\muxd\muxd.py:3438-3460` implements
  `local_serve` as “loopback-only, no token” and only rejects browser `Origin`.
  A local `create` request reaches `coordinate_session_request` at
  `muxd.py:2897` and the PowerShell `-EncodedCommand` spawn path at
  `muxd.py:1416`; the same protocol also exposes input, kill, owner, and bind
  operations.
- **Why it matters:** Any local process, including an agent sandbox hosted by
  muxd, can create arbitrary processes in muxd's user context or inject input
  into existing sessions.
- **Fix:** Authenticate every local-control connection with a high-entropy token
  stored under an Ahmed-only ACL, or replace TCP with a named pipe whose ACL
  grants only Ahmed and SYSTEM. Keep the browser-Origin rejection as
  defense-in-depth, not authentication.

#### 4. Hosted sandbox group can read the relay host credential [HIGH]

- **Evidence:** `icacls C:\Users\Ahmed\muxd\muxd.env` reports
  `CrackerBarrel\CodexSandboxUsers:(I)(RX)`. The file contains
  `MUX_HOST_TOKEN`, `RELAY_LAN`, and `RELAY_PUBLIC`.
- **Why it matters:** A hosted sandboxed agent can recover the host token and
  impersonate muxd to the relay or use the disclosed relay endpoints.
- **Fix:** Remove inherited access and set an explicit ACL granting only
  `Ahmed:F` and `SYSTEM:F`. Rotate `MUX_HOST_TOKEN` after the ACL change and
  verify effective access from a sandbox identity.

#### 5. A wedged-but-alive muxd is deliberately left unrecovered [HIGH]

- **Evidence:** `muxd-watchdog.log` recorded on 2026-07-15 at 03:22:36:
  `running but local control is unresponsive; preserving process custody`.
  In-loop auto-restart is disabled at `muxd.py:1907-1908`, and the external
  watchdog restarts only when the process is absent.
- **Why it matters:** The observed 30-50 second control stalls freeze every web
  terminal while both recovery layers intentionally preserve the unresponsive
  process.
- **Fix:** Add bounded escalation: after repeated failed local-control probes and
  a stale main-loop tick, verify that no active PTYs require custody, capture
  diagnostics, then restart. If active PTYs exist, restart only the affected
  control/link subsystem or surface a loud degraded state with an operator
  action.

#### 6. muxd default executor starvation can freeze all control operations [HIGH]

- **Evidence:** `try_live_session_ids`, `_agent_cmdlines`
  (`muxd.py:680-683`, 15-second timeout), and `_pid_descends_from`
  (`muxd.py:1046-1061`, per-PID CIM walk with a 15-second timeout) share the
  default `ThreadPoolExecutor` with spawn, terminate, and
  `durable_json_write`. Every `@state_mutation` awaits
  `manifest_save_async`, which holds the global `state_lock` across its executor
  write at `muxd.py:2372-2378`.
- **Why it matters:** A burst of slow WMI/CIM work can occupy every worker.
  State mutations then wait for disk work while holding the global lock, so TCP
  and output flushing can remain alive while create/kill/input/control appears
  frozen. This matches the observed “alive but unresponsive” failure mode.
- **Fix:** Use a small dedicated executor for WMI/subprocess probes, cache
  liveness briefly, reduce CIM timeouts, bound concurrent probes, and snapshot
  state under the lock before releasing it for serialization/fsync.

#### 7. Logging off destroys muxd and all hosted agents [HIGH]

- **Evidence:** The muxd scheduled task uses `LogonType Interactive` and
  `RunLevel Limited`.
- **Why it matters:** The daemon and its PTY children are tied to the
  interactive logon session. User logoff is therefore a destructive lifecycle
  event, not merely a lost UI connection.
- **Fix:** Run muxd in a session-persistent service/task context with an account
  and privileges appropriate to PTY ownership, or explicitly document and gate
  logoff as destructive until that migration is complete. Add a logoff/reboot
  lifecycle test.

#### 8. Relay-to-muxd LAN link is plaintext and places its token in the URL [HIGH]

- **Evidence:** `muxd.py:3657` and `muxd.py:3708` establish a `ws://` connection
  with the host token in the query string. `PLAN.md:72-73` already acknowledges
  the exposure.
- **Why it matters:** A LAN observer or rogue host can capture the credential or
  tamper with create/input/kill traffic. Query credentials also leak into common
  request and diagnostic logs.
- **Fix:** Use TLS with certificate validation or pinning on the LAN path and
  send authentication in the first protocol frame or an authorization header,
  never in the URL. Rotate the token after deployment.

#### 8A. Desktop bridge and relay implement incompatible command protocols [HIGH]

- **Evidence:** Both bridge implementations POST to
  `/api/app-commands/lease` and require `leaseToken`, `intentId`, and a matching
  `replayPolicy`: `MainPage.Remote.cs:329-347`,
  `RemoteBridge.cs:162-180`, and `RemoteCommandProtocol.cs:19-39`. The audited
  relay exposes only `POST /api/app-commands`, `GET /api/app-commands`, and
  `POST /api/app-commands/:id/ack` (`server.js:1036-1068` in the current
  working tree); `rg -n 'lease|replayPolicy|intentId' server.js` returns no
  matches. `enqueueAppCommand` consequently stores none of the fields the
  bridge requires.
- **Why it matters:** At these revisions the lease POST receives no compatible
  JSON command list, and even a command obtained through the old GET shape
  would be rejected by `IsReplaySafe`. Web transcript, fetch, rename,
  collection, start-mux, history, and tab-color actions cannot complete. The
  poller silently returns on a non-JSON/404 body at
  `MainPage.Remote.cs:334-337`, so the failure is easy to misdiagnose as an
  idle queue.
- **Fix:** Land one versioned command protocol atomically on relay, GUI bridge,
  and headless bridge. Add the lease route, server-generated intent IDs,
  lease tokens, replay policies, lease expiry/requeue semantics, and
  capability-aware integration tests. Until then, either deploy the matching
  relay revision or explicitly revert both bridge consumers to the old GET/ack
  protocol; do not mix revisions.

#### 8B. Relay queue is treated as PC action authority without end-to-end command provenance [HIGH, CURRENTLY MASKED BY 8A]

- **Evidence:** The GUI bridge leases and executes relay data at
  `MainPage.Remote.cs:329-435`; the headless bridge does the same at
  `RemoteBridge.cs:162-237`. The replay check in
  `RemoteCommandProtocol.cs:19-39` compares only a command type with a plaintext
  policy string. Supported effects include transcript return, native/app
  rename, file transfer plus prompt insertion, collection mutation, and
  `startmux`. `startmux` does correctly ignore relay-supplied command text and
  rebuild a launch from local archive state through
  `MainPage.Remote.cs:482-545` and `ArchiveService.cs:3644-3701`, but the fact
  that the request was authorized is still inherited entirely from the relay
  queue. Transcript reads at `MainPage.Remote.cs:373-376` and
  `RemoteBridge.cs:204-205` are not restricted to IDs present in the last
  projection.
- **Why it matters:** After the lease mismatch is repaired, the relay's
  ambient-loopback owner bypass becomes a remote PC capability: a same-host VPS
  process can enqueue a fresh trusted agent launch, request a known local
  transcript ID, rename local records, or inject a downloaded file path into a
  mux prompt. A correct `replayPolicy` string proves operation semantics, not
  who authorized the operation.
- **Fix:** Remove ambient relay trust first. Give the desktop bridge a dedicated
  pull/ack credential and bind every queued command to an owner-authenticated
  enqueue event, command schema version, expiry, monotonic intent ID, and
  allowed consumer capability. Restrict transcript/rename/start intents to an
  ID or fresh-launch capability explicitly present in a current bridge-signed
  projection. Audit-log the enqueue principal and bridge execution result.

### Medium

#### 9. Relay authentication cache has unbounded negative-entry growth [MEDIUM, RESOLVED IN CURRENT WORKTREE]

- **Evidence:** `_authCache` at `server.js:34` and `server.js:39-50` caches every
  distinct cookie, including `owner:false`, for 60 seconds with no size cap or
  periodic expired-entry eviction.
- **Why it matters:** An unauthenticated client can send many unique cookies and
  force avoidable memory growth.
- **Fix:** Do not cache negative results, impose an LRU/maximum-entry bound, and
  remove expired entries proactively. Add a distinct-cookie flood test.
- **Current status:** The uncommitted diff adds `AUTH_CACHE_MAX = 5000` with
  oldest-entry eviction. This removes unbounded growth; retaining negative
  entries is now a bounded policy choice rather than a memory DoS.

#### 10. WebSocket authorization is never revalidated [MEDIUM]

- **Evidence:** Owner authorization is checked once when `/ws` connects around
  `server.js:1330`; no later expiry/revocation check closes the socket.
- **Why it matters:** A terminal connection remains controllable indefinitely
  after the underlying owner session expires or is revoked.
- **Fix:** Bind the socket to the authenticated session expiry and revocation
  state. Revalidate periodically and on sensitive frames, and close with a
  specific code when authorization ends.

#### 11. `POST /api/projects` is not restricted to the local bridge [MEDIUM, RESOLVED IN CURRENT WORKTREE]

- **Evidence:** The route at `server.js:737` lacks `isTrustedLocal`, although the
  route contract comment at `server.js:517-519` describes it as loopback-only;
  `/api/running` at `server.js:774` does enforce local trust.
- **Why it matters:** Any owner-authenticated relay client can replace the
  projects projection, including `runningSessions` and `runningVerified`. Those
  values feed `ensureNoLocalOwnerForMuxName` at `server.js:680`, so a compromised
  owner client can corrupt a double-writer safety decision.
- **Fix:** Require a bridge-specific capability on this route, schema-validate
  the complete projection, and keep safety-critical ownership facts sourced
  from muxd/OS observation rather than a UI projection.
- **Current status:** The uncommitted diff now rejects this route unless
  `isTrustedLocal(req)` succeeds. A bridge-specific secret is still preferable
  to ambient loopback trust.

#### 12. Tests are not connected to a clean package/deploy gate [MEDIUM, PARTIALLY RESOLVED IN CURRENT WORKTREE]

- **Evidence:** `multiplex-app/package.json` leaves `scripts.test` as
  `echo Error... && exit 1`; the suite is run only with manual `node --test`.
  `package.json` also names stale `index.js` as `main` instead of `server.js`.
- **Why it matters:** A normal CI/deploy invocation cannot exercise the existing
  tests, making regressions in authentication and protocol handling easier to
  ship.
- **Fix:** Set `test` to the real `node --test` command, correct `main`, and make
  service deployment depend on lint/test/startup smoke checks.
- **Current status:** The uncommitted `package.json` diff corrects `main` and
  points `npm test` at the three suites. However, an ordinary `npm test` run did
  not terminate and left the relay test process plus multiple child
  `server.js` processes alive. The same suite passed 25/25 in 8.7 seconds with
  Node's `--test-force-exit`, which confirms assertion success but also confirms
  that the current package command is not yet a clean deploy gate. Fix test
  teardown and make the deployment service invoke the clean command.

#### 13. Session-create timeout leaves a phantom live row [MEDIUM]

- **Evidence:** `POST /api/sessions` optimistically inserts into `hostSessions`
  at `server.js:369`; its 15-second timeout returns 504 without removing that
  entry.
- **Why it matters:** The UI can show a session as alive even though creation
  was never confirmed, until a later host status push repairs the projection.
- **Fix:** Represent creation as an explicit pending state keyed by request ID.
  On timeout remove or mark the entry unknown, then reconcile only against a
  host acknowledgment/status generation.

#### 14. Kill and status rebuild can resurrect a deleted session [MEDIUM]

- **Evidence:** Kill deletes at `server.js:1227`, while an in-flight status
  rebuild can re-add the same session at `server.js:1181-1182`; there is no
  symmetric pending-kill/tombstone guard.
- **Why it matters:** A stale status frame can make a killed session appear live
  again and can confuse subsequent actions.
- **Fix:** Add per-session operation generations/tombstones and ignore status
  data older than the acknowledged kill generation.

#### 15. Synchronous tmux probes block the relay event loop [MEDIUM]

- **Evidence:** `legacyTmuxNames()` and `tmuxHas()` use `execSync` on every
  `/api/sessions` poll (`server.js:298`), every mutating route, and
  `/api/health` (`server.js:967-969`).
- **Why it matters:** Multiple polling clients or a slow tmux command can stall
  all HTTP and WebSocket work even though tmux is now only legacy support.
- **Fix:** Remove the fallback if migration is complete. Otherwise run it
  asynchronously behind a short-TTL cached snapshot with a deadline and
  concurrency limit.

#### 16. Terminal/agent state still depends on footer text regexes [MEDIUM]

- **Evidence:** Fallback `attentionStatusForHosted` at `server.js:254-295`,
  auto-heal death/goal detection at `server.js:830-843`, and `paneAgentState` at
  `server.js:194-214` infer state from terminal footer text.
- **Why it matters:** UI wording, terminal width, localization, or ordinary
  output that resembles a footer can produce false death, attention, or heal
  decisions.
- **Fix:** Move lifecycle/goal/attention state into explicit versioned protocol
  fields from the agent host. Retain text inference only as visibly degraded
  compatibility behavior, never as an auto-heal authority.

#### 17. muxd writes the manifest while holding the global state lock [MEDIUM]

- **Evidence:** `manifest_save_async` serializes and writes through the executor
  while `state_lock` remains held at `muxd.py:2372-2378`.
- **Why it matters:** A slow filesystem write blocks unrelated input, create,
  kill, and status mutations.
- **Fix:** Copy an immutable snapshot and generation under the lock, release the
  lock, perform the durable write, and commit/queue a newer generation without
  serializing all state operations behind fsync.

#### 18. One shared relay output queue lets a noisy session drop other sessions [MEDIUM]

- **Evidence:** `RelayOutQueue(maxsize=64)` is shared across all sessions at
  `muxd.py:1205-1217`; full-queue frames are silently dropped while only a
  counter is incremented.
- **Why it matters:** Output from one flooding PTY can consume the entire queue
  and create unexplained gaps in unrelated terminals.
- **Fix:** Use per-session byte-bounded queues and fair round-robin scheduling.
  Emit an explicit gap/resync marker when any session drops data.

#### 19. Live remote keystrokes can be silently discarded [MEDIUM]

- **Evidence:** Relay `i` frames reach `Session.write` at
  `muxd.py:1610-1613`, which ignores a false return from `_enqueue_writer_item`
  at `muxd.py:1552-1558` when the PTY is momentarily unavailable.
- **Why it matters:** User input can vanish during a revive/rebind transition
  with no error or retry signal.
- **Fix:** Return an acknowledgment/error tied to an input intent ID. Buffer
  within a strict bound during recoverable transitions or fail visibly so the
  client can decide whether to retry.

#### 20. Output overflow drops the middle of the stream without a resync signal [MEDIUM]

- **Evidence:** At `muxd.py:1496-1499`, and in the duplicated `OwnerSession`
  implementation, overflow deletes the older portion and retains the newest
  512 KB without marking a discontinuity.
- **Why it matters:** The terminal parser receives a syntactically continuous
  but semantically gapped byte stream, which can corrupt screen state and cause
  blank or malformed rendering.
- **Fix:** On overflow discard to a known snapshot boundary and send an explicit
  reset/full-tail frame. Prefer bounded per-client backpressure over invisible
  middle deletion.

#### 21. Relay protocol lacks version negotiation and validates only create [MEDIUM]

- **Evidence:** Only `create` is schema-validated through
  `REMOTE_CREATE_FIELDS` at `muxd.py:2094-2138`. `i`, `resize`, `sb`, `tail`,
  `kill`, `heal`, and `rename` at `muxd.py:3769-3820` are accepted without
  versioned schemas.
- **Why it matters:** Relay/muxd skew can silently reinterpret fields or apply
  malformed operations.
- **Fix:** Negotiate a protocol version/capability set at connection time and
  validate every frame with strict type, range, and unknown-field handling.

#### 22. Timed-out confirmed writes can later double-apply input [MEDIUM]

- **Evidence:** `write_confirmed` returns timeout after eight seconds at
  `muxd.py:1627`, but the queued item remains and the writer can still apply it
  after PTY recovery at `muxd.py:1580-1608`.
- **Why it matters:** A client retry can inject the same command twice. The
  actual behavior is at-least-once after timeout, not the intended at-most-once
  contract.
- **Fix:** Give every input an idempotency/intent ID. On timeout cancel or mark
  the queued item superseded before returning; deduplicate retries at muxd.

#### 23. Flaky WMI ownership checks fail closed without a bounded recovery path [MEDIUM]

- **Evidence:** WMI/CIM failures at `muxd.py:1062-1067` and
  `muxd.py:721-726` feed create/relaunch refusal paths at
  `muxd.py:3274-3275`, `muxd.py:1085`, and `muxd.py:1123`.
- **Why it matters:** A transient WMI stall blocks legitimate create and
  relaunch operations, compounding the executor-starvation incident.
- **Fix:** Cache recent verified ownership, retry on a dedicated bounded
  executor, distinguish timeout from an actual ownership conflict, and surface
  a recoverable degraded error.

#### 24. Relay has no graceful shutdown path [MEDIUM]

- **Evidence:** `server.js` installs no SIGTERM drain/close handling.
- **Why it matters:** Service deployments hard-drop all browser and host
  WebSockets and can interrupt in-flight creates/commands without a clear
  reconnect reason.
- **Fix:** On SIGTERM stop accepting upgrades, mark service draining, close
  sockets with a retryable code, wait for bounded in-flight work, and then exit.

#### 25. Authentication and upgrade paths have no rate limiting [MEDIUM]

- **Evidence:** No relay-side rate limiter protects owner-cookie validation,
  `/ws` upgrade attempts, or command routes. nginx has no separate
  `limit_req` zone for `/multiplex/`.
- **Why it matters:** Attackers can amplify authentication/cache pressure and
  connection churn even when authorization ultimately fails.
- **Fix:** Add IP/account-aware limits at nginx and tighter application limits
  for authentication failures and WebSocket upgrades, with exemptions only for
  authenticated host/bridge capabilities.

#### 25A. Status rendering is an HTML sink reached by unescaped server-derived text [MEDIUM]

- **Evidence:** `public/index.html:1466` assigns caller text to
  `#statustext.innerHTML`; `flash` forwards arbitrary text to it at
  `index.html:1919`. Several call sites concatenate server/session/error data
  without `esc`, including the dormant session detail at `index.html:1761`,
  app-command errors at `index.html:1858`, relaunch errors at
  `index.html:1917`/`1952`, rename error details at `index.html:2107`, and
  upload/transfer errors at `index.html:2208`/`2236`.
- **Why it matters:** A malformed or compromised relay/bridge response that
  places HTML in an error/detail/title path can execute script in the owner
  origin. That script inherits the owner cookie's same-origin API access.
  Normal tab names are rendered with `textContent`, so the unsafe surface is
  concentrated in status/error rendering rather than every title.
- **Fix:** Replace `setStatus(state, html)` with structured DOM construction:
  a text-only default plus a small explicit helper for the few static status
  fragments that need markup. Never pass server text through `innerHTML`.
  Add a test using `<img src=x onerror=...>` in every server-derived detail.

#### 25B. WebSocket control and session payloads are not schema/range validated in the client [MEDIUM]

- **Evidence:** `onControl` parses any string beginning with `d`, assigns
  `m.cols`, `m.rows`, `m.clients`, and labels directly, then calls
  `term.resize(m.cols, m.rows)` at `public/index.html:1165-1171`. Session polling
  accepts any JSON array at `index.html:1617-1635`, and `renderTabs` assumes
  every element has the expected object/string fields at
  `index.html:1647-1663`.
- **Why it matters:** A skewed or corrupted relay frame can request enormous
  terminal dimensions, trigger excessive canvas/buffer allocation, leave
  `sizeClients` as a non-array that later throws, or make one malformed session
  entry abort the whole render loop.
- **Fix:** Validate a versioned client schema before applying frames. Clamp
  dimensions to the same protocol maxima as create/resize, require finite
  integers and bounded labels/arrays, reject unknown control versions, and
  discard malformed session entries while surfacing a visible degraded error.

#### 25C. Client output buffering is not byte-bounded and drops frozen output without resync [MEDIUM]

- **Evidence:** `enqueueTermWrite` appends 64 KiB slices to an unbounded
  `termWriteQueue` at `public/index.html:872-915`. While selection is frozen,
  `index.html:1569` retains up to 6,000 WebSocket messages rather than a byte
  budget, then drops the oldest messages with no terminal reset/resync marker.
- **Why it matters:** A fast-output session can grow browser memory faster than
  xterm parses it. Selection mode can retain hundreds of megabytes depending
  on frame size; if the chunk-count limit is reached, replaying the remaining
  suffix into the existing parser state can produce a corrupted or blank
  terminal.
- **Fix:** Track queued bytes, impose per-state byte budgets, and apply
  backpressure or detach/replay from a known scrollback snapshot. If any bytes
  are dropped, clear/reset xterm and request a full tail instead of silently
  feeding a gapped stream.

#### 25D. Mobile visual-viewport changes leave the app at its boot-time height [MEDIUM]

- **Evidence:** `applyViewport` writes `visualViewport.height` into the inline
  `#app.style.height` at `public/index.html:1248-1252`, and boot calls it at
  `index.html:2528`. Subsequent `visualViewport.resize` events call only
  `scheduleViewportFit` at `index.html:1254`, so the inline height is never
  updated when the mobile keyboard/address bar changes the visual viewport.
- **Why it matters:** The stale inline height overrides the `100dvh` grid and
  can leave terminal rows or mobile controls covered, clipped, or occupying
  dead space after keyboard open/close and browser chrome changes.
- **Fix:** Have the resize handler call a throttled `applyViewport`, including
  `offsetTop` where needed, or remove the inline height and rely on verified
  `dvh` behavior. Add viewport tests that open/close a virtual keyboard-sized
  visual viewport and assert every grid row remains visible.

#### 25E. Mobile duplicate suppression silently discards legitimate repeated input [MEDIUM]

- **Evidence:** `repeatedWholePhrase` collapses repeated 12+ character chunks
  at `public/index.html:1372-1379`; `filterMobileTextInput` drops an identical
  8+ character chunk repeated within 1.5 seconds and any chunk already present
  in the six-second memory at `index.html:1389-1425`. This filter runs on every
  mobile `term.onData` payload at `index.html:1444-1457`, including intentional
  paste/repeat actions.
- **Why it matters:** Repeating a command, token, phrase, test vector, or paste
  can be silently changed or removed. A heuristic intended to repair one IME
  can therefore corrupt terminal input for other keyboards and workflows.
- **Fix:** Keep deduplication only at the helper-textarea event boundary where
  an individual browser event can be correlated, not by content/time in the
  terminal byte stream. Use event IDs/state transitions and add tests for
  intentional repeated paste, emoji/graphemes, autocomplete replacement, and
  hardware keyboards on a mobile viewport.

#### 25F. Open tabs have no deploy/protocol version negotiation [MEDIUM]

- **Evidence:** HTML responses receive no-store headers at `server.js:70-77`,
  and the application logic is inline in that HTML. However,
  `window.__muxBuild='2026-07-15-wheelscroll'` at
  `public/index.html:958` is only logged locally; reconnect at
  `index.html:1518-1580` performs no build or protocol handshake.
- **Why it matters:** A normal navigation receives fresh code, but a tab left
  open across a relay deployment reconnects indefinitely with stale JavaScript.
  A frame-shape change can then be misinterpreted rather than forcing a reload.
- **Fix:** Include client build and protocol version in the initial WebSocket
  request/hello. Have the relay reject incompatible clients with a dedicated
  close code and have the client perform one cache-bypassing reload. Preserve
  no-store for HTML.

#### 25G. Projects projection has no total serialized-size budget [MEDIUM]

- **Evidence:** `ArchiveService.cs:3817-3834` projects every launchable chat in
  every collection with no global/per-collection cap; only the separate
  `allChats` list is capped at 500 at `ArchiveService.cs:3835-3839`. Chats can
  be duplicated between collection lists and `allChats`, with title and alias
  arrays included. The relay JSON limit is 6 MiB.
- **Why it matters:** A large archive or heavily collected chat set can exceed
  the relay body limit and make all project/running synchronization fail. The
  bridge currently logs the curl response but has no projection truncation
  contract or incremental sync.
- **Fix:** Define a projection byte budget and deterministic truncation/page
  metadata, cap aliases/title lengths, avoid duplicate chat objects through an
  ID-indexed schema, and fail visibly when the relay rejects a projection.

#### 25H. `fetchfile` trusts an unsanitized upload ID as a filesystem path [MEDIUM]

- **Evidence:** The relay stores `uploadId` as an arbitrary string and requires
  only that it be nonempty (`server.js:1014-1042` in the baseline/current
  region). `RemoteUploadTransfer.cs:24-43` validates only non-emptiness, then
  passes `uploadId` directly to `Path.Combine(destRoot, uploadId)` and embeds it
  in the SCP remote path. Only the filename is sanitized at
  `RemoteUploadTransfer.cs:142-150`.
- **Why it matters:** A forged relay command can use rooted or `..` path
  components to escape the intended local upload directory and can alter the
  remote SCP source path. Depending on an available source file, this can
  overwrite an attacker-chosen filename outside the transfer root.
- **Fix:** Require the exact server-generated upload-ID grammar and length,
  reject separators/rooted paths/dot segments, resolve the final local path and
  verify it remains under `destRoot`, and resolve the remote source from
  server-returned immutable metadata rather than reconstructing it from command
  fields.

### Low

#### 26. Health status conflates relay health with an optional/stale bridge poll [LOW]

- **Evidence:** `/api/health` couples its degraded result to `bridgeLive` at
  `server.js:977`.
- **Why it matters:** The UI red indicator becomes normal when the desktop
  bridge is intentionally down, reducing its value for actual relay/muxd
  outages.
- **Fix:** Report independent components (`relay`, `muxd`, `desktopBridge`) and
  derive severity according to whether each component is required for the
  current operation.

#### 27. Relay contains dead legacy guard logic [LOW]

- **Evidence:** The guard at `server.js:1348` checks `!legacyBlocked` where the
  condition is always true.
- **Why it matters:** Dead conditions obscure the actual routing/security
  contract and can mislead later maintenance.
- **Fix:** Remove the guard or restore a meaningful, tested state transition.

#### 28. JSON bodies are parsed before route authorization [LOW]

- **Evidence:** Global `express.json({ limit: '6mb' })` is installed at
  `server.js:20`.
- **Why it matters:** Unauthorized callers can force allocation and parsing of
  multi-megabyte JSON before rejection.
- **Fix:** Use small route-specific body limits and authenticate/capability-check
  before parsing large upload payloads where the framework permits.

#### 29. muxd durability, threshold, duplication, and bounds defects add operational risk [LOW]

- **Evidence:** `_fsync_directory` is a no-op on Windows at `muxd.py:83-85`;
  watchdog constants are inverted/dead (`EXIT=12 < WARN=30`) at
  `muxd.py:897-898` and `muxd.py:1897-1908`; `session_payload` computes
  `tail_text` twice at `muxd.py:2198` and `muxd.py:2225` every five seconds per
  session; roughly 200 lines are duplicated between `Session` and
  `OwnerSession`; relay resize has only minimum bounds at `muxd.py:1631-1632`
  while create clamps at `muxd.py:2123-2134`; intent history keeps
  180 days/20,000 entries and rewrites the whole structure under lock at
  `muxd.py:1821-1822` and `muxd.py:1942-1960`.
- **Why it matters:** These defects individually have limited impact but
  increase crash-durability ambiguity, dead watchdog behavior, unnecessary CPU
  and lock time, drift between duplicated implementations, and memory/terminal
  abuse from oversized resize values.
- **Fix:** Implement/verify Windows replacement durability semantics, correct
  and test watchdog thresholds, compute tails once, consolidate shared session
  logic, apply upper resize bounds on every path, and move/prune intent history
  outside the global state lock.

#### 30. Any local user can request the scheduled muxd task [LOW]

- **Evidence:** `muxctl` invokes `schtasks /Run MuxdSessionHost` at
  `muxd.py:155` without an additional caller authorization layer.
- **Why it matters:** This can create lifecycle churn or start muxd at unwanted
  times, although it does not by itself grant the caller muxd's control
  credential.
- **Fix:** Restrict task execution ACLs to Ahmed/administrators and make
  `muxctl` verify the expected caller.

#### 31. Mouse-wheel preservation covers only SGR mouse reports [LOW]

- **Evidence:** `public/index.html:1435` strips every X10 mouse report before
  inspecting its button byte; `index.html:1441` preserves wheel reports only
  for SGR/1006 when `Cb & 64`; and `index.html:1442` strips every urxvt/1015
  report. `tests/wheel-scroll.test.js:44-45` explicitly expects generic X10
  and 1015 reports to be removed. Direct source-function probes produced:
  `SGR wheel => preserved`, `X10 wheel => dropped`, and
  `urxvt/1015 wheel => dropped`.
- **Why it matters:** The recent fix restores scrolling for applications using
  SGR mouse mode, but a TUI that negotiates X10 or 1015 wheel encoding still
  cannot receive wheel input in its alternate buffer. The broad comment and
  test title that wheel is preserved overstate the actual compatibility.
- **Fix:** Decode X10's encoded button byte and 1015's first numeric field,
  preserve only reports with the wheel bit set, and continue stripping
  motion/click/drag reports. Add wheel-up/down and modified-wheel tests for all
  three encodings.

#### 32. Initial session-load failure is rendered as a valid empty system [LOW]

- **Evidence:** `loadSessions` swallows fetch/JSON errors at
  `public/index.html:1617-1619`. When no prior cache exists, the failure branch
  substitutes an empty array at `index.html:1620-1629`; the normal empty-state
  path then renders `No sessions` at `index.html:1645`.
- **Why it matters:** An authentication failure, relay outage, or malformed
  response on first load is indistinguishable from a healthy relay with zero
  sessions. The four-second retry may recover, but the owner receives no
  actionable signal while it is failing.
- **Fix:** Track load state separately from session data. On first-load failure,
  show a persistent `Unable to load sessions` state with HTTP/error category
  and retry control; retain the last-known tabs only after at least one
  successful load.

## nginx and serving verification

- `/etc/nginx/sites-enabled/harmonizer:303` proxies `/multiplex/` to
  `http://127.0.0.1:7682/`, forwards WebSocket `Upgrade`/`Connection`, overwrites
  `X-Forwarded-For` with `$remote_addr`, and sets read/send timeouts to 86400
  seconds. WSS upgrade forwarding is therefore configured correctly.
- The relay nevertheless listens on `0.0.0.0:7682`; the upstream-only intent is
  not enforced at the socket boundary.
- No request-body limit specific to `/multiplex/` and no separate rate-limit
  zone were present in the inspected location.
- `multiplex-app.service` runs as `User=harmonizer` on Node `v24.18.0`.

## Positive controls and resolved items

- Static serving uses no-store headers in `server.js:70-77`. Because the
  application logic is inline in `index.html`, a normal navigation receives
  fresh client JavaScript; finding 25F is limited to already-open tabs.
- Session names and normal tab/title data are mostly assigned with
  `textContent` or escaped through `esc`; the confirmed HTML-injection surface
  is concentrated in status/error rendering.
- The client has reconnect generations, paint/write watchdogs, scroll-position
  preservation, manual terminal reload, and jump-to-bottom recovery paths.
  These reduce ordinary blank/frozen-terminal failures but do not make silently
  gapped byte streams recoverable.
- The recent mouse change correctly strips SGR click/motion reports while
  preserving SGR wheel reports. Finding 31 narrows the remaining gap to
  X10/1015 encodings.
- The bridge projection carries launch intent (`id`, tool, mux name), not
  arbitrary command text. `startmux` reconstructs a trusted command from local
  archive state at `ArchiveService.cs:3430-3701`, validates session IDs, resolves
  trusted executable paths, and quotes PowerShell paths.
- `ReadCodexRolloutPath` uses a parameterized SQLite query at
  `ArchiveService.cs:3739-3752`; the thread ID is not concatenated into SQL.
- Sizing and pins are implemented.
- Boot recreation now creates hosted sessions rather than tmux sessions at
  `server.js:907-916`.
- Atomic writes are used by the relay.
- `/host` rejects a bad token with constant-time comparison before handshake at
  `server.js:1134-1137` and `server.js:1144`.
- Node is `v24.18.0`; the previously suspected Node 18 EOL issue does not apply.

## Suspected or unconfirmed findings

These items are plausible from source inspection but were not demonstrated as
an exploitable or user-visible failure in this audit.

### S1. Rollout path trust depends on integrity of the local Codex database [SUSPECTED, LOW]

- **Evidence:** `ReadCodexRolloutPath` returns the `rollout_path` value from the
  local `threads` table without canonicalizing it or checking that it remains
  below the expected Codex sessions directory
  (`ArchiveService.cs:3739-3752`). Transcript commands later use the returned
  path.
- **Why it might matter:** If another local principal can poison that database,
  a transcript request could read an unintended local file. This was not
  confirmed because the database ownership/ACL and downstream transcript
  parser behavior were not audited, and database write access may already imply
  equivalent access.
- **Fix if the boundary is intended:** Canonicalize the stored path, require an
  expected `.jsonl` extension, and verify it is below an Ahmed-owned Codex
  session root before opening it.

### S2. Mobile delta deletion counts UTF-16 code units, not user-perceived characters [SUSPECTED, LOW]

- **Evidence:** `taFlushDelta` finds a common prefix with `charCodeAt`, then
  emits one DEL per `prev.length - cp` at `public/index.html:996-1004`.
- **Why it might matter:** Emoji and combining sequences can span multiple
  UTF-16 code units; an IME replacement may emit more terminal DEL bytes than
  the displayed glyph/cell count. A real-device IME reproduction was not run,
  so the terminal's actual edit result remains unconfirmed.
- **Fix if reproduced:** Compute replacement edits by Unicode code point or,
  preferably, from composition/input event ranges and verify against grapheme
  and terminal-cell behavior on the supported mobile keyboards.

## Verification you ran

- The full Node suite passed **25/25** in 8.7 seconds with
  `node --test --test-force-exit tests/relay.test.js
  tests/client-layout.test.js tests/wheel-scroll.test.js`. An ordinary
  `npm test` did not exit and left its relay test and child `server.js`
  processes alive; those audit-owned processes were explicitly stopped.
- The client-only command
  `node --test tests/client-layout.test.js tests/wheel-scroll.test.js` exited
  normally with **7/7 tests passed**.
- Focused bridge tests:
  `dotnet test native/CodexLocalRetrieval.Native.Tests/CodexLocalRetrieval.Native.Tests.csproj --no-restore --filter "FullyQualifiedName~ArchiveServiceTests|FullyQualifiedName~RemoteCommandProtocolTests|FullyQualifiedName~RemoteUploadTransferTests|FullyQualifiedName~ArchitectureLaunchSurfaceTests"`:
  **145/145 tests passed**.
- Direct extraction/probes of the client functions confirmed:
  `repeatedWholePhrase("abcdeabcdeabcde") => "abcde"`;
  SGR wheel reports are preserved; X10 and urxvt/1015 wheel reports are
  dropped.
- Static protocol comparison confirmed that both bridge pollers call
  `/api/app-commands/lease` and require lease metadata, while the current relay
  has only enqueue/GET/ack routes and no `leaseToken`, `intentId`, or
  `replayPolicy` fields.
- Focused source review confirmed the bridge projection schema, trusted
  `startmux` reconstruction, parameterized rollout lookup, collection-size
  behavior, and `fetchfile` path construction.
- nginx/service facts were verified read-only on harmonizer: `/multiplex/`
  proxies to `127.0.0.1:7682`, forwards WebSocket upgrade headers, overwrites
  `X-Forwarded-For`, uses 86400-second proxy timeouts, and has no location-
  specific rate limit. `ss` showed the relay itself listening on
  `0.0.0.0:7682`; the service runs as `harmonizer` on Node `v24.18.0`.
- Earlier relay and muxd lanes supplied exact code/command evidence for their
  findings. This restart deliberately did not repeat a line-by-line audit of
  those surfaces.

## Open questions

1. Which relay revision introduced the lease protocol expected by
   `MainPage.Remote.cs` and `RemoteBridge.cs`, and was that revision omitted
   from this deployment or intentionally rolled back?
2. Can a hosted sandbox currently reach relay loopback port 7682 and muxd port
   7699 under its actual network/job restrictions? The code and listener
   configuration grant authority if reachable; a sandbox-identity integration
   probe would establish the exact blast radius.
3. Must muxd preserve active PTYs across Windows logoff, service restart, and
   watchdog recovery, or is destructive custody acceptable and documented?
   That requirement determines the service account and recovery design.
4. Which mouse encodings do the deployed Codex/Claude TUIs actually negotiate
   through the bundled xterm version? This determines whether finding 31 is a
   present user-visible defect or compatibility debt.
5. What is the largest real serialized projects projection and collection chat
   count? Measure it before selecting byte/page limits.
6. Are Codex's SQLite database and rollout files writable only by Ahmed/SYSTEM?
   That decides whether suspected finding S1 crosses a meaningful local trust
   boundary.
7. Should the desktop bridge execute commands only while the native app is
   visibly open, or is the headless bridge intentionally a persistent PC
   control plane? The answer should be explicit in command expiry, approval,
   and audit policy.

## Suggested fix order

1. Authenticate muxd loopback control, restrict and rotate `muxd.env`, then
   verify effective denial from the sandbox identity. These close direct local
   command execution and credential theft.
2. Bind the relay to `127.0.0.1`, remove ambient loopback owner trust, and give
   nginx/desktop bridge distinct scoped credentials. Do this before reviving
   the bridge command queue.
3. Resolve the bridge/relay lease mismatch atomically with a versioned,
   authenticated command protocol, expiry/idempotency, provenance, and
   integration tests for every command type.
4. Separate WMI/CIM work from muxd's control executor, release the global lock
   before durable writes, and add a bounded wedged-process recovery policy.
   Re-run the observed stall workload and watchdog tests.
5. Replace the LAN `ws://?...token=` host link with authenticated TLS and rotate
   the host token.
6. Remove the client `innerHTML` status sink; validate/range-limit every server
   frame; byte-bound output/frozen queues; require explicit resync after drops.
7. Fix viewport updates and mobile input deduplication, preserve wheel reports
   across the supported mouse encodings, and add real mobile/IME viewport tests.
8. Address operation generations, graceful shutdown, auth/upgrade rate limits,
   projection byte budgets, upload-ID path validation, and the remaining
   medium/low operational issues.

## Assessment of handed findings

I found no evidence that contradicts the handed relay, muxd, or nginx findings.
The current uncommitted worktree resolves the three items explicitly marked
`RESOLVED IN CURRENT WORKTREE`; the package/deploy-gate item is only partially
resolved because its test process does not terminate cleanly. All four changes
still need review and deployment. The bridge audit adds an important qualifier:
arbitrary relay-provided launch command text is not executed, because
`startmux` rebuilds the command locally. That positive control does not remove
the relay queue's authorization/provenance problem for the trusted actions it
can request.
