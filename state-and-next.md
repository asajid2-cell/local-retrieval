# Multiplex — State of the System & What's Next

*2026-07-02, after the ownership flip (P0–P3) and the parity/hardening pass (M1–M5). Companion to
`full-review.md` (the original harsh audit) and `planned-work.md` (the sweep + milestones, all landed).
Grades are honest, evidence-cited, and compared against the review's own bar: "a local terminal that
happens to have remote viewers, zero downtime."*

---

## 1. Where we are (snapshot, 2026-07-02)

- **Architecture**: flipped. muxd (PC, Python+pywinpty, ~330 lines) owns session ConPTYs locally;
  the VPS relay is a viewer/bridge with the old tmux+ssh path as automatic fallback. Outbound-only
  link with LAN-preferred + public-wss fallback + auto-return.
- **Live right now**: relay healthy (`ok:true`, 0 errors in the journal since deploy), host link up,
  PC RTT 4ms, 1 hosted session, 8 legacy sessions (4 armed), muxd task `Ready`, both repos clean at
  their milestone commits.
- **Proof it works — not from a drill, from PRODUCTION**: at 03:11:03 the LAN link dropped (Wi-Fi
  blip, keepalive timeout). muxd failed over to the public relay path **in the same second**; at
  03:14:05 the M5 auto-return probe brought it **back to the LAN**. No session noticed. Nobody was
  watching. That is the design goal happening on its own.
- **Verified invariants** (drilled repeatedly): 51s total Wi-Fi blackout → agents unharmed; relay
  restart → output continuous (max gap 1.02s); create/attach burst → zero tmux twins; pin survives
  reconnect + relay restart; 6000-char paste intact; rename migrates state; dead sessions revive on
  attach; `muxctl` gives local tmux-attach parity.

## 2. Scorecard

| Dimension | Grade | Why |
|---|---|---|
| **Core architecture** | **A** | Ownership is finally on the right machine. Every failure the old design died from (VPS reboot, either Wi-Fi hop, relay deploy, ssh drop) is now a display blip, proven by reproduced-failure drills AND one live incident. Failure domains match the review's target table. |
| **Reliability (hosted path)** | **A−** | Invariants hold; self-heals at every layer (muxd manifest, relay watchdog, DHCP resolver, link failover). Docked: muxd is young (~1 day in prod, one real session), and logoff kills it (documented, reboot-equivalent). Trust grows with runtime. |
| **Reliability (whole fleet)** | **B−** | The honest number: **8 of 9 live sessions still ride the legacy path** — the one that kills agents on a bad Wi-Fi day. The flip protects new sessions; the fleet migrates only on relaunch. And boot-recreate still resurrects armed sessions onto **tmux+ssh even when muxd is up** (gap found in this review). |
| **UX** | **B+** | Pin-follows-device (last-writer-wins), desktop keybar, working copy/paste, PC badge, revive-on-attach — all verified mechanically. Docked: **none of it has passed your hands yet** (HUMAN-GATE), and "needs attention" states (S7) still look like a plain red dot. |
| **Security** | **B+** | Owner-gated everything, token rejected pre-handshake in constant time, LAN-scoped ufw, secrets out of git. Docked: node 18 (EOL) under the relay; no rate limiting on the auth endpoints. |
| **Ops/observability** | **C+** | Health endpoint + status dot exist and are honest (hostedArmedDown). But: no alerts (a degraded dot only helps if you're looking), no state backups, verifiers live in /tmp instead of the repo, deploys still drop viewers without a drain. |
| **Code quality** | **B−** | Everything is verified, commented, and in git — but server.js is ~850 lines of single-file relay doing 9 jobs, there are zero unit tests (only the e2e verifiers), and agent-state detection is still footer-regex (D4) — every codex/claude UI update is a slow-motion threat to the dots and the watchdog. |

**Overall: B+, trending A−.** The hard, structural work is done and proven. What remains is
adoption (migrate the fleet), trust (runtime + your hands on it), and the ops floor.

## 3. What we should do next (priority order)

### N0 — Yours, ~10 minutes total (unlocks more than any code)
1. **Use it**: open the site on phone + desktop, tap the size chip on the phone (everything should
   go phone-shaped), toggle `⌨` on desktop, select/copy, paste something big. The features are
   machine-verified; they need your verdict.
2. **TELUS router**: disable client/AP isolation on pod `3c:f0:83:49:53:ce` + DHCP-reserve the PC
   (`04:e8:b9:44:7a:46`). Kills the two remaining network gremlins at the source.

### N1 — Migrate the fleet (the biggest reliability win left)
8 sessions still on the fragile path. Two parts:
- **Code (small)**: boot-recreate + the relay watchdog's recovery should create/resume via **muxd
  when the host link is up** instead of tmux+ssh — then every recovery event auto-migrates a
  session to the safe path. Today they re-land on the old one.
- **Manual (your timing)**: relaunch each long-running session at a natural pause (↻ Relaunch →
  it comes back hosted, `PC` badge visible). The two long codex goals should migrate last, at a
  checkpoint you choose.

### N2 — Process-truth state (kills D4, now possible)
muxd owns the pty, so it can see the **child process tree** (is claude.exe/codex.exe actually
alive? consuming CPU?). Report that with each status push and let the dots + watchdog use process
truth instead of footer-regex. Ends the "a TUI redesign breaks the watchdog" failure class for
hosted sessions.

### N3 — Ops floor (P6, half a day)
- Move the m1–m4 verifiers + ghost check into the repo (`tests/`) with a `run-all` script; run
  before every deploy (they caught 3 real bugs this pass — they've earned a home).
- Nightly state backup (pins/autoheal/projects/uploads-meta → the PC, it's the durable side now).
- **Alert, don't just color a dot**: health degraded / watchdog gave-up / host link down > 5 min →
  ntfy push to your phone.
- Node 18 → 22 LTS on the relay. Deploy drain (SIGTERM → clients get a "restarting" toast).

### N4 — De-app the queue + the GUI crash (P5)
Uploads/add-to-collection still require the desktop app, and the GUI still crash-loops
(`0xc000027b`). Either fix the XAML crash (its own debugging session) or reroute uploads through
the server's own ssh to the PC — muxd could even absorb the queue consumer role later.

### N5 — Small polish (batch when touching the files anyway)
Copy overlay pulling full muxd ring history on hosted sessions · S7 "needs attention" chip
(distinct from plain red) · muxctl live console-resize forwarding · logoff spike (run muxd as a
logon-independent task, needs a ConPTY-without-desktop test) · test harnesses must not leak tmux
sessions (two `ghost-*` leaked from the throwaway-relay check; cleaned).

## 4. One-line verdict

The system finally matches how you use it — sessions live where the agents live, the network is
just a window — and it has survived every failure we could throw at it plus one real one we didn't
plan. Next: get the other 8 sessions onto it, make recoveries land there by default, and give it
eyes (alerts) so the first sign of trouble isn't a dead chat.
