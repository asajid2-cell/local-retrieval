# Multiplex — Improvement Plan (2026-07-06)

*Companion to `AUDIT_CAPTURE.md`. The audit is the evidence pile; this is the decision about what
to do with it. IDs below (C1, H2(relay), pain #1, etc.) refer straight back to the audit sections so
every item is traceable to its evidence.*

---

## The three facts that set the order

1. **There is only ONE currently-live RCE vector, and it's the muxd loopback port (H2).**
   The scarier-looking one — relay CSWSH (relay-C1) — is **defused today**: verified `2026-07-06`
   that hl-auth sets `hl_session` with `sameSite:"lax"` and a host-only domain
   (`hl-auth/src/routes/authRoutes.js:15`, `config.js:24`). A Lax cookie is not sent on a
   cross-site WebSocket handshake, so a third-party page cannot open `/ws` as the owner. relay-C1
   is real *architecture debt* (one config flip to `SameSite=None` re-arms it) but not a live hole.
   The muxd loopback (`ws://127.0.0.1:7699`, no token, no origin check) **is** live: any webpage
   open in any browser on the PC can spawn PowerShell as Ahmed. That's the security P0.

2. **The #1 user complaint has a concrete, relay-only, ship-anytime fix nobody has connected to it.**
   "Web terminal black / gaps in output" (pain #1, raised ≥6×, highest emotion) lines up with
   relay-H2: the attach path drops a >800-frame burst *without* flushing or sending CLEAR
   (`server.js:1048`), so the viewer loses output and stacks live bytes on a stale screen. This is
   the single highest-leverage correctness lead in the whole audit, and it needs **no muxd restart**.

3. **Every muxd fix needs one restart that kills live sessions — so spend that restart once.**
   C1 (crashed prod), C2, H5, plus the two already-committed-but-not-live patches (log-writer,
   bounded scrollback) all require restarting the daemon, which tears down every ConPTY it owns.
   On Windows those handles can't survive a restart, and manifest-resume = a *new* process (loses
   the in-flight turn, per full-review D3). So the plan **batches all muxd changes into a single
   coordinated restart** at a checkpoint Ahmed picks — not a drip of one-fix-one-restart.

These three facts produce two parallel tracks that don't block each other: **Track A ships from the
VPS with zero session risk; Track B accumulates until one restart.** Do them concurrently.

---

## Track A — Relay-only, deploy anytime, no session risk (START HERE)

Everything here is `server.js` / `public/*`, deployable behind the existing drain-less restart with
no muxd involvement. Order is by leverage.

- **A1 — Fix the burst-drop (relay-H2). THE #1 user pain.**
  On attach overflow, flush `c.q` and send a CLEAR (`\x1b[3J\x1b[2J\x1b[H`) before going live
  instead of `continue`-ing onto a stale screen (`server.js:1048`). Pair with a per-attach
  scrollback token so a late `sb` reply can't paint over live content (audit relay A2-#3 ordering).
  **Verify:** attach to a chatty session, force >800 queued frames, confirm screen is clean and
  complete on both fresh attach and reconnect. This is the item most likely to make Ahmed say "the
  black terminal is gone."

- **A2 — Close the auto-spawn footgun + pre-arm the CSWSH defense (relay-H1, relay-C1).**
  `server.js:1182` `!legacyBlocked` is dead-true → any `/ws?session=<anything>` spawns a PC shell.
  Make unknown sessions attach-only (they already have a `1013` refusal path for dormant). Then add
  an `Origin` allowlist check on the `/ws` upgrade — cheap, and it permanently removes the "one
  cookie-policy flip from RCE" exposure regardless of what hl-auth does later.
  **Verify:** `/ws?session=bogus` refuses without creating a session; cross-origin handshake with a
  forged Origin is rejected pre-101.

- **A3 — Detect a dead muxd link + add backpressure (relay-H4, relay-H3).**
  Add a `pong` handler and terminate-on-missed-pong (the 20s ping at `server.js:1063` has no
  liveness consequence today), so a black-holed muxd stops reporting `hostUp()`. Add a
  `bufferedAmount` check on both viewer and host legs (`server.js:1049,1057`) so a slow phone on a
  weak link can't grow relay memory unbounded on the shared VPS.
  **Verify:** kill muxd's socket mid-stream → relay flips to degraded within one ping cycle instead
  of hanging attaches into a rotting buffer.

- **A4 — Test the auth layer (biggest test gap).** The relay's entire auth path is currently
  untested — `relay.test.js` runs trusted-local with hl-auth pointed at a dead port, so
  `isOwner`/Origin/CSWSH never execute. Add hermetic tests that exercise a real cookie gate + the
  new Origin check. This is what lets A2 land safely and stay landed.

---

## Track B — The one coordinated muxd restart (batch, then spend the restart once)

Accumulate all of these on the PC repo, compile-check, then do **one** restart at a checkpoint with
no critical in-flight agent turn. Sequence inside the batch by risk.

- **B1 — Lock the ring (C1).** `_reader` mutates the deque while the asyncio loop iterates it in
  `scrollback`/`tail_text` → `RuntimeError: deque mutated during iteration`, which **already tore
  down the relay link in prod (07-05 16:30)**. Snapshot under a lock. This is the one confirmed
  production crash — it leads the batch.

- **B2 — Supervise the pump tasks (C2).** `pump_out`/`pump_status`/`flush_out`/`self_heal_tick`/
  `local_serve` are bare `create_task` with no exception handler. If any throws outside the
  `async for` body the task dies silently while the link stays up → output/status freeze while
  input still flows (`flush_out` death freezes output for *all* sessions permanently). Wrap each in
  a supervisor that logs + restarts with backoff. Without this, B1's whole failure class stays
  invisible when it recurs in a new form.

- **B3 — Token + Origin on the loopback port (H2). The live RCE.** Require a token in the first
  frame on `ws://127.0.0.1:7699` and reject any request carrying a browser `Origin`
  (`muxd.py:733`). Trivial diff; it is the security P0 and the thing that should *motivate*
  scheduling this restart sooner rather than later.

- **B4 — Stop the thread/ring leak (H5).** One line: `self.wq.put(None)` in `kill()`
  (`muxd.py:233`) so the `_writer` thread's `wq.get()` unblocks and the ring (≤800KB) is released
  per kill/relaunch instead of pinned forever.

- **B5 — Bound `outq` (H1).** Cap/coalesce `outq` (`muxd.py:496`) so an hours-long relay outage
  with armed agents can't grow memory unbounded or flood stale replay on reconnect. The 2MB pending
  cap doesn't apply once chunked into `outq` today.

- **B6 — Go live on the two already-written patches.** The committed log-writer thread and bounded
  local scrollback have been waiting on exactly this restart. They ride along for free.

- **B7 — Manifest async I/O (M2).** `manifest_save()` does sync file I/O on the loop — the same
  stall class the log-writer fix already proved fatal (30–63s freezes). Move it off-loop while
  you're in here.

**Restart discipline (the standing ops answer):** muxd restart is destructive by nature on Windows,
so (a) batch — never restart for a single fix; (b) pick a checkpoint with no critical in-flight
turn; (c) let the manifest auto-resume the rest; (d) announce it. A non-destructive restart (hand
ConPTYs to a broker that survives) is a real project, noted as a stretch under Track F, not a
prerequisite for any of the above.

---

## Track C — Mobile & input correctness (frontend, both paths)

These are the daily-friction pains behind #1. Independent of A/B; can interleave.

- **C1 — Escape-sequence / DA-reply injection (pain #4).** Raw `[?1;2c`, `]10;rgb:…` leaking into
  the input box, on web-mobile *and* local. Terminal query replies (DA / OSC 10/11) are escaping
  into the input path. Intercept and swallow these replies at the muxctl/xterm boundary instead of
  forwarding them as typed keys. This is a correctness bug with a clean root cause, not a UX
  preference — highest of the three to fix.

- **C2 — Mobile input duplication (pain #2).** "Delete one thing and it deletes everything." A
  dedup harness exists (`index.html:1148-1226`) but isn't confirmed fixed, and Ahmed rejected the
  separate-input-box workaround — he wants the root cause. Reproduce on a real device against the
  IME/composition path, not the harness.

- **C3 — Mobile scroll clamp above the input bar (pain #3).** Can't reach the bottom bar. Layout
  fix; verify with the ui-craft viewport battery across mobile states so it doesn't regress.

---

## Track D — Trustworthy status (the feature ask that is also the D4 fix)

Pain #6 ("green/yellow/red is arbitrary, I want it trustable") and the chronic D4 fragility
(status by footer-regex, breaks on every codex/claude UI update) are **the same fix**, and muxd now
owns the pty so it's finally possible.

- **D1 — Process-truth status.** Have muxd report the child process tree with each status push: is
  `claude.exe`/`codex.exe` actually alive, consuming CPU, at what ConPTY title — and let the dots +
  watchdog consume process truth instead of parsing TUI footer text. Add real
  finished / working / needs-attention states (S7) so "dead", "at a confirm prompt", and "gave up"
  stop all looking like one red dot. Ends the "a TUI redesign silently breaks the watchdog" class
  for hosted sessions.

---

## Track E — Features (wishlist, after the floor is solid)

Real asks from the transcript, sequenced last because they build on a trustworthy foundation.

- **E1 — Past-sessions browser.** Scan `~/.claude/projects` + `~/.codex/sessions`, resume any into
  muxd. `allChats` today is a 500-cap partial projection — add pagination + server-side search
  instead of treating collection membership as truth.
- **E2 — Transcript viewer from the sessions list** ("see which is which" without attaching).
- **E3 — Web scrollback depth (pain #5, design tension).** The 60000-byte bounded attach is a
  perf tradeoff; offer an explicit "load full history" fetch from muxd's ring on demand so the
  default stays fast but the ceiling is gone.
- **E4 — Start-mode choice.** Visible-local+web vs headless-until-`mux name`.
- **E5 — Retrieval-app fork bug (pain #7).** Forked chat resumes the pre-fork session id; needs
  correct id + reroll/reassign. Scope is unclear in the audit — **triage first** (reproduce, find
  the id-assignment site) before committing to a fix.

---

## Track F — Security & ops hardening (leftovers, batch when nearby)

- H3(muxd): plaintext LAN is the *preferred* path and the token rides in the URL query (lands in
  VPS access logs, sniffable on LAN, never rotates). Move the token to the first frame; consider
  WSS on LAN or accept the risk explicitly and document it.
- H4(muxd): the PTY writes any relay `i` frame with no PC-side authentication — the whole RCE gate
  lives on the VPS. Long-term, sign/authenticate frames PC-side so VPS compromise ≠ PC shell.
- Ops floor (from full-review P6 / state-and-next N3): move the m1–m4 verifiers into `tests/` with
  a `run-all` pre-deploy gate; nightly backup of pins/autoheal/projects to the PC; **ntfy alert**
  on host-link-down > 5 min / watchdog-gave-up (a degraded dot only helps if someone's looking);
  SIGTERM drain + "restarting" toast on relay deploy.
- Stretch: non-destructive muxd restart (ConPTY handoff to a survivor broker) — kills the restart
  discipline constraint entirely. Big; only if restart cadence becomes a real pain.
- Clean up the VPS drift the audit flagged: dirty `package.json`/lock, `server.js.bak-*` litter.

---

## Recommended first moves

1. **Ship A1 now** — it's the highest-emotion user pain, relay-only, zero session risk. If nothing
   else happens this week, this one.
2. **Ship A2 + A3** right behind it — closes the auto-spawn footgun and gives the relay real
   liveness/backpressure, still no restart.
3. **Assemble the Track B batch** (B1–B7) in the PC repo, compile-checked and reviewed, then do the
   single coordinated restart at the next natural checkpoint — B3 (the live RCE) is the reason not
   to let that checkpoint drift far.
4. Then C (mobile correctness) and D (trustworthy status), which is where the daily experience
   actually gets better. E and F follow once the floor holds.

**Two things to verify before building, not assume:** the exact reproduction of pain #2 (mobile
dup) on a real device, and the scope of E5 (retrieval fork). Both are marked UNCLEAR in the audit.
