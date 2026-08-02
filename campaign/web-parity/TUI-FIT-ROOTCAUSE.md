# Why the web TUI is "big and cut off", and what is actually broken (apex, 2026-08-02)

## Root cause: the local-first sizing rule (working as designed)

`relay/server.js:3143-3148` — when a session has an attached PC-local terminal, that terminal OWNS
the PTY size and every web viewer mirrors it (`hostedSize: true`, mode label `local · <cols>×<rows>`):

    const localOwned = ... ((hosted.localViewers|0) > 0 || !!hosted.owner);
    if (localOwned && ...) return { cols: hosted.cols|0, rows: hosted.rows|0, pin: null, hostedSize: true };

The `orch` tab in the screenshots is open locally at **248×48**. A browser ~1900px wide fits roughly
190 columns, so the viewer goes into `.pan` mode (`applyPan`, index.html:1408) and the right-hand
columns fall off screen. Nothing is malfunctioning: resizing the PTY from the web would reflow the
local terminal the owner is actively typing in, which the rule exists to prevent.

Note the asymmetry that makes this confusing: a HEADLESS PC-hosted session (no local terminal)
does resize to the web viewer, so "sometimes it fits, sometimes it doesn't" is expected behavior
with no visible explanation.

## What IS broken (the real defects behind the complaint)

1. **Panning is undiscoverable and desktop-unusable.** The horizontal pan exists only as a
   touch-drag (index.html ~1554, `touchstart/touchmove` on `#term`). On desktop there is no
   scrollbar, no drag cursor, no keyboard affordance — the content is simply cut off with no way to
   reach it. FIX: a real horizontal scrollbar (or shift+wheel / drag-to-pan with a cursor hint) on
   `#term.pan`, on both pointer and touch.
2. **`.altbuf` hides the scrollbar even when horizontal panning is still meaningful.**
   `applyScrollAffordance` (index.html:1999) sets `.altbuf` on the alternate screen and the CSS
   hides the bar, because vertical scrollback genuinely does not exist there. But horizontal pan
   does, and it gets killed with it. FIX: hide only the VERTICAL affordance on the alt buffer;
   keep horizontal panning visible whenever `winSize.cols > viewportFit.cols+1`.
3. **The "app scroll" chip is correct but cryptic.** It means "this app paints its own screen, so
   there is no scrollback to drag" — the user read it as a random pill. FIX: keep the chip, give it
   a title/tooltip and (on tap) a one-line explanation, and when panning is active say so
   (e.g. `app scroll · 248 cols — drag to pan`).
4. **No explanation of WHY the size is what it is.** The status already carries `local · 248×48`;
   the size button (`⇲ size`, `cycleSize`) can pin the window to THIS device, which is exactly the
   escape hatch, but nothing tells the user that. FIX: when `mode === 'local'` and the viewer is
   panning, surface a one-tap hint on the size chip: "mirroring the PC terminal (248×48) — tap to
   fit this screen instead", making the existing pin flow discoverable.

## Sequencing
`relay/public/index.html` is owned by the `wp-mobile` swarm right now (keyboard + focus fixes).
This work is a SEPARATE lane that must start only after that swarm's fix is integrated —
same-file collision otherwise. Items 1-3 are client-only; item 4 is client-only too (the relay
already sends `mode`, `modeLabel`, and `clients`).
