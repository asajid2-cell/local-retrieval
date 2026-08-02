# wp-tui fold

Date: 2026-08-02

## Result

W1-W3 are implemented in the relay and web terminal client.

- Explicit pin authority now resolves before attached-local ownership. Pinning the phone therefore
  resizes the one shared PTY and updates every viewer, including the PC terminal.
- Unpin returns the shared state to `auto`. The control frame retains `local: true` when auto is
  currently sourced from an attached PC terminal so the client can explain that state honestly.
- Headless auto negotiation now uses the per-axis minimum across active viewers. With one PTY and
  one grid, `min(cols) × min(rows)` is the only automatic grid every active viewport can display
  completely; larger viewports letterbox on either axis.
- Immediate unpin does not trust a stale pre-pin host snapshot. During the short host-report
  convergence window it keeps the PTY size last established by the accepted pin, then clears that
  transitional value once muxd reports the same size.
- A pin persistence fault is caught at the websocket boundary. An uncommitted pin does not resize
  the PTY, the relay remains alive, and health reports persistence blocked instead of crashing all
  attached terminals.
- The size chip now renders exactly `auto · WxH`, `📌 this device · WxH`, or
  `📌 <other device> · WxH`. Its title explains PC-sourced auto, mirrored pins, panning, and the
  tap-to-pin escape hatch. The status-strip copy is always visible and clickable on desktop;
  the touch keybar keeps its existing copy on mobile.
- Horizontal overflow now has a styled outer `#term.pan` scrollbar on fine-pointer desktops.
  Alternate-buffer handling suppresses only the dead vertical xterm viewport direction, leaving
  the outer horizontal pan reachable.
- The scroll chip is now tappable. It distinguishes app-owned vertical scrolling from horizontal
  panning, including `app scroll · pan N cols`, and gives a short status explanation on tap.

## Relay Evidence

`relay/tests/relay.test.js` now drives the real relay through `RelayHarness`:

- A `FakeHost` advertises a local-owned `160x44` hosted session.
- A `72x28` phone and `132x40` desktop attach.
- The phone sends `P1`.
- FakeHost receives `{t:"resize", s:"shared-pin", cols:72, rows:28}`.
- Both viewers receive pinned `d` frames at `72x28`; the phone has `mine:true`, the desktop
  `mine:false`, and their labels distinguish `this device` from `Phone`.
- Without injecting any host size acknowledgement, the phone sends `P0`.
- Both viewers receive `mode:"auto"`, `local:true`, and `72x28`.

A second relay test attaches asymmetric `74x60` and `138x26` viewers to a headless hosted session
and proves that auto sends the host and both viewers the common `74x26` grid. A third test injects
a `pins.json` pre-write failure and proves the process remains reachable, reports blocked
persistence, and does not send the rejected pin resize.

`relay/tests/client-layout.test.js` executes the shipped client functions in a DOM-like VM and
proves the exact size labels, width-mismatch `.pan` activation, visible outer scrollbar rules,
alternate-buffer horizontal-pan preservation, and pan-aware chip text/title.

## Verification

Re-ran:

```text
node --test --test-name-pattern "explicit device pin" tests/relay.test.js
node --test tests/client-layout.test.js
node --test --test-name-pattern "explicit device pin|headless auto sizing|pin persistence failure" tests/relay.test.js
npm test
git diff --check
```

Final `npm test`: 409 tests, 408 passed, 0 failed, 1 skipped.

## Manual Checks Remaining

Only a human with the real muxd/PTY and devices can confirm:

- While typing in a PC terminal, pin from a narrower phone and confirm both live terminals redraw
  at the phone grid without freezing or dropping input.
- Unpin and confirm every web viewer shows auto and the attached PC terminal remains authoritative.
- On desktop, drag the horizontal scrollbar in a deliberately oversized alternate-screen TUI and
  confirm the hidden columns are reachable. The DOM test proves the shipped pan state and CSS
  affordance appear, but this repository has no browser layout engine to prove `scrollWidth`,
  `clientWidth`, and live `scrollLeft` movement.
- Tap the `app scroll`/pan chip and the mirrored size chip on phone and desktop to confirm the
  explanations are readable and the pin takeover is discoverable.

## Files

- `relay/server.js`
- `relay/public/index.html`
- `relay/tests/harness.js`
- `relay/tests/relay.test.js`
- `relay/tests/client-layout.test.js`
