# Multiplex Reliability Strategy

This file owns strategy only. Runtime acceptance and deployment evidence live in the retrieval
app's `CURRENT.md`, `LOOPS.md`, and `CAMPAIGN.md`. Git history preserves the July 6 audit plan.

## Reliability Contract

- One canonical session identity has at most one app-controlled writer.
- Create and relaunch hold one reservation across check, stop, spawn, and durable publication.
- A replacement never starts until prior process-tree exit is confirmed.
- Relay requests carry opaque intent and identity, not executable commands or local paths.
- Acknowledged state and commands survive restart, replay, and lost responses.
- muxd-owned children cannot silently outlive custody.
- Queues, scrollback, protocol lines, process output, and retained intent history are bounded.
- Durable file work and blocking process inspection stay off the asyncio event loop.
- Browser pages cannot access the loopback control protocol.

## Landed Architecture

### Session ownership

- muxd is the single ConPTY owner for hosted sessions.
- C# launchers and muxd share canonical identity claims.
- Per-session create locks serialize concurrent create and relaunch requests.
- Process instance tokens prevent recycled PIDs from being treated as owned children.
- Failed termination preserves custody and blocks replacement.
- Visible-owner sidecars require reconnect keys and explicit child-exit confirmation.

### Durable state and delivery

- Session manifests use flushed atomic replacement, semantic read-back, backups, and recovery.
- State mutations run as cancellation-safe serialized transactions.
- Create and input intents are fingerprinted, replay-safe, and durably terminalized.
- Relay commands use fenced leases and acknowledgements; live work is never age-pruned.
- PTY input is explicitly at-most-once when dispatch outcome cannot be made atomic.
- Boot reconciliation normalizes interrupted and stale active lifecycles before reuse.

### Responsiveness and resource bounds

- Manifest persistence runs off-loop.
- Non-durable input does not wait on unrelated durable state work.
- PTY input writers are lazy, generation-pinned, and stopped on every terminal path.
- Input enqueue and stop-sentinel ordering share one mutex.
- Ring access is synchronized; relay and local queues are bounded.
- Long process output and protocol lines are bounded and drained.
- Task failures are supervised and logged.

### Trust boundaries

- Relay schema contains no executable command or local filesystem authority.
- The VPS cannot autonomously kill a local writer.
- Browser-origin WebSockets are rejected before muxd reads a loopback control frame.
- Relay host connections are token-gated and protocol-version checked.

## Verification

Every production change must pass:

1. `python -m pytest -q`
2. VPS-backed smoke tests with `MUXD_VPS_TESTS=1`
3. The unified retrieval-app gate in `tools/run_gates.ps1`
4. Installed-runtime checks for PID replacement, manifest read-back, relay protocol parity,
   duplicate create, lost-ack replay, restart recovery, loop lag, and memory bounds

Changes that affect create, relaunch, input, persistence, or process custody require a regression
that is observed failing against the prior behavior before the fix is accepted.

## Remaining Strategy

These are improvements, not unresolved session-loss defects:

- Move LAN relay authentication from URL/query configuration to an authenticated first frame and
  use transport encryption where the LAN trust model requires it.
- Build a survivor broker only if non-destructive muxd upgrades become worth the complexity;
  Windows ConPTY ownership currently makes a daemon restart destructive by design.
- Continue mobile IME, terminal-query filtering, and scroll ergonomics as separate frontend work.
- Replace heuristic agent-status labels with stronger process and protocol truth where available.
- Add alerting for prolonged relay disconnects and watchdog escalation.
- Keep relay deployment provenance clean by converting the VPS working copy into a reproducible
  packaged deployment rather than a long-lived patched tree.

## Restart Discipline

Batch muxd changes, inspect live sessions, back up manifests, stop the scheduled task, prove the old
daemon and owned children exited, start one replacement, then verify durable reconciliation and relay
reconnect. Never restart merely to clear a transient symptom.
