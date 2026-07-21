# Multiplex Full Review — Architecture, Reliability, and the Path to Zero Downtime

*2026-07-02. Harsh by request. Every claim below is backed by forensics gathered on this system —
exit codes, journal entries, ping captures, reboot history — not vibes.*

---

## 0. Executive verdict

**The system is built upside down.** The thing you care about (a claude/codex agent, hours into a
goal) is the *most fragile* object in the architecture: its life depends on an unbroken chain of
SIX links — browser → nginx → node relay → **VPS tmux** → **ssh over two Wi-Fi hops** → PC ConPTY →
agent. Break ANY of the middle links and the agent **dies** — not "disconnects," dies. A local
terminal session has ONE link (the PC itself). That is the standard you asked for, and the current
design cannot meet it by patching, because the defect is ownership: **the VPS owns your sessions,
and the VPS is a dual-boot desktop on Wi-Fi that hard-reboots.**

Proof this is not theoretical — all from the last 48 hours:

| When | What happened | Evidence |
|---|---|---|
| Jul 1 ~22:05 | Interactive codex died at idle | Pane fell to `PS>`; no crash, no kill logged |
| Jul 1 01:13:45 | codex died the *instant* the ssh pipe dropped | Exit code `0xC000013A` (console hang-up) at the exact second of `RunSsh failed: The pipe is being closed` |
| Jul 1 | 3 sessions sat silently dead at a **VPS bash prompt** | Old watchdog sent PowerShell resume to bash — unrecoverable by design |
| Jul 2 02:14 | **VPS rebooted → tmux server gone → ALL 10 sessions annihilated** | `last -x`: 2 reboots in 24h, `still running` wtmp entries = unclean shutdowns |
| ongoing | VPS→PC LAN RTT 8–268 ms, jitter 45 ms, ssh calls failing back-to-back | 30-packet ping capture; two consecutive `ssh` failures observed live |
| 14 days | multiplex-app unit logged 46 start/stop/fail lines | journalctl |
| Jul 2 02:37 | **PC reboot changed its DHCP IP (.146 → .154) → every ssh-back "connection refused"** — recovery structurally impossible until a human edited `~/.ssh/config` | Happened live *during this review*; `Host win` hard-codes the IP |

The agent processes themselves are innocent: zero codex crash/WER events in the whole hunt. Every
death traced to the environment we built around them.

---

## 1. Current architecture (what actually exists)

```
[phone/desktop browser]
   │ HTTPS/WSS (hl-auth owner gate)
[nginx on VPS] ── /multiplex/ → 127.0.0.1:7682
   │
[node server.js — ONE process]
   ├─ express static (index.html / projects.html)
   ├─ /ws — node-pty → `tmux attach -t <name>`     ← VPS tmux OWNS the session
   ├─ /api/sessions — execSync tmux + pane-regex state dots
   ├─ autoheal watchdog (12s tick, types into panes)
   ├─ app-command queue (kill/rename/transcript/addtocollection/fetchfile)
   ├─ uploads store (phone → VPS → app scp's to PC)
   └─ projects.json (desktop app pushes its collections while open)
   │
[tmux session] runs `ssh -t win || exec bash -l`
   │            ← Wi-Fi hop 1 (VPS wlp3s0; ethernet port DOWN)
[PC sshd] → PowerShell → ConPTY → claude.exe / codex.exe
                ← Wi-Fi hop 2 (PC adapter; power-save fix pending reboot)
[desktop app + headless bridge] poll the queue over their own ssh; scan/kill local processes
```

**Failure-domain table — today:**

| Failure | Blast radius today |
|---|---|
| VPS reboot/crash | **Every session killed** (tmux gone). Happened Jul 2 02:14. |
| VPS Wi-Fi blip > keepalive | **Agent killed** (ssh ConPTY hang-up). Proven `0xC000013A`. |
| PC Wi-Fi blip | Same — **agent killed**. |
| node server restart/deploy | All clients drop (reconnect works; tmux survives — KillMode=process) |
| Desktop app closed/crashed | add-to-collection, uploads-to-PC, rename, transcript, local kill: **all dead** (GUI is crash-looping: `0xc000027b` XAML, unresolved) |
| PC reboot | Sessions die (agents live there — inherent), recovery is manual/watchdog |
| Browser refresh/phone sleep | Fine (reconnect + clobber-guard) — the ONLY failure that's properly contained |

One row is acceptable (PC reboot). Five are not.

---

## 2. The core defects, ranked

### D1 — Inverted session ownership (CRITICAL, the root)
The agent's controlling terminal is a **ConPTY owned by an ssh session originating on the VPS**.
The kernel closes that console the moment the ssh dies → Windows delivers console-hangup → agent
exits `0xC000013A`. This is why "even fresh sessions die," why VS Code sessions don't (their
console is local), and why no codex config will ever fix it. **The session must be hosted on the
PC** — the machine the agent runs on — and the VPS must become a *viewer* that attaches/detaches.
Exactly like a local terminal + optional remote attach. This single inversion converts both Wi-Fi
hops and the entire VPS from "session killers" into "display blips."

### D2 — The VPS is treated as reliable; it is measurably the least reliable machine (CRITICAL)
It is a dual-boot desktop on **Wi-Fi with its ethernet port unplugged** (`enp2s0 DOWN`), rebooted
twice in 24h with unclean-shutdown wtmp signatures, load 1.2–1.8 at idle, hosting a dozen other
containers. Yet it holds: every tmux session, scrollback, uploads, projects.json, autoheal state,
and the only copy of the relay. Everything session-critical must move off it or be reconstructible
from it in seconds.

### D3 — Recovery is "re-type the resume command," not "reattach" (HIGH)
Autoheal (even after my rewrite) can only *re-run* `codex resume` — a **new process**: in-flight
turn lost, confirmation prompts left on screen ("Enter to confirm" seen on 2 of 3 recoveries),
goal loops need re-arming. With PC-hosted sessions, the same failures need **zero recovery** —
the process never died; you just reattach the viewer.

### D4 — State detection by regex-ing pane pixels (MEDIUM, chronic)
green/yellow/red dots, autoheal death detection, "is a goal active" — all parse the TUI footer text
(`esc to inter`, `gpt-5.5`, `Pursuing goal`). Every codex/claude UI update can silently break the
watchdog and the dots. It already mis-fired once (the 45s-frozen heuristic Ctrl-C'ing live turns —
the "interrupted with the resume prompt in the prompt box" bug). Process-level truth (is the agent
process alive; is it consuming CPU; ConPTY title) must replace text heuristics wherever possible.

### D5 — The desktop app is a hard dependency for soft features (MEDIUM)
add-to-collection 400s → "unknown command" → nothing, because the GUI (which has the handler) is
crash-looping and the bridge (which runs) lacks the handler. Uploads can't reach the PC without the
app. Kill was "access is denied" until yesterday. A queue consumed by a crash-looping consumer with
no dead-letter surface is not a design, it's a hope. The server itself has an ssh route to the PC
(`win`) — most of these don't need the app at all.

### D6 — Sizing/pinning authority is wrong (MEDIUM — the mobile complaint)
Current: one shared size per session; `auto` = **widest connected viewport wins**; "pin" = cycling
through *connection ids*. Consequences:
- A desktop tab left open in another room (or backgrounded) keeps the phone panning a 200-col
  window forever. The phone user is ACTIVE; the desktop is a ghost. Wrong authority.
- Pin is bound to a connection id, so a Wi-Fi blip → reconnect → **new id → pin silently lost**,
  session snaps back to auto/widest.
- Cycling to find your own device is guesswork from the phone.
**Required semantics (your words): last pinner is authoritative.** Pin = "size to THIS device,"
sticky across reconnects (device identity, not connection identity), until someone else pins or
un-pins. Auto should weigh only *recently active* clients (input or visible in last N min), not
every zombie socket.

### D7 — Zero observability (MEDIUM)
No health endpoint, no latency/drop counters, no alerting, no status surface in the UI. Every
failure so far was discovered by you, mid-work, as lost sessions — then reconstructed by ad-hoc
forensics (we had to build a kill-attribution monitor to even learn the exit code). A system with
this many moving parts must announce its own failures.

### D8 — Operational hygiene (LOW each, real in sum)
- Node **18** (EOL) runs the relay.
- No tests of any kind on server.js; every change is deploy-and-pray (the `node --check` gate I
  added to deploys is the entire QA story).
- `execSync` tmux calls block the event loop (band-aided with 1.2s timeouts + caches).
- projects.json / autoheal.json / uploads-meta.json: no backup, VPS-only, rewritten with bare
  `writeFileSync` (torn-write risk on those unclean reboots).
- ws auth is checked once at connect; the 60s owner cache means a revoked session lingers.
- No systemd sandboxing (NoNewPrivileges, ProtectSystem) on a service that shells out.

---

## 3. Smaller issues & paper cuts (fix-with-the-rewrite list)

| # | Issue | Note |
|---|---|---|
| S1 | Mobile can't scroll codex history when *live* | Alt-buffer wheel-forward hack works but is fragile; PC-hosted tmux gives REAL scrollback (`copy-mode`) for free |
| S2 | "Enter to confirm" left on screen after auto-resume | Watchdog should optionally answer known-safe resume confirms, or surface "needs attention" in the tab |
| S3 | Tab-vanish clobber pattern | Fixed in index.html; the same clobber risk exists in projects.html re-renders |
| S4 | Image paste requires the app for the PC hop | Server can scp to the PC itself over `win`; drop the app dependency |
| S5 | Unread/activity badges reset on server restart | `read` state is client-local but `activity` comes from tmux — gone after VPS reboot |
| S6 | `SAFE()` name collisions | Two chats titled the same → same mux name → attach to the wrong session |
| S7 | No "session needs attention" state | Dead / at-confirm / gave-up states all look like "red dot" or nothing |
| S8 | Copy/paste | Copy overlay shipped (works); paste of TEXT >~4KB into codex can still choke the pty — chunk it |
| S9 | autoheal gave-up latch only clears on healthy | After 5 failed recoveries it stays silent forever; needs UI surfacing + manual retry button |
| S10 | Deploys drop every ws client simultaneously | Acceptable (auto-reconnect), but a `SIGTERM` drain + client "server restarting" toast would make it invisible |

---

## 4. Target architecture — "local terminal first, VPS optional"

```
[PC — owns ALL session state]
  muxd (session host):  tmux inside WSL2  (or a node-pty ConPTY daemon if WSL interop fails)
    ├─ session "cosmo-fix": claude.exe   ← console owned LOCALLY. Network can vanish; process lives.
    ├─ session "venpod":    codex.exe
    ├─ local attach:  Windows Terminal → `wsl tmux attach -t venpod`   (true local-terminal parity)
    ├─ local watchdog: process-exit → immediate resume (goal-aware), no network in the loop
    └─ outbound WSS ──► VPS relay (auto-reconnect, exp backoff, resumable)

[VPS — stateless viewer/relay, allowed to die]
  nginx + hl-auth (unchanged)
  relay: pairs browser ws ↔ PC host ws; serves the static UI; NO tmux, NO session state
  degraded mode: if PC host is unreachable → offer plain `ssh win` panes (today's mode) + banner
```

**Failure-domain table — target:**

| Failure | Blast radius (target) |
|---|---|
| VPS reboot/crash | Display outage only. Sessions untouched. Browser reattaches when relay returns; scrollback intact (lives in PC tmux). |
| Either Wi-Fi blip | Display freeze seconds; **agent never notices**. |
| Relay deploy | Same as above — invisible with drain+reconnect. |
| Desktop app dead | Nothing session-related affected (app = collections sync only). |
| PC reboot | The one real outage: muxd autostarts, recreates sessions from its manifest, resumes agents, re-announces to relay. Minutes, automatic. |

That table **is** the "0 downtime" definition, stated as testable invariants:
1. **Kill the Wi-Fi for 60s mid-turn → the turn completes anyway.**
2. **Reboot the VPS → every session survives, scrollback intact, reattach < 30s after boot.**
3. **`systemctl restart multiplex-app` → no session dies, clients auto-reattach < 5s.**
4. **Close the desktop app forever → terminals work 100%.**
5. **Reboot the PC → all armed sessions are resumed without a human, goals re-engaged.**
Each phase below lands one or more of these, and each is verified by literally performing the
failure and watching the invariant hold (loops-style: the verifier is proven before the loop).

Key design decisions:
- **WSL2 tmux as the session host** is the cheap, battle-tested path: tmux owns the pty locally;
  Windows binaries (`claude.exe`, `codex.exe`) run fine under WSL interop, keep running across
  ssh/network loss, and `wsl tmux attach` from Windows Terminal gives local parity. **Phase 1 is a
  spike that proves codex+claude TUIs are healthy under WSL tmux ptys** (interop pty quirks are the
  risk — if it fails, fall back to a node-pty ConPTY daemon: more code, same ownership win).
- **Outbound connection from PC → VPS** (like the CameraRoom S10 anchor): no inbound port on the
  PC, survives NAT/firewall, reconnects itself. The relay pairs it with browsers.
- The VPS keeps nginx + hl-auth exactly as-is (they've not been the problem).

---

## 5. Sizing/pinning redesign (D6, concrete)

- Each browser gets a persistent `deviceId` (localStorage) + friendly label ("S24", "desktop").
- Pin action = `{deviceId, cols, rows, at}` stored **per session** server-side (survives reconnects
  and relay restarts). **Last pin wins — whoever pinned most recently is authoritative**, from any
  device, until un-pinned (one tap back to auto).
- Auto mode = widest among clients **active in the last 3 min** (input, scroll, or visible tab per
  heartbeat). Ghost/backgrounded clients are ignored; a solo phone gets a phone-sized window
  immediately.
- UI: the size chip shows *who* owns the size ("pinned · S24 · 47×110"); tapping it pins to *this*
  device; long-press → auto.
- Same-size no-op guard: reattach must not trigger a resize storm (tmux redraw flicker).

---

## 6. Phased plan (each phase independently shippable, verified before the next)

**P0 — Stop the bleeding (now, hours)**
- [x] Watchdog: shell-prompt death detection, bash-vs-PS recovery, goal-aware resume (shipped Jul 1; recovered 3 dead sessions on first tick)
- [x] Wi-Fi power-save off (VPS persistent; PC live + reboot-pending)
- [ ] **Boot-recreate**: on relay start, recreate every session in `autoheal.json` (`ensureSession`)
      → watchdog reconnects ssh → resumes agents. Converts a VPS reboot from "10 chats gone" to
      "auto-restored in ~1 min." *(Validated manually Jul 2 02:35–02:40: recreated the 7 armed
      sessions after the wipe → watchdog reconnected + resumed all 7; both codex goals re-engaged.)*
- [ ] **★ TELUS AP client-isolation (found Jul 2, during the Wi-Fi drill)**: the PC sometimes roams to
      BSSID 3c:f0:83:49:53:ce where INTERNET works but LAN PEERS are unreachable (client isolation) —
      instant "server not responding" + dead ssh-backs; BSSID 3c:f0:83:93:6e:cb is healthy. HUMAN-GATE:
      disable client/AP isolation on that TELUS pod (or remove the pod). The hosted path already rides
      it out via the public wss fallback (proven live at 23:42:19 — failover took 1s).
- [ ] **Pin the PC's address**: DHCP reservation on the router (or a static IP on the PC), and/or
      hostname-based `HostName` for `win`. The Jul 2 incident proves a plain reboot can silently
      orphan the entire fleet behind a stale hard-coded IP.
- [ ] `/api/health` (uptime, tmux ok, PC-ssh RTT, last watchdog actions) + a status dot in the UI
- [ ] **You:** plug the VPS ethernet cable in; reboot the PC once *(reboot done Jul 1 — it's what
      exposed the DHCP defect)*. Ethernet still pending — cheapest reliability on the menu.

**P1 — Prove PC-hosted sessions ✅ DECIDED (Jul 2)**
- ❌ WSL2 tmux path FAILED: claude.exe under WSL interop exits without rendering (Windows TUIs need
  a real Windows console; WSL gives them pipes). The anticipated decision gate fired.
- ✅ PIVOT PROVEN: **pywinpty ConPTY host** — spike showed BOTH claude and codex TUIs render, echo
  input, resize, and stay alive in a locally-owned ConPTY (codex TUI up at t+1.0s).
- Gotchas for the record: the host MUST run under `python.exe` (a console process) — under
  `pythonw.exe` the ConPTY spawn dies natively with no traceback; and `ws` cannot host two
  path-bound WebSocketServers on one http server (the non-matching one 400s the handshake —
  use `noServer` + manual upgrade routing).

**P2+P3 — Ownership flip + outbound link ✅ LANDED (Jul 2, verified)**
- **muxd** (`C:\Users\Ahmed\muxd\muxd.py`, Python+pywinpty): owns a ConPTY per session, ring-buffer
  scrollback (~800KB), sessions.json manifest → muxd/PC restart recreates + resumes every session,
  local self-heal for dead shells, scheduled task `MuxdSessionHost` (logon + auto-restart).
- Outbound WSS to the relay (`/host`, token in /etc/multiplex-app.env; LAN direct ws://…:7682 with
  ufw scoped to 192.168.1.0/24, public wss fallback through nginx) — no inbound port on the PC.
- Relay bridges browsers ↔ muxd: hosted create/attach/kill/resize/tail, scrollback replay before
  live bytes, watchdog + goal-aware autoheal extended to hosted sessions, tmux-twin ghost-kill +
  boot-recreate guard (no double-resume), legacy tmux path intact as degraded fallback.
- **VERIFIED against the invariants:**
  - Invariant 3 (relay restart): ticker session ran **continuously** through `systemctl restart
    multiplex-app` — 141s of ticks, max gap 1.12s.
  - Invariant 1 (Wi-Fi cut): **51-second total Wi-Fi blackout** → muxd pid unchanged, both session
    shells unchanged, 245 ticks / max gap 1.12s spanning the outage, hosted codex alive at its
    composer. The agent never noticed the network died.
  - Hosted end-to-end: create→attach (scrollback+live)→type into codex composer→kill, all through
    the relay, all green. Legacy sessions rode the same outage out via keepalives.
- New sessions are hosted automatically when the link is up; pre-flip tmux sessions migrate on
  their next relaunch.

**P4 — Sizing/pinning rewrite** (§5) — device-identity pins, last-writer-wins, active-client auto.

**P5 — De-app the queue**
- Server-side scp for uploads/paste (no app in the loop);
- addtocollection → durable dead-letter + visible per-command status; bridge gets the handler
  (and the GUI XAML crash gets its own debugging session — it's masked half the queue for days).

**P6 — Ops floor**
- Node 22 LTS; smoke battery for server.js (session CRUD, ws attach, auth deny, health) run pre-deploy;
- atomic writes (`tmp+rename`) + nightly tar of VPS state files to the PC;
- systemd hardening; drop counters + ntfy push on: watchdog gave-up, relay↔host link down > 2 min,
  health probe fail. **PC reboot drill** → invariant 5.

---

## 7. What I got wrong along the way (so it's on the record)

- Called the codex deaths "fixed" twice (Cloudflare discovery, then `remote_control_enabled`) before
  the kill-attribution monitor produced the real answer (`0xC000013A` console hang-up). The lesson is
  baked into this plan: **no reliability claim without a reproduced-failure verifier** (§4 invariants).
- The first autoheal design Ctrl-C'd live turns on a 45s-quiet heuristic — it *caused* the exact
  symptom it existed to fix. Replaced with shell-prompt-only detection; the target design removes
  the text-heuristic class entirely.

---

*Bottom line: patch nothing more. Land P0 this week for survivability, run the P1 spike, then flip
ownership (P2/P3). Everything after that is polish on a foundation that finally matches how you
actually use this — a local terminal that happens to have remote viewers.*
