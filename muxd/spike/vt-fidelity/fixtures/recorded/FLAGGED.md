# Recorded VT fixture corpus — review flags

Fixtures in this directory are **recorded**, not synthetic: their bytes come from a real
ConPTY or from a live `muxd` session rather than from `gen_synthetic.py`. Anything whose
sidecar sets `containsRecordedSessionContent: true` carries real captured session content
and is flagged below for human review before commit.

Sidecar contract (per `<name>.json`): `corpus`, `cols`, `rows`, `source`, `desc`,
`containsRecordedSessionContent`, `captureMethod`.

| fixture | capture method | bytes | buffer | recorded session content |
| --- | --- | --- | --- | --- |
| `live-shell-review-rollout.bin` | `live-muxd` | 428142 | alt-screen | **yes** |
| `pager-alt.bin` | `conpty-pager-alt` | 18651 | alt-screen | no |
| `tui-alt.bin` | `conpty-tui-alt` | 5907 | alt-screen | no |
| `shell-plain.bin` | `conpty-shell-plain` | 1688 | normal | no |

---

## live-shell-review-rollout.bin

- **Capture method:** `live-muxd` (`capture.py --live review-rollout-session-progr-2de3c0dd`)
- **Byte size:** 428142
- **Geometry:** 80x24
- **Source:** live muxd session `review-rollout-session-progr-2de3c0dd`
- **Buffer:** contains an alt-screen enter (`ESC[?1049h`)
- **How it was taken:** attached read-only over the local muxd websocket. The attach frame
  was `{t:'attach', s:<name>, sb:<bytes>}` with **no `cols` field**, so the live session was
  never resized; no input was ever sent. The first reply is the binary
  `attach_replay_payload` (raw scrollback bytes), followed by live output frames.

> **ORCHESTRATOR FLAG:** this fixture contains real captured session content from a live
> muxd session and **must be reviewed by a human before commit**. Its bytes are a verbatim
> scrollback replay and may include file paths, command history, source fragments, or other
> environment-specific material from the recorded session.

## pager-alt.bin

- **Capture method:** `conpty-pager-alt` (`capture.py --conpty pager-alt`)
- **Byte size:** 18651
- **Geometry:** 140x40
- **Source:** `less -R muxd.py` driven under ConPTY (pywinpty)
- **Buffer:** alt-screen (`ESC[?1049h` enter/leave), full repaints, reverse-video status line
- **Recorded session content:** no — synthetic ConPTY drive of a known program against a
  repo file. No review flag required.

## tui-alt.bin

- **Capture method:** `conpty-tui-alt` (`capture.py --conpty tui-alt`)
- **Byte size:** 5907
- **Geometry:** 140x40
- **Source:** `tui_demo.py` driven under ConPTY (pywinpty)
- **Buffer:** alt-screen; exercises 1049/1/2004/1000/1002/1006, DECSTBM, SGR 256 +
  truecolor, and wide characters
- **Recorded session content:** no — output of a checked-in demo script. No review flag
  required.

## shell-plain.bin

- **Capture method:** `conpty-shell-plain` (`capture.py --conpty shell-plain`)
- **Byte size:** 1688
- **Geometry:** 140x40
- **Source:** `cmd.exe` driven under ConPTY (pywinpty)
- **Buffer:** normal buffer only — prompts, echoed input, scrolling output; no alt-screen
  enter anywhere in the stream. This is the corpus's normal-buffer anchor.
- **Recorded session content:** no — scripted commands against a fresh shell. No review
  flag required.
