# Charter — branch mind `wp-tui`

Owner's words: "the pin doesn't really work"; "if a session is open on both mobile and PC it'll be
broken on one or the other"; "either auto or this device — if one device is on this device,
everything else gets forced to defer; if it flips back to auto it flips to auto for everybody".

Repo: Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval (master). Persistent tandem peer; fan
out sealed junior lanes (luna@max) after freezing contracts; re-run every verifier yourself.
Read first: campaign\web-parity\TUI-FIT-ROOTCAUSE.md.

## The constraint (apex-established, do not try to design around it)

ONE PTY, ONE SIZE. Per-device rendering is impossible for a faithful mirror: there is a single pty
on the PC, the TUI paints absolute-positioned frames for its current size onto the alternate
screen, and the relay forwards those bytes verbatim. The relay cannot re-render another size, and
the app only redraws for one size at a time. Two sizes would mean two ptys = two different agent
sessions. So the answer is to make the SHARED size negotiate correctly and visibly.

## W1 — the pin must actually win (relay/server.js `targetSize`, ~line 3264)

Today the order is: local-first → pin → widest-active. The local-first branch RETURNS EARLY with
`pin: null`, so whenever a PC terminal is attached to that session the pin is ignored entirely.
The owner's PC terminal is essentially always attached, which is precisely why pinning from the
phone appears to do nothing.

Required order: **explicit pin → local-first → widest-active**. A pin is a deliberate human act and
outranks the local terminal. Note `recompute()` already forwards a resize to muxd whenever
`!sz.hostedSize`, so once a pin wins, the PTY genuinely resizes and the PC terminal re-renders at
the pinned size — that is the intended behavior ("if I switch it to phone it should switch to my
current width"), not a bug. Unpin must return everyone to auto (already true — keep it true).

Also fix the auto case if you agree it is wrong: auto currently takes `widest(active)`, which by
construction breaks the NARROWER device whenever a phone and a desktop are both active. Consider
narrowest-active (everyone can read; the wide device letterboxes) and state your reasoning either
way in the fold — this is a judgment call, make it explicitly, don't leave it unexamined.

## W2 — say what mode you are in (relay/public/index.html)

The size chip must make the model legible at a glance: `auto · WxH`, `📌 this device · WxH`, and
`📌 <other device> · WxH` when mirroring someone else's pin. When mirroring AND panning, say so and
point at the escape hatch — the existing tap-to-pin (`cycleSize`) is exactly what the owner wants
and nothing currently tells them it exists.

## W3 — panning must be reachable (relay/public/index.html)

- Horizontal pan is touch-drag only, so on desktop the overflow is simply unreachable. Give it a
  real affordance (scrollbar and/or drag with a cursor hint) whenever `winSize.cols > viewportFit.cols+1`.
- `applyScrollAffordance` sets `.altbuf` on the alternate screen and the CSS hides the scrollbar —
  correct for VERTICAL scrollback, which does not exist there, but it kills HORIZONTAL panning too.
  Separate them.
- The "app scroll" chip is accurate but reads as noise. Keep it, explain it (title/tap), and when
  panning is active say so.

## Verification (binding)

Source-text assertions are NOT sufficient — today a change that passed every such test froze the
live terminal. W1 needs real relay tests through tests/harness.js (a FakeHost plus two viewers of
different widths: pin from the narrow one, assert the host receives the resize and both viewers are
told the new size; unpin, assert auto returns for both). W3 needs at least one real-browser or DOM
check of the pan affordance appearing, plus manual notes for what only a human can confirm.
Write-scope: `relay/server.js`, `relay/public/index.html`, `relay/tests/**`. Fold to
`campaign\web-parity\folds\wp-tui.md`; pull the apex on a design fork or when ready to integrate.
