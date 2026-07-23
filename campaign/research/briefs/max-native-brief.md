READ FIRST: campaign/research/briefs/_shared-context.md (shared campaign context, deliverable
schema, conduct rules). Then this brief. Repo root = Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval

# LANE max-native — refine the mux-native prong (real-local-terminal fidelity)

Base plan: `campaign/mux-native.md` (leaves A–F first cut; G/H/I deferred). Read it fully,
including §0 (the alt-screen doctrine — it stands: no faked alt-screen scrollback, ever).
Your job: validate anchors, PROMOTE the terminal model to core, spec the stripMouseReports hang
leaf, fold in cold finding 5, and emit the refined orch-ingestable leaf set.

Deliverable: `campaign/research/max-native-refined.md` (schema in shared context).

## Task 1 — PROMOTE leaf H (muxd terminal model) to a CORE subtree — the campaign centerpiece
Governing call 2: the finish line is a remote terminal that behaves like a real local terminal —
native scroll/selection/copy/visible scrollbar, TRUE scrollback, correct reflow on width change,
low latency. The base plan's H sketch (H1 fidelity spike → H2 sidecar model + snapshot cap →
H3 resize-correct attach repaint → H4 sbtext/search + web history overlay) is the right spine —
now decompose it into REAL leaf specs:
- H1 (spike): feed recorded ring bytes from a real codex/claude session through pyte (or a
  candidate VT emulator), diff final screen vs xterm.js headless — spec the go/no-go verifier
  and what "go" means (fidelity threshold, ConPTY VT dialect coverage incl. the
  REPLAY_PRIVATE_MODES set muxd already tracks). kind:build, but its OUTPUT gates H2+ — model
  the gate as deps.
- H2: sidecar model fed from the session ring tap, per-session memory bounds (muxd/PLAN.md's
  bounded-resources contract), a `snapshot` capability behind a feature flag, NO attach-path
  change yet. muxd.py restart discipline applies but deploys are free (governing call 3).
- H3: attach repaint from the model snapshot when viewer size ≠ ring width — kills raw-byte
  replay garble. Must keep the raw ring as fallback.
- H4: `sbtext`/history text export + web history/search overlay (crosses into index.html —
  note the seam with your C/D leaves and the value prong's UI work).
Decide: does H need a kind:"plan" fable node first, or are your H1–H4 specs tight enough to go
straight to build leaves? (Prefer tight build leaves + one judge node; only keep a plan node if a
genuine open design decision remains — name it if so.) Look at muxd.py's ring/replay machinery
(`attach_replay_payload` ~:1248, RING_CAP/SB_SEND constants) to ground the specs.

## Task 2 — the stripMouseReports hang (priority bug)
A HANGING test exists in the stripMouseReports area — likely a real infinite loop on some input
in the LIVE-PATH function (`stripMouseReports` in relay/public/index.html ~1432; tests in
relay/tests/wheel-scroll.test.js — note the campaign directive said relay.test.js; the function's
tests actually live in wheel-scroll.test.js — pin down where the hang manifests). Investigate by
READING the function: look for loops whose index doesn't advance on malformed/partial sequences.
You may run single test files ONLY with `node --test --test-force-exit --test-timeout=20000`.
Characterize the bug (input class that loops), then spec a find+fix build leaf whose verifier
includes a regression test with the pathological input under --test-timeout. Do NOT fix it.

## Task 3 — fold in cold finding 5 (input continuity)
From the cold-correctness review (path in shared context if you want the full text): terminal
input is silently discarded while the ws is reconnecting — index.html ~1091 no-ops unless the
socket is open; all xterm input routes there (~1444-1462); compose actions close the dialog
before term.paste/send (~2116-2117). Typing/submitting during a transient disconnect loses data
with no indication. Spec a build leaf: bounded pending-input buffer with replay, or disable input
with clear affordance while disconnected — pick the design (state why) and give it a vm-extract
node:test verifier. (Finding 6, ws control-frame validation, belongs to the adversarial lane —
note the index.html seam.)

## Task 4 — validate/refresh the base first cut (A–F) + reconsider G
- Re-verify anchors for A–F (muxctl.py wheel_input_sequences, web build tag, scroll-parity plan).
- Re-cut deps for branch-per-leaf: A→B (vector file contract) and A→E stay semantic? C→D was
  same-file serialization — drop unless truly semantic.
- Leaf G (web click forwarding + leak detector): under the "full native" bar, recommend whether
  it enters the tree now (with the leak-detector design pinned in the goal) or stays deferred —
  argue it, don't punt.
- Leaf F (latency probe): keep; confirm the probe design's muxd availability assumption on the
  runner still holds.

Reads to start from (verify, then expand as needed): campaign/mux-native.md, muxd/muxctl.py,
muxd/muxd.py (ring/replay + attach regions), relay/public/index.html (wheel router ~1298,
stripMouseReports ~1432, input send ~1091/~1444, selection/copy ~2290-2519),
relay/tests/wheel-scroll.test.js, relay/tests/client-layout.test.js, muxd/tests/ (scroll/input
tests), muxd/DEPLOY-local-scroll.md, muxd/PLAN.md.
