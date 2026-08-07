# Remote terminal mirror

Multiplex supports two Windows terminal ownership modes:

1. **Mux-owned session**: muxd launches the shell under ConPTY and owns its process tree.
2. **Adopted local session**: `muxrun.py --attach-pid` attaches to an already-running
   Windows console, mirrors its visible screen, and forwards remote input without relaunching it.

The adopted mode is the path used by the web app's **Mirror** action. The original terminal stays
open and remains the process/console owner. A hidden sidecar attaches with `AttachConsole(pid)`,
reads the attached standard output screen-buffer handle, sends repaint frames to muxd, and writes remote key events with
`WriteConsoleInputW`.

## Adopted-session contract

- The app and relay independently verify the exact live `(pid, session id, tool)` tuple.
- muxd verifies that the reported session identity belongs to that exact PID before allowing the
  duplicate-writer launch claim exemption.
- muxrun opens an exact process handle before attaching to the console. Liveness and explicit Stop
  use that handle, so PID reuse cannot redirect the operation.
- Adopted rows publish `adopted: true`, `externalOwner: true`, and `kind: adopted-local`.
- Adopted sessions are never auto-healed or relaunched by muxd.
- Sidecar or bridge exit only detaches the web mirror. It does not stop the local agent.
- A detached sidecar can re-register only while the same process-instance token and session
  identity still match.
- Explicit mux Stop asks the sidecar to terminate the adopted target, waits for confirmed exit,
  and only then removes the durable mux row.

The existing `/tomux` workflow remains separate. It deliberately stops a local writer and relaunches
the session under mux ownership. Mirror is the non-destructive path for an already-running terminal.

## Data flow

```text
local Codex/Claude console
        ^
        | AttachConsole / screen-buffer read / WriteConsoleInputW
        v
hidden muxrun sidecar
        ^
        | loopback owner websocket
        v
muxd <-> relay <-> browser terminal
```

## Current Windows limits

- This depends on the target being attached to a classic Windows console surface that exposes
  console screen-buffer APIs. It is Windows-only.
- The mirror sends visible text repaints rather than taking process ownership or converting the
  existing console into a ConPTY.
- Closing the browser, desktop app, bridge, or sidecar does not close the adopted terminal.
