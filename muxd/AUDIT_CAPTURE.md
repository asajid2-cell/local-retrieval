# Multiplex — Deep Audit Capture (2026-07-06)

*Raw findings only. No plan yet — this is the evidence pile we step back from before deciding what to do.*

Sources: Codex rollout `2026-07-03T00-24-18` (97 user msgs / ~3-day siege), three parallel code audits (rollout-narrative, muxd Python daemon, relay+frontend), plus live ground-truth probes of the running VPS relay and the PC daemon.

---

## 0. What this system is

- **muxd** (PC, `C:\Users\Ahmed\muxd\muxd.py`, Python+pywinpty): owns a ConPTY per session locally; dials OUT over one token-gated WebSocket to the VPS relay. LAN `ws://` preferred, public `wss://` failover, active `lan_return` probe back. Ring-buffer scrollback (800KB/session), manifest (`sessions.json`) for reboot resume.
- **relay** (VPS, `~/multiplex-app/server.js`, Node): mirrors session state, bridges browser xterm.js viewers ↔ muxd. Holds only soft web state (pins, autoheal arm-list, projects projection, app-command queue, uploads). Legacy VPS tmux is now **blocked** (diagnostic-only).
- **frontend** (`public/index.html` SPA + `public/projects.html`): xterm.js terminal, mobile keybar, device-pinned sizing, paint-recovery harness. Live at `harmonizerlabs.cc/multiplex`, hl-auth owner-gated.
- **muxctl.py** local attach client; **muxrun.py** experimental visible-owner sidecar.

## 1. Live ground-truth (verified today)

- Relay healthy: `ok:true`, uptime ~6.3h, 14 hosted sessions, **0 legacy tmux** (`legacyPolicy:"blocked"`), PC (`CRACKERBARREL`) reachable RTT 6ms, host protocol v2 OK, **Node v24.18.0** (the EOL-node concern from the old reviews is already resolved).
- Public endpoints all return **401 unauthed** (`/`, `/api/sessions`, `/api/health`, `/host`) — hl-auth gate is live.
- Local files (`server.js`, `index.html`, `projects.html`, `relay.test.js`) are **byte-identical (md5) to the deployed VPS copies** — audits are of live code.
- muxd running as scheduled task `MuxdSessionHost`, **Logon Mode: Interactive only** (logoff still kills it — known), launched via `pythonw.exe` (black-window fix in place). Multiple python PIDs alive (sessions).
- muxd.log shows the LAN→public→LAN failover dance working repeatedly on 07-05, AND one real crash: `07-05 16:30 RuntimeError: deque mutated during iteration` (= C1 below, confirmed in production).
- **Uncommitted drift on VPS**: `package.json`/`package-lock.json` dirty; `server.js.bak-*` backups litter the dir.
- **muxd log-writer patch committed but NOT live** — daemon not restarted (would kill running sessions). Running daemon still has old logging path.

## 2. Confirmed code defects (severity-ranked, file:line)

### muxd (Python daemon)
- **C1 CRITICAL — ring deque race, crashed prod.** `_reader` thread does `ring.append/popleft` (`muxd.py:162-164`) while the asyncio loop iterates the same deque in `scrollback`/`tail_text` (`muxd.py:214-223`), called every 5s by `pump_status`. → `RuntimeError: deque mutated during iteration` tears down the whole relay link. **Observed 07-05 16:30.** Fix: lock the ring / snapshot under lock.
- **C2 CRITICAL — pump tasks die silently, no supervision.** `pump_out`/`pump_status`/`flush_out`/`self_heal_tick`/`local_serve` are bare `create_task` (`muxd.py:753-764,540-552`) with no exception handler/restart. If C1 (or any exc) fires outside the `async for` body, the task dies but the link stays up → output/status silently freeze while input still flows. `flush_out` death freezes output for ALL sessions permanently.
- **H1 — unbounded `outq` growth while relay down.** `outq` unbounded (`muxd.py:496`); `flush_out` enqueues `o` frames every 12ms regardless of link state; only consumer `pump_out` exists only while connected. Hours-long outage + armed agents → memory grows unbounded + stale-output replay flood on reconnect. 2MB pending cap doesn't apply once chunked into outq.
- **H2 — loopback control port = drive-by RCE.** `muxd.py:733` serves `ws://127.0.0.1:7699` with **no token, no origin check**; `create` accepts arbitrary `cmd`/`cwd` → PowerShell spawn. Any webpage in any browser on the PC can `new WebSocket("ws://127.0.0.1:7699")` and run arbitrary PowerShell as Ahmed. Fix: token in first frame and/or reject browser `Origin`.
- **H3 — plaintext LAN + token in URL.** `RELAY_LAN=ws://…` (plaintext, and it's the *preferred* path); token appended as `?token=` query (`muxd.py:745,773`) → lands in VPS access logs, sniffable on LAN, static/no-rotation. `muxd.env` unhardened.
- **H4 — keystroke path trusts relay 100%.** `muxd.py:829-830` writes any relay `i` frame straight to the PTY; no PC-side authentication of frames. Whole RCE gate lives on the VPS (VPS integrity + hl-auth + plaintext LAN).
- **H5 — Session/writer-thread leak per kill/relaunch.** `_writer` thread blocks on `wq.get()` forever; `kill()` (`muxd.py:233-242`) never enqueues `None` sentinel → thread + ring (≤800KB) pinned forever. Fix: one line `self.wq.put(None)` in `kill()`.
- **M1** heal-vs-create race → orphan ConPTY zombie. **M2** `manifest_save()` sync file I/O on loop (same stall class the log-writer fix already proved fatal, 30-63s freezes). **M3** LAN→public→LAN churn on sub-second blips + clean-close drop logged as nothing + fast hammer loop. **M4** owner mode can't deliver Ctrl+C. **M5** remote input not gated when local viewer attached; stale-tab create can kill a live session. **M6** normal detach logged as `failed=True`, pollutes error stats. **M7** muxrun retry spam into the agent's own console. **M8** watchdog exit<warn incoherence + sleep/resume false stack dumps.
- LOWs: dead/duplicate statements (L1,L2), UTF-8 mid-sequence tail slicing (L4), `_type_cmd` bypasses serialized writer (L6), log drop under load (L7). `muxd.crash` = 0 bytes = **no native crash ever** (tripwire, not evidence).

### relay + frontend
- **C1(relay) CRITICAL — no Origin check on `/ws` → Cross-Site WebSocket Hijack.** `server.js:980-989,1163-1164` authorizes purely on `hl_session` cookie, no `Origin` validation. If cookie is `SameSite=None` (common for cross-subdomain SSO, set by hl-auth outside this repo), any third-party page the owner visits can open the ws as owner → create PC shell + inject keystrokes = full RCE. **Either exploitable now or one config change away** — depends on hl-auth cookie policy (VERIFY).
- **H1(relay) — unknown session auto-spawns PC shell; guard is dead-true.** `server.js:1182` `!legacyBlocked` is always true (already returned at 1177-1181). **VERIFIED by reading source.** Condition collapses to `hostUp()`; any `/ws?session=<anything>` spawns a new muxd shell. Combined with CSWSH = the RCE primitive; alone = stale bookmark/typo spawns orphan shells.
- **H2(relay) — burst output during attach silently dropped.** `server.js:1048`: >800 queued frames while waiting for scrollback → sets `sbWait=false` and `continue`s WITHOUT flushing `c.q` or sending CLEAR → viewer loses output + stacks live bytes on stale screen. **This is a strong candidate root cause for the repeated "black/unrendered terminal + gaps in streamed output" complaint (see §3).**
- **H3(relay) — no backpressure / bufferedAmount handling.** `server.js:1049,1057` tight `ws.send` loop, no `bufferedAmount` check on viewer OR host leg → slow phone on weak link → unbounded relay memory / OOM on shared VPS.
- **H4(relay) — dead muxd link never detected.** `server.js:1063-1064` pings every 20s but no `pong` handler / no terminate-on-missed-pong. Black-holed muxd (the exact target failure) leaves `hostUp()` true → creates/attaches hang, `sendHost` "succeeds" into a rotting buffer.
- **M-tier:** replaced-muxd stale handler still mutates global state (M1); host token in ws URL query (M2); autoheal keystroke injection races a live typist + `cls` is PS-only (M3); no CSRF, `/api/upload` uses `express.raw type:*/*` = non-preflighted cross-origin write (M4); client `termWriteQueue` unbounded (M5); `_authCache` never evicts (M6); kill/rename can report failure on success (M7).
- **LOWs:** `intentional` reconnect flag leaks after kill (L1); 101 before auth reject (L2); `execSync` tmux on hot paths (L3); coarse loopback trust (L4).
- **XSS: clean.** Names `SAFE()`'d, rendered via textContent/esc. No sink found.

## 3. User pain points — the OPEN ones (from the siege transcript)

These are the ones the transcript does NOT show clearly resolved — the real backlog:

1. **Web terminal black/unrendered + gaps in streamed output** — raised **≥6 times** (Jul 4-5), an early fix was for the wrong project, a sizing fix was explicitly rejected. THE most-repeated complaint. Likely tied to relay-H2 (dropped burst) and/or the paint-recovery harness. **OPEN, highest user-emotion.**
2. **Mobile input duplication** — "delete one thing and it'll delete everything"; user rejected the separate-input-box workaround, wants the root cause. Dedup harness exists (`index.html:1148-1226`) but not confirmed fixed. **OPEN.**
3. **Mobile scroll clamp above the input bar** — can't see/reach the bottom bar. **OPEN.**
4. **Escape-sequence / DA-reply injection into the prompt** — raw `[?1;2c`, `]10;rgb:…` leaking into input (web-mobile AND local "random numbers into terminal"). Terminal query replies (DA/OSC 10/11) escaping into the input path. **OPEN.**
5. **Web scrollback too shallow** — "only lets us scroll up a bit not to the top" vs the deliberate 60000-byte bounded-attach perf tradeoff. **DESIGN TENSION.**
6. **Trustworthy session status** — "green/yellow/red is arbitrary… I want it trustable at a glance." Wants real finished/working/needs-attention. **OPEN feature.** (Note: dots still footer-regex based = D4 fragility; muxd now owns the pty and could report process-tree truth.)
7. **Retrieval-app fork bug** — forked chat resumes the pre-fork session id; wants correct id + reroll/reassign. **UNCLEAR.**
8. **CPU 99% / rebalance** — outcome not shown. **UNVERIFIED.**

## 4. Feature wishlist signals (mentioned, not built)

- At-a-glance trustworthy status (real detection, process-truth not regex).
- Past-sessions browser: scan `~/.claude/projects` + `~/.codex/sessions`, resume any into muxd; `allChats` is a partial 500-cap projection, wants pagination/search.
- Transcript viewer from the sessions list ("see which is which").
- Dedicated mobile flow / per-device views.
- Start-mode choice: visible-local+web vs headless-until-`mux name`.
- Full web scrollback.
- Deck-aware everything across all collection touchpoints.
- Watcher/autoheal arming UX (opt-in per session; concept exists in muxd, no UI).

## 5. What's genuinely GOOD (keep, don't touch)

- **Ownership flip is the right architecture and it works** — outbound-only dial, LAN-preferred + wss failover + active return, dozens of clean failover cycles surviving relay deploys / AP flaps / multi-hour outages with zero session loss. Proven live + in one real unplanned incident.
- **Evidence-driven hardening**: the loop-monitor + cross-thread stack dumper caught the exact sync-`log()` disk-stall root cause; the `LOG_Q` writer-thread fix is precisely targeted. The `alive()`-must-not-touch-winpty lesson is encoded as a regression test.
- **Layered backpressure in muxd**: 800KB ring + 2MB pending cap + 12ms coalescing, each tier commented.
- **ConPTY input discipline**: serialized single-writer queue, 1KB paste chunks, 4ms pacing (real obscure garbling fix), mirrored client-side in muxctl.
- **Relay correctness wins**: `pendingCreates` prevents double-resume transcript-forking; single-writer transcript discipline (kill-before-relaunch, no "resume anyway" escape hatch) is consistent across server + both UIs; clear-screen→replay→queued-live scrollback ordering; atomic tmp+rename state writes; constant-time token compare; `/host` bad token rejected pre-handshake.
- **Frontend**: Chromium glyph-blank master/detail fix + canvas-pixel paint-recovery harness are real and thorough; mobile input dedup + sticky-mod keybar are serious attempts at hard mobile-xterm problems; device-pinned last-writer-wins sizing is coherent.
- **Observability**: `/api/health` with PC sshd TCP-probe RTT + DHCP re-resolution.

## 6. Test coverage gaps (highest-value missing)

- muxd: concurrent ring stress (would've caught C1); fake-relay reconnect (H1/C2/M3); session-churn leak (H5); heal-vs-create race (M1); manifest-corruption + `.bak`.
- relay: **the entire auth layer is untested** (tests run trusted-local loopback with hl-auth pointed at a dead port → `isOwner`/Origin/CSWSH never execute); two-muxd race; dead-host detection; 800-overflow drop; backpressure; resize/pin/PIN_HOLD; autoheal tick.

## 7. Cross-cutting themes (for when we plan)

- **Security blast radius is the PC itself** (arbitrary shell as Ahmed). Two independent RCE vectors found: relay CSWSH (needs Origin check) + muxd loopback port (needs token/origin). The auth story is entirely on the VPS + cookie policy today.
- **The #1 user complaint (black terminal / dropped output) has a concrete code candidate** (relay H2 overflow drop) that no one has connected to it yet — that's the highest-leverage correctness lead.
- **State-truth is still regex-based** despite muxd now owning the pty and being able to see the child process tree — the "trustable status" ask and the D4 fragility are the same fix.
- **Two committed-but-not-live changes** (muxd log-writer + bounded local scrollback) are waiting on a daemon restart that keeps getting deferred to avoid killing sessions — restart discipline itself is an open ops question.
