# planned-work.md — Multiplex: parity sweep, mirroring/pinning redesign, I/O polish, feature restoration

*2026-07-02. PLAN ONLY — nothing here is implemented yet. Companion to `full-review.md` (the
architecture review; P2/P3 ownership flip is LANDED and verified — this doc plans what's next).*

---

## 0. Scope

1. **Parity & correctness sweep** of the whole system (browser UI, relay, muxd) — every gap and
   bug found by auditing the shipped code, each with a fix and a verifier.
2. **Mirroring/pinning redesign** — device-identity pins, "prefer this device" = that size for
   everyone, auto stays auto; mobile becomes a first-class dedicated terminal when preferred.
3. **Desktop keybar** — one button to show all the mobile buttons on desktop.
4. **Copy / paste / input / streaming polish** across both paths.
5. **Feature restoration** on the hosted path (rename, local attach, previews, dead-session attach).

Non-goals here: P5 (de-app the upload/collection queue), P6 (node 22, alerts, backups), the GUI
XAML crash — tracked in full-review.md, unchanged.

---

## STATUS — ALL FIVE MILESTONES LANDED + VERIFIED (2026-07-02)

Every milestone was deployed to the live stack and proven with a scripted verifier (in
`scratchpad/mux/m*_verify.js`), then re-run as a regression sweep against the final build.
Committed to the two local git repos (VPS `~/multiplex-app`, PC `C:\Users\Ahmed\muxd`).

- **M1 (correctness)** ✅ — 20× create+attach burst → **zero tmux twins**; reconnect replay is
  CLEAR-prefixed → **no dup screen**; size re-asserts on muxd reconnect.
- **M2 (device pinning)** ✅ — pin follows the DEVICE (survives reconnect + relay restart via
  `pins.json`), **last-writer-wins**, auto weighs only recently-active viewers (ghost-exclusion
  proven on a short-window relay); size chip = tap pin-this-device / long-press pin-another.
- **M3 (desktop keybar + I/O)** ✅ — `⌨` toggle brings the full keybar to desktop (compact);
  6000-char paste survives intact (chunked ConPTY writes); coalesced output streams smoothly;
  TCP_NODELAY on both socket legs.
- **M4 (hosted parity)** ✅ — rename (muxd op + migrates autoheal/pin state, A2#8), deep on-demand
  tail (61 rows vs the ~11-line cache), attach-to-dead **revives** the shell, `muxctl ls`/`attach`
  local terminal parity, `PC` badge on hosted tabs.
- **M5 (hardening)** ✅ — atomic state writes; constant-time `/host` token **rejected
  pre-handshake**; degraded health when the PC host is down with armed hosted sessions; muxd
  per-session output backpressure (~2MB) + auto-return to the LAN relay when it recovers.

**Invariant held throughout:** a hosted session stays **continuous across a relay restart**
(26 ticks, max gap 1.02 s) — the ownership flip never regressed.

Two plan items intentionally not coded (documented instead): **A2 #11 muxd-on-logoff** — behaves
like a PC reboot (the manifest auto-resumes sessions at next logon), so it's no worse than the
already-handled reboot case; running headless would risk ConPTY spawning without an interactive
desktop and needs a real logoff spike. **A2 #10 cross-path collision** — already prevented by the
routing + hello ghost-kill + `hostedHas` guards (M1 proved zero twins), so no separate reject path
was added.

---

## A. Parity & correctness sweep

### A1. Parity matrix (legacy tmux+ssh vs PC-hosted muxd)

| Feature | Legacy | Hosted | Verdict |
|---|---|---|---|
| Create session (web + API) | ✅ | ✅ | parity |
| Attach / mirror on N devices | ✅ | ✅ | parity |
| Scrollback on attach | tmux redraw | ring replay (~260KB) | parity-ish; see A2-#3 dup bug |
| Kill (tab menu, projects page) | ✅ | ✅ | parity |
| **Rename tab** | ✅ tmux rename | ❌ **500 error** (PATCH is tmux-only) | **GAP → E1** |
| Auto-resume watchdog (goal-aware) | ✅ | ✅ | parity |
| Boot recovery after wipe/reboot | boot-recreate | muxd manifest | parity (different owners, same outcome) |
| State dots (green/yellow/red) | 1.5s-fresh pane regex | ≤5s-stale muxd tail | parity-ish; see A2-#6 |
| Tail preview (long-press) | N lines requested | capped ~900 chars | **GAP → E4** |
| Unread/activity badges | tmux activity | muxd lastOut | parity |
| Attach to a DEAD session | ensureSession recreates shell | ❌ attaches to a corpse (input goes nowhere) | **GAP → E3** |
| Local terminal attach (no browser) | `tmux attach` on VPS | ❌ none | **GAP → E2** |
| Uploads / image paste to PC | ✅ (app-dependent) | ✅ (same) | parity (both wait on P5) |
| Shared sizing / pan | ✅ | ✅ | parity (both get the B redesign) |
| Visible "where does this run" | n/a | ❌ no badge | **GAP → E5** |

### A2. Correctness defects found in the shipped code (fix in this order)

1. **Optimistic-create race (data-loss class).** `POST /api/sessions` inserts the new hosted name
   into `hostSessions` optimistically; muxd's next 5s `sessions` push (sent BEFORE it processed
   the create, e.g. in-flight) **clears the map and wipes the optimistic entry** → the browser's
   imminent `/ws` attach routes LEGACY → creates a tmux twin → **two agents can resume one
   transcript**. Fix: `pendingCreates` map (name → deadline ~8s); `sessions` pushes may not evict
   a pending name; muxd already confirms with a post-create push that clears it.
   *Verify: scripted create+attach 50× with jittered status pushes → zero tmux twins.*
2. **Hosted reattach duplicates the screen.** On reconnect (`isRetry=true`) the client does NOT
   `term.reset()`, then the scrollback replay appends onto the stale screen → doubled/garbled
   content. Fix (server-side, one line): prefix every `sb` payload with `\x1b[3J\x1b[2J\x1b[H`
   (clear scrollback+screen+home). *Verify: attach, kill ws, reconnect → screen identical, no dup.*
3. **Late-scrollback ordering glitch.** If the 4s sbWait timeout fires (queue flushed) and the
   `sb` reply arrives AFTER, it's sent as if it were fresh output → screen replay lands on top of
   live content. Fix: per-attach sb token; drop an `sb` whose token was already flushed.
4. **Stale `st.cur` size after muxd restart.** Relay dedupes resizes via `st.cur`; a restarted
   muxd spawns ptys at default 140×40 but the relay thinks the old size is current → wrong size
   until a viewport changes. Fix: reset `st.cur` on host hello; muxd includes each session's
   (cols,rows) in its pushes and the relay re-asserts on mismatch.
5. **Sticky public-path fallback.** muxd stays on the wss/Cloudflare path forever once the LAN
   link blips (latency tax on every keystroke). Fix: when connected via fallback, probe the LAN
   URL every 3–5 min and migrate back on success. *Verify: drop LAN, watch failover; restore LAN,
   watch it return ≤5 min.*
6. **Hosted dots/watchdog run on a ≤5s-stale, 900-char tail.** Also `o` frames update `lastOut`
   but not the tail, so a chatty session's dot can be computed from old text. Fix: muxd includes
   tail in a lightweight push when output occurred since the last one (coalesced, ≥2s apart), and
   answers an on-demand `{t:'tail', s, lines}` request (also fixes E4).
7. **No output backpressure in muxd.** A session flooding output (build logs) while the link
   rides the slow public path grows `outq` unbounded → memory. Fix: per-session pending-bytes cap
   (~2MB) → coalesce chunks; the ring buffer already preserves history for reattach.
8. **Rename loses watchdog/pin state (BOTH paths, pre-existing).** `PATCH` renames tmux but not
   `_healOn`/`_heal` membership → an armed tab silently loses auto-resume on rename. Fix: rename
   migrates `_healOn`, `_heal`, and (new) pin records; hosted rename via E1.
9. **Health blind spot.** `/api/health` stays `ok:true` when the host link is down even if every
   armed session is hosted (= unreachable, unhealable). Fix: `degraded:true` when
   `!host.connected && hostedArmedCount > 0`; surface "PC host offline" in the status dot tooltip.
10. **Same-name collision after hello.** Ghost-twin kill runs only at hello; a tmux session
    created later with a hosted session's name coexists → attach routing prefers hosted, tmux twin
    idles invisibly. Fix: reject `POST` names that exist on the other path unless `force`, and log.
11. **muxd logoff death.** Scheduled task is logon-scoped: logging off Windows kills every hosted
    session (reboot ≠ logoff; reboot self-heals at next logon). Spike: task as "run whether user
    is logged on or not" — ConPTY without an interactive desktop needs proving. If it fails,
    document logoff as the one manual hazard. (PC reboot already covered by the manifest.)
12. **Minor:** `/host` token compare not constant-time; `pins`/`autoheal`/`projects` writes not
    atomic (tmp+rename); `muxd.crash` handle never rotated. Batch these.

---

## B. Mirroring & pinning redesign (the mobile complaint, spec'd)

**Semantics (user-specified):**
- **Pin = "prefer this device."** Tapping pin on the phone sizes the shared window to the PHONE
  for **everyone** (desktop mirrors/pans the phone-shaped window). Tapping pin on desktop does the
  reverse. **Last pinner is authoritative** — from any device, no questions asked.
- **Auto = auto.** No pin → the widest **recently-active** viewport wins. "Active" = tab visible
  OR input/scroll in the last 3 min (client heartbeats). A desktop tab sleeping in another room
  can no longer force the phone to pan forever.
- Pin survives reconnects, relay restarts, and muxd restarts — it belongs to the **device**, not
  the socket.

**Design:**
- **Device identity:** client generates a persistent `deviceId` (localStorage) + auto label
  ("Phone · Android/Chrome", "Desktop · Windows/Chrome"; user-editable in the help sheet). Sent
  in the `/ws` query and with `v` frames.
- **Server state:** `pins.json`: `{ session → { deviceId, label, at } }` (atomic writes). `targetSize`
  resolution: pinned device connected (any socket with that deviceId) → its viewport; pinned but
  absent → HOLD the pinned size for 10 min (grace for blips — do NOT snap sizes around), then
  auto; no pin → auto over active clients only.
- **Activity:** clients piggyback `{visible, lastInputAt}` on `v` frames + a 30s heartbeat; server
  marks clients active/ghost.
- **Protocol:** new client msg `P` = pin-to-me / `P0` = clear-to-auto (replaces blind `s` cycling;
  `s` kept as legacy no-op → cycles pin→auto→pin for muscle memory). `d` control frame gains
  `{pin:{label, mine}}` so every device can render who owns the size.
- **UI:** size chip shows `auto · 100×30` or `📌 Phone · 47×110`; **tap = pin to THIS device**,
  tap again (when mine) = back to auto; long-press = device list (pin any, or auto). Identical on
  mobile keybar and desktop header.
- Applies to hosted AND legacy sessions (sizing lives in the relay — one implementation).

*Verify: two real devices; phone pins → desktop shows 📌 Phone and pans within 1s; phone Wi-Fi
blip + reconnect → pin holds (new socket, same deviceId); relay restart → pin holds (pins.json);
auto mode + desktop backgrounded 3 min → phone gets native size without touching anything.*

---

## C. Desktop keybar ("see all the mobile buttons")

- Header toggle `⌨` (next to zen): force-shows the keybar on desktop — CSS
  `#app.keybar-on #keybar { display:flex }` overriding the `(hover:hover) and (pointer:fine)`
  hide; state in localStorage. Esc/zen do not clear it.
- Everything already in the keybar becomes desktop-usable: Sel/Copy/Paste, ⇲ size (→ new pin
  chip), sticky mods (Ctrl/Alt/Shift one-shots), arrows/Tab/Esc/Enter, F-keys, symbols, custom
  key-combo buttons.
- Desktop-specific rendering: keybar as a compact single row, no giant touch targets
  (`pointer:fine` variant heights), so it doesn't eat terminal rows.
- *Verify: ui-craft battery with keybar open at all 8 viewports; every button drives the terminal
  from desktop; toggle persists across reloads.*

---

## D. Copy / paste / input / streaming polish

**Copy (mostly shipped, finish it):**
- Keep: select→auto-freeze, Ctrl/Cmd+C selection copy, Select&Copy overlay (native selection).
- Add: overlay "Copy last command output" (parse from the last prompt marker); overlay content for
  hosted sessions sourced from muxd's ring (full history) instead of just the xterm buffer, via a
  `{t:'sb'}` fetch — phones get deep history without scrollback gymnastics.

**Paste (the real gaps):**
- **Chunked writes**: >4KB pastes stall/garble ConPTY input. Relay (legacy) and muxd (hosted)
  write input in ≤1KB slices with ~8ms gaps when a single `i` frame exceeds 4KB.
- **Bracketed paste**: wrap large client pastes in `ESC[200~ … ESC[201~` when the app has enabled
  bracketed-paste mode (xterm.js exposes it) so codex/claude treat it as one paste, not typed keys
  (no accidental Enter-submits mid-paste).
- **Mobile**: keybar Paste + compose dialog already exist; compose gains a "send as paste"
  (bracketed) toggle for multi-line prompts.
- *Verify: 100KB paste into codex composer lands intact, no premature submit, both paths.*

**Streaming/latency:**
- `socket.setNoDelay(true)` on every leg (browser-ws upgrade socket, /host socket, muxd side) —
  kills Nagle-induced ~40ms keystroke echo bumps, biggest win on the CF fallback path.
- muxd output coalescing: flush reader chunks on a ~12ms timer (one ws frame per flush instead of
  per 8KB read) — fewer frames, smoother phone rendering, less b64/JSON overhead.
- Client: `term.write` callback backpressure already handled by xterm; keep binary frames as-is.
- *Verify: keystroke echo RTT sampled before/after on LAN and fallback; target <60ms LAN, <180ms CF.*

---

## E. Feature restoration on the hosted path

1. **Rename** — muxd `{t:'rename', s, to}` op: renames session key + manifest entry (keeps pty);
   relay `PATCH` routes hosted→muxd, legacy→tmux; migrates `_healOn`/`_heal`/pins/read-state (A2-#8).
2. **Local terminal attach (parity with `tmux attach`)** — `muxctl.py attach <name>` + a loopback
   listener in muxd (localhost-only ws): raw-mode console client streaming the same pty (mirrors
   with web viewers, sizing joins the shared window as a device named "Local terminal").
   `muxctl ls` lists sessions. This restores the "it's just a local terminal" story end-to-end.
3. **Attach-to-dead recreates** — hosted attach to a dead session recreates the shell (and queues
   the manifest resume cmd) exactly like legacy `ensureSession`; the corpse's scrollback is
   replayed first so you can see how it died.
4. **Proper tail previews** — `{t:'tail', s, lines}` on demand (A2-#6) so long-press previews and
   the projects page match legacy depth.
5. **Hosted badge** — tabs and the projects page show a small `PC` chip on hosted sessions
   (`hosted:true` is already in `/api/sessions`); tooltip: "runs on your PC — survives network/VPS
   failures". Legacy sessions show nothing (default), so migration progress is visible at a glance.

---

## F. Sequencing (each milestone shippable + verified before the next)

| # | Milestone | Contents | Gate |
|---|---|---|---|
| M1 | **Correctness first** | A2-#1..#4 (races, dup-screen, ordering, sizes) | scripted create/attach/reconnect drills, zero twins/dups |
| M2 | **Pin/mirror redesign** | B (device pins, active-auto, UI chip) + A2-#8 pin migration | two-device drill incl. blip + restart persistence |
| M3 | **Desktop keybar + I/O polish** | C + D (chunked/bracketed paste, noDelay, coalescing) | battery + 100KB paste + RTT samples |
| M4 | **Hosted parity pack** | E1–E5 + A2-#6 tails | rename/attach-dead/muxctl/tail/badge checks |
| M5 | **Hardening leftovers** | A2-#5,#7,#9..#12 (fallback return, backpressure, health, atomic writes, logoff spike) | failure drills re-run (invariants 1 & 3 stay green) |

Estimated shape: M1 small, M2 the big one, M3–M5 medium. All relay/UI/muxd work commits to the
two local git repos created Jul 2 (VPS `~/multiplex-app`, PC `C:\Users\Ahmed\muxd`) — local only.

## G. Standing verification (every milestone)

- The two landed invariants re-run after each milestone: **relay restart** and **50s Wi-Fi cut**
  with a live ticker + idle codex — max output gap must stay ~1s (no regression of the flip).
- ui-craft destruct battery on index.html states: keybar open (desktop), pin chip states, copy
  overlay, dialogs — exit 0 across the 8 viewports.
- Legacy fallback smoke: with muxd stopped, tabs still open via tmux+ssh (degraded mode intact).
