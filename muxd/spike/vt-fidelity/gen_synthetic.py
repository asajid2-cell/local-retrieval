#!/usr/bin/env python3
"""Synthetic VT fixtures for the terminal-model fidelity spike.

These are hand-built byte streams, not recordings: every one targets a specific slice of the
dialect muxd actually replays out of a session ring.  Together they cover the whole of
REPLAY_PRIVATE_MODES (muxd.py:920) plus the CSI/SGR/OSC vocabulary a ConPTY-hosted claude/codex
emits.  A candidate terminal model that renders these correctly can rebuild a muxd replay.

Emitted as <name>.bin + <name>.json sidecar into fixtures/synthetic/.

    python muxd/spike/vt-fidelity/gen_synthetic.py
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "fixtures", "synthetic")

ESC = b"\x1b"
CSI = b"\x1b["

# muxd.py:920 — the exact set muxd re-asserts to a late-attaching viewer.
REPLAY_PRIVATE_MODES = (1, 47, 1047, 1049, 2004, 9, 1000, 1002, 1003, 1005, 1006, 1007, 1015)

FIXTURES = []


def fixture(name, cols, rows, desc, body):
    FIXTURES.append({"name": name, "cols": cols, "rows": rows, "desc": desc, "body": body})


def _lines(*rows):
    return b"\r\n".join(rows)


# --- 1. every REPLAY_PRIVATE_MODES mode, set then reset, with visible text between ------------
# A viewer that mishandles a mode set/reset pair loses the screen it was supposed to rebuild.
buf = bytearray(b"private-mode sweep\r\n")
for mode in REPLAY_PRIVATE_MODES:
    buf += CSI + b"?" + str(mode).encode() + b"h"
    buf += b"set " + str(mode).encode() + b"\r\n"
    buf += CSI + b"?" + str(mode).encode() + b"l"
    buf += b"rst " + str(mode).encode() + b"\r\n"
fixture("private-modes-sweep", 80, 40, "set/reset each REPLAY_PRIVATE_MODES mode with text between",
        bytes(buf))

# --- 2. alt screen: enter 1049, paint, leave — the primary buffer must survive ----------------
alt = bytearray()
alt += b"primary line A\r\nprimary line B\r\nprimary line C\r\n"
alt += CSI + b"?1049h"          # save cursor + switch to alt buffer + clear
alt += CSI + b"2J" + CSI + b"H"
alt += _lines(b"ALT ROW 1", b"ALT ROW 2", b"ALT ROW 3")
alt += CSI + b"?1049l"          # back to primary; alt content must vanish
alt += b"\r\nprimary line D"
fixture("altscreen-1049-roundtrip", 80, 24, "1049 enter/paint/leave; primary buffer must be intact",
        bytes(alt))

# 47 / 1047 are the older alt-screen spellings muxd also replays; a viewer that only knows 1049
# renders a codex/claude TUI into the wrong buffer.
alt47 = bytearray(b"before-47\r\n")
alt47 += CSI + b"?47h" + CSI + b"2J" + CSI + b"H" + b"alt47 content"
alt47 += CSI + b"?47l"
alt47 += b"\r\nafter-47\r\n"
alt47 += CSI + b"?1047h" + CSI + b"2J" + CSI + b"H" + b"alt1047 content"
alt47 += CSI + b"?1047l" + b"\r\nafter-1047"
fixture("altscreen-47-1047", 80, 24, "legacy alt-screen spellings 47 and 1047", bytes(alt47))

# --- 3. stay-in-alt-screen: the state a live codex/claude TUI is actually parked in ------------
tui = bytearray()
tui += CSI + b"?1049h" + CSI + b"?1h" + CSI + b"?2004h"     # alt + DECCKM + bracketed paste
tui += CSI + b"?1000h" + CSI + b"?1002h" + CSI + b"?1006h"  # mouse tracking family
tui += CSI + b"2J" + CSI + b"H"
tui += CSI + b"1;36m" + b"\xe2\x95\xad" + b"\xe2\x94\x80" * 40 + b"\xe2\x95\xae" + CSI + b"0m\r\n"
for i in range(6):
    tui += CSI + b"1;36m\xe2\x94\x82" + CSI + b"0m"
    tui += (" row %d " % i).encode().ljust(40)
    tui += CSI + b"1;36m\xe2\x94\x82" + CSI + b"0m\r\n"
tui += CSI + b"1;36m" + b"\xe2\x95\xb0" + b"\xe2\x94\x80" * 40 + b"\xe2\x95\xaf" + CSI + b"0m\r\n"
tui += CSI + b"8;3H"                                        # park cursor inside the box
fixture("altscreen-live-tui", 100, 30, "alt-screen box-drawing TUI parked in alt buffer (codex/claude shape)",
        bytes(tui))

# --- 4. DECAWM autowrap on/off ----------------------------------------------------------------
wrap = bytearray()
wrap += CSI + b"?7h" + b"A" * 100 + b"\r\n"      # wraps to a second row
wrap += CSI + b"?7l" + b"B" * 100 + b"\r\n"      # clamps at the right margin
wrap += CSI + b"?7h" + b"tail"
fixture("decawm-wrap", 40, 12, "DECAWM on (wrap) vs off (clamp) over the right margin", bytes(wrap))

# --- 5. SGR: 16-colour, 256-colour, truecolor, attributes -------------------------------------
sgr = bytearray(b"sgr matrix\r\n")
for base in (30, 90):
    for i in range(8):
        sgr += CSI + str(base + i).encode() + b"m" + ("c%d " % (base + i)).encode()
    sgr += CSI + b"0m\r\n"
for n in (0, 15, 33, 118, 196, 231, 244, 255):
    sgr += CSI + b"38;5;" + str(n).encode() + b"m" + ("x%03d " % n).encode()
sgr += CSI + b"0m\r\n"
for rgb in ((255, 0, 0), (0, 255, 0), (0, 0, 255), (18, 52, 86), (240, 240, 240)):
    sgr += CSI + b"38;2;%d;%d;%dm" % rgb + b"T" + CSI + b"48;2;%d;%d;%dm" % rgb + b"B"
sgr += CSI + b"0m\r\n"
sgr += CSI + b"1mbold" + CSI + b"22m " + CSI + b"3mital" + CSI + b"23m "
sgr += CSI + b"4munder" + CSI + b"24m " + CSI + b"7mrev" + CSI + b"27m "
sgr += CSI + b"9mstrike" + CSI + b"29m" + CSI + b"0m\r\ndone"
fixture("sgr-256-truecolor", 90, 20, "SGR 16/bright/256/truecolor fg+bg and attribute toggles", bytes(sgr))

# --- 6. ED / EL erase variants ----------------------------------------------------------------
ed = bytearray()
for r in range(1, 11):
    ed += ("row%02d " % r).encode() + b"#" * 30 + b"\r\n"
ed += CSI + b"3;10H" + CSI + b"0K"    # erase to end of line
ed += CSI + b"5;10H" + CSI + b"1K"    # erase to start of line
ed += CSI + b"7;1H" + CSI + b"2K"     # erase whole line
ed += CSI + b"9;5H" + CSI + b"0J"     # erase below
ed += CSI + b"1;1H" + CSI + b"1J"     # erase above
fixture("erase-ed-el", 60, 14, "EL 0/1/2 and ED 0/1 against a painted grid", bytes(ed))

# --- 7. DECSTBM scroll regions + IL/DL/scroll ---------------------------------------------------
stbm = bytearray(CSI + b"2J" + CSI + b"H")
for r in range(1, 21):
    stbm += ("line-%02d" % r).encode() + b"\r\n"
stbm += CSI + b"5;15r"          # scroll region rows 5..15
stbm += CSI + b"15;1H"
stbm += b"\r\n" * 4             # scroll the region up 4
stbm += CSI + b"8;1H" + CSI + b"3L"   # insert 3 lines inside the region
stbm += CSI + b"12;1H" + CSI + b"2M"  # delete 2 lines inside the region
stbm += CSI + b"5;1H" + CSI + b"2S"   # SU
stbm += CSI + b"5;1H" + CSI + b"1T"   # SD
stbm += CSI + b"r"              # reset region
stbm += CSI + b"20;1H" + b"after-region"
fixture("decstbm-regions", 60, 24, "DECSTBM region with IL/DL/SU/SD then region reset", bytes(stbm))

# --- 8. cursor motion: CUP/CUU/CUD/CUF/CUB/CHA/VPA/HVP + save/restore ---------------------------
cur = bytearray(CSI + b"2J" + CSI + b"H")
cur += CSI + b"5;5H" + b"P1"
cur += CSI + b"3A" + b"U3"          # CUU
cur += CSI + b"6B" + b"D6"          # CUD
cur += CSI + b"10C" + b"F10"        # CUF
cur += CSI + b"4D" + b"B4"          # CUB
cur += CSI + b"20G" + b"CHA20"      # CHA
cur += CSI + b"12d" + b"VPA12"      # VPA
cur += CSI + b"2;30f" + b"HVP"      # HVP
cur += ESC + b"7"                   # DECSC
cur += CSI + b"15;15H" + b"SAVED"
cur += ESC + b"8" + b"RESTORED"     # DECRC
cur += CSI + b"18;1H" + b"\x1bMreverse-index"   # RI at top of its own line
cur += CSI + b"20;1H" + b"tab:\tA\tB\tC"
fixture("cursor-motion", 80, 24, "CUP/CUU/CUD/CUF/CUB/CHA/VPA/HVP, DECSC/DECRC, RI, HT", bytes(cur))

# --- 9. DECCKM (?1) — cursor key mode, replayed so a late viewer picks SS3 vs CSI arrows --------
ckm = bytearray(b"deccm off\r\n")
ckm += CSI + b"?1h" + b"deccm on (SS3 arrows)\r\n"
ckm += CSI + b"?1l" + b"deccm off (CSI arrows)\r\n"
ckm += CSI + b"?1h" + b"left on for the viewer"
fixture("decckm-1", 60, 10, "DECCKM set/reset — arrow-key encoding a replayed viewer must inherit",
        bytes(ckm))

# --- 10. OSC 0 / 2 title strings, both BEL and ST terminated -----------------------------------
osc = bytearray()
osc += ESC + b"]0;title-via-osc0\x07" + b"after osc0 bel\r\n"
osc += ESC + b"]2;title-via-osc2\x1b\\" + b"after osc2 st\r\n"
osc += ESC + b"]0;second\x07" + b"tail line"
fixture("osc-title", 60, 10, "OSC 0/2 window title, BEL- and ST-terminated", bytes(osc))

# --- 11. UTF-8: wide CJK, emoji, combining marks, box drawing -----------------------------------
uni = bytearray()
uni += "ascii baseline\r\n".encode()
uni += "CJK 日本語テキストの行 幅は二倍\r\n".encode()
uni += "emoji 🚀🔥✅🎯 mixed\r\n".encode()
uni += "combining é ä ñ done\r\n".encode()
uni += "box ┌───┬───┐ │ x │ y │ └───┴───┘\r\n".encode()
uni += "arrows ←↑→↓ ⇧⌘⌥ ✓✗\r\n".encode()
uni += "wide-at-margin ".encode() + "漢".encode() * 30
fixture("utf8-wide-emoji", 50, 20, "wide CJK, emoji, combining marks, box drawing, wide char at margin",
        bytes(uni))

# --- 12. progress-bar carriage-return churn — what a real codex/claude run emits ---------------
prog = bytearray(b"installing\r\n")
for pct in range(0, 101, 5):
    bar = b"#" * (pct // 5) + b"." * (20 - pct // 5)
    prog += b"\r" + CSI + b"2K" + b"[" + bar + b"] " + ("%3d%%" % pct).encode()
prog += b"\r\ndone\r\n"
for i in range(30):
    prog += CSI + b"1A" + b"\r" + CSI + b"2K" + ("redraw %d" % i).encode() + b"\r\n"
fixture("progress-cr-churn", 60, 16, "CR/EL2 progress bar plus CUU-based in-place redraw", bytes(prog))

# --- 13. throughput corpus — a large scrolling log, the ingest-rate measurement -----------------
big = bytearray()
for i in range(20000):
    big += CSI + b"32m" + ("%06d" % i).encode() + CSI + b"0m"
    big += b" the quick brown fox jumps over the lazy dog "
    big += CSI + b"33m" + ("0x%04x" % (i & 0xFFFF)).encode() + CSI + b"0m\r\n"
fixture("throughput-scroll-log", 120, 40, "~1.6MB coloured scrolling log — ingest throughput probe",
        bytes(big))

# --- 14. reflow bait: long soft-wrapped paragraphs at 140 cols ---------------------------------
para = bytearray(CSI + b"?7h")
words = ("terminal reflow is the property that a soft-wrapped logical line re-breaks when the "
         "viewport width changes instead of being truncated or left stranded at the old width ")
for i in range(12):
    para += ("para%02d " % i).encode() + (words * 3).encode()[:520] + b"\r\n"
fixture("reflow-paragraphs", 140, 40, "long soft-wrapped paragraphs — the reflow-on-resize probe",
        bytes(para))


def main():
    os.makedirs(OUT, exist_ok=True)
    total = 0
    for f in FIXTURES:
        body = f["body"]
        with open(os.path.join(OUT, f["name"] + ".bin"), "wb") as fh:
            fh.write(body)
        with open(os.path.join(OUT, f["name"] + ".json"), "w", encoding="utf-8") as fh:
            json.dump({
                "name": f["name"],
                "corpus": "synthetic",
                "cols": f["cols"],
                "rows": f["rows"],
                "bytes": len(body),
                "source": "gen_synthetic.py",
                "desc": f["desc"],
            }, fh, indent=2)
        total += len(body)
        print("wrote %-28s %8d bytes" % (f["name"], len(body)))
    print("%d synthetic fixtures, %d bytes total -> %s" % (len(FIXTURES), total, OUT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
