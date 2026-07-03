# Campaign: Multiplex Reliability Parity

## Win Condition (change-controlled)
- `multiplex` in a local Windows terminal and `https://harmonizerlabs.cc/multiplex/` address the same PC-hosted muxd sessions.
- Starting a session locally makes it appear on the web; starting a session on the web makes it attachable locally.
- A named active session can be attached locally without creating a VPS tmux twin.
- After reboot, unarmed sessions do not auto-run agents. Only explicitly armed watcher/autoheal sessions may auto-start.
- Web responsiveness is attributable with logs/probes; no unexplained fallback, twin creation, or empty-state disagreement remains.
- Latency-parity target: starting/attaching through multiplex should be attributable against local-terminal baselines. Any remaining overhead must be named as startup shell/ConPTY, transport, browser rendering, or launcher compatibility.
- HUMAN-GATE: the user confirms the site feels responsive enough during real agent work.

## Constraints & Anti-goals
- Do not kill live agent sessions unless a verifier requires it and there is no non-disruptive path.
- Do not restore the legacy default where every reboot or plain tab visit auto-starts all saved agents.
- Do not let VPS tmux be the normal ownership model; muxd on this PC owns the real terminals.
- Preserve existing unrelated repo changes.

## Terrain Map
- Known true: PowerShell `multiplex` currently comes from `C:\Users\Ahmed\Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1` and calls `ssh -t harmonizer@192.168.1.142 /usr/local/bin/multiplex-session <name>`.
- Known true: `/usr/local/bin/multiplex-session` is a legacy tmux helper: `tmux new -A -s ... "ssh -t win || exec bash -l"`.
- Known true: muxd loopback attach server is alive on this PC, but `muxctl ls` currently reports no muxd-hosted sessions.
- Known true: VPS `/api/health` reports `host.connected=true`, host name `CRACKERBARREL`, `host.sessions=0`, tmux down/empty, armed count 0.
- Known true: relay `/ws` creates PC-hosted sessions when the host link is up and no tmux session exists.
- Known true: muxd has an existing local attach protocol, but it only supports list/attach, not local create/open.
- Unknown: whether web slowness is caused by output frame pressure, sizing/pin recompute, browser rendering, or fallback churn during reconnects.

## Solved Ground (PROTECTED)
| What | Evidence | Date |
| --- | --- | --- |
| Unarmed manifest entries no longer auto-start on muxd boot | commit `b35798d`, `python -m py_compile muxd.py`, log wording changed to explicit relaunch required | 2026-07-03 |
| VPS sudo/nginx were repaired in previous lane | handoff: sudoers verified with `sudo -n true`, nginx `/remote/healthz` 200 | 2026-07-03 |
| Local and web attaches can target the same muxd ConPTY | local websocket marker `LOCAL_MUX_OK_1783064119`; relay `/ws` marker `WEB_MUX_OK_1783064171356`; both through `mux-parity-test` | 2026-07-03 |
| Legacy VPS helper no longer creates tmux sessions | `/usr/local/bin/multiplex-session` now SSHes to PC and runs `muxctl.py open <safe-name>` with PowerShell `-EncodedCommand`; test created hosted `mux-helper-test`, then cleanup left `/api/sessions` empty | 2026-07-03 |
| Relay health no longer falsely degrades when tmux has no live server and muxd is connected | `/api/health` returned `ok:true`, `degraded:false`, `host.connected:true`, `host.sessions:0` after cleanup | 2026-07-03 |
| Latency attribution: cold start is dominated by local ConPTY/shell startup, not relay | Local fresh muxd sessions: `open_to_marker` ~3.53-3.85s; web relay fresh sessions: ~3.63-3.74s; direct winpty PowerShell/cmd startup variants: ~3.03-3.35s | 2026-07-03 |
| Latency attribution: warm sessions are low-hundreds-ms, relay not the dominant cost | Existing session local attach/input: ~74-145ms; existing session relay attach/input from VPS loopback: ~65-220ms | 2026-07-03 |

## Approach Tree
| # | Approach class | Prediction | Cheapest probe | Kill criteria | Status |
| --- | --- | --- | --- | --- | --- |
| 1 | Replace local entrypoint with muxd-backed open/attach | Local terminal stops making tmux twins and can attach to web-started sessions | Add muxd local create/open; test local start -> API sessions and local attach | Dead if local muxd cannot create/attach without relay and web does not see it | won |
| 2 | Harden relay fallback boundaries | Web only falls back to tmux when muxd host is truly down; no stale tmux twin | API/WS probes under host up/down | Dead if fallback is not involved in observed failure | won |
| 3 | Attribute web glitchiness with measured output path | Slow path appears as host link churn, frame flood, or client render backlog | Controlled noisy session with timestamps/log counts | Dead if controlled session is smooth while real agent remains slow | smoke-tested |
| 4 | Backend sync freshness | Web project/running sync is stale or disabled independently of terminal muxing | Inspect `/api/projects`, app sync process/logs | Dead if project sync is live and not part of current UX failure | not blocker |
| 5 | Latency parity attribution | If multiplex is not identical to local, overhead is one of shell startup, relay transport, browser xterm rendering, or launcher compatibility | Fresh vs warm local/web probes and code inspection | Dead if measurements cannot separate startup from transport | attributed |

## Fronts
| Front | Mechanism | State | Last advance |
| --- | --- | --- | --- |
| Local parity | self/debug | won | muxd local create/open plus legacy helper bridge verified |
| Web attach correctness | self | won | local and relay WebSocket marker probes passed |
| Performance attribution | self | smoke-tested | 300-line relay smoke finished in ~3.5s, but line counter was not a certified gauge |
| Backend sync | self | not blocker | `/api/projects` live with recent sync |
| Latency parity | self/debug | attributed | Cold start dominated by local shell/ConPTY startup; warm-session relay overhead is small; scrollback/browser and protected profile remain |

## Beat Log
- 2026-07-03: Landing from handoff. Current trunk is local/web parity. Evidence shows local `multiplex` bypasses muxd and uses legacy VPS tmux, while muxd and relay both report zero hosted sessions.
- 2026-07-03: Implemented muxd loopback `create`/`open`; `muxctl create mux-parity-test` spawned a PC ConPTY with `cmd=no`; `muxctl ls` and VPS `/api/sessions` both reported it as hosted.
- 2026-07-03: Proved local attach I/O by sending `Write-Output LOCAL_MUX_OK_1783064119` over `ws://127.0.0.1:7699`.
- 2026-07-03: Proved web relay attach I/O by connecting to VPS loopback `/ws` and seeing `WEB_MUX_OK_1783064171356` from the same hosted session.
- 2026-07-03: Patched `/usr/local/bin/multiplex-session` as a compatibility bridge for the protected old PowerShell profile. It now runs PC muxctl via SSH instead of `tmux new -A`. A timeout test reached `muxctl -> opening local muxd session 'mux-helper-test'` and the session appeared as `hosted:true`.
- 2026-07-03: Patched relay `/api/health`: tmux availability is only degraded when muxd is down too. Restarted `multiplex-app.service`; health is now `ok:true`, `degraded:false`.
- 2026-07-03: Project sync check is live: `/api/projects` reports host `CRACKERBARREL`, 24 collections, 18 running-session records, synced ~25s old. Treat backend sync as not the active blocker unless it regresses.
- 2026-07-03: Latency probe pass. Fresh local muxd sessions took ~3.5-3.9s to produce marker output; fresh relay sessions took ~3.6-3.7s, so the relay is not the dominant cold-start bottleneck. Direct winpty startup of PowerShell/cmd variants took ~3.0-3.4s, identifying local ConPTY/shell startup as the main cold-start cost.
- 2026-07-03: Warm-session probe pass. Existing session local attach/input produced marker output in ~74-145ms. Existing session relay attach/input via VPS loopback produced marker output in ~65-220ms. Transport overhead is present but not multi-second.
- 2026-07-03: Frontend code inspection: relaunch path has hard-coded waits of 500ms + 700ms + 800ms; each web attach writes scrollback replay into xterm (`SB_SEND=260000` from muxd, browser xterm scrollback 8000). Heavy scrollback can cause browser-main-thread jank even when the PTY is healthy.

## Learnings
- The word "session" currently names three different things: VPS tmux, relay-visible hosted session, and muxd local ConPTY. Reliability requires one normal ownership path.
- Windows Defender Controlled Folder Access is enabled, and direct writes to `C:\Users\Ahmed\Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1` are denied from this process. The old function still prints stale text, but the VPS helper it invokes now lands in muxd.
- Cold-start parity with an already-open local terminal is impossible with the current architecture because muxd creates a new winpty/ConPTY + shell for each new session. To match an existing local terminal, muxd needs either a warm shell/PTY pool or direct process launch of parsed agent commands.
- Runtime parity is much closer after startup; the remaining non-local feel is mostly browser/xterm rendering, scrollback replay, shared-size/pan behavior, and the old `multiplex` profile still taking a compatibility SSH bounce.

## BLOCKED / Decisions needed
- Cosmetic/direct launcher cleanup is blocked by Controlled Folder Access unless the profile can be edited from an allowed/elevated process. Functional named attach is covered by the helper bridge.
