# Web Scroll Affordance Acceptance Checklist

## Context

The page under test is `relay/public/index.html`, served by the relay. Confirm the loaded build first by opening the browser console and reading `window.__muxBuild`; it must be exactly the string `2026-07-23-visible-scrollbar-honest-alt-scroll`. Anything else means a stale deploy and the rest of these checks are meaningless.

## Checks

- [ ] At a desktop viewport of about `1906x912`, use a session with real scrollback and inspect the terminal's right edge.
  Expected: A thin themed scrollbar is visible on the terminal's right edge and can be dragged with the mouse to move through the terminal history, visibly scrolling the buffer.

- [ ] On a phone, vertically touch-drag inside the terminal through xterm scrollback, including when the shared terminal is taller than the pane (`rowpan`).
  Expected: Touch scrolling through xterm's scrollback works without fighting the pan, and the `rowpan` drag still pans the host rather than dead-ending at the scrollback boundary.

- [ ] Run a full-screen app such as `vim`, `less`, or `htop`, then quit it.
  Expected: In the alternate screen the scrollbar disappears and an `app scroll` chip appears in the terminal's bottom-right corner above the jump-to-live pill; after quitting, the scrollbar returns and the chip becomes a position readout.

- [ ] Scroll up in the normal buffer, then return to the live bottom.
  Expected: A position readout such as `top · 240 up` or `62% · 91 up` appears while scrolled up, disappears when pinned to the live bottom, and never overlaps or blocks the jump-to-live pill.
