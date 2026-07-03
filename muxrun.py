# muxrun - visible local terminal owner for multiplex sessions.
#
# The agent runs in this console with inherited stdio. muxrun is only a sidecar:
# it registers the terminal with muxd, mirrors the visible screen to remote viewers,
# and writes remote keystrokes back into this console's input buffer.
import argparse
import asyncio
import base64
import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

try:
    import websockets
except ImportError:
    print("muxrun needs: pip install websockets", file=sys.stderr)
    sys.exit(1)

URL = "ws://127.0.0.1:" + os.environ.get("MUXCTL_PORT", "7699")
TASK_NAME = os.environ.get("MUXD_TASK", "MuxdSessionHost")
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)

STD_INPUT_HANDLE = -10
STD_OUTPUT_HANDLE = -11
ENABLE_PROCESSED_OUTPUT = 0x0001
ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002
ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004
ENABLE_QUICK_EDIT_MODE = 0x0040
ENABLE_EXTENDED_FLAGS = 0x0080


class COORD(ctypes.Structure):
    _fields_ = [("X", ctypes.c_short), ("Y", ctypes.c_short)]


class SMALL_RECT(ctypes.Structure):
    _fields_ = [
        ("Left", ctypes.c_short),
        ("Top", ctypes.c_short),
        ("Right", ctypes.c_short),
        ("Bottom", ctypes.c_short),
    ]


class CONSOLE_SCREEN_BUFFER_INFO(ctypes.Structure):
    _fields_ = [
        ("dwSize", COORD),
        ("dwCursorPosition", COORD),
        ("wAttributes", ctypes.c_ushort),
        ("srWindow", SMALL_RECT),
        ("dwMaximumWindowSize", COORD),
    ]


class CHAR_UNION(ctypes.Union):
    _fields_ = [("UnicodeChar", ctypes.c_wchar), ("AsciiChar", ctypes.c_char)]


class KEY_EVENT_RECORD(ctypes.Structure):
    _fields_ = [
        ("bKeyDown", wintypes.BOOL),
        ("wRepeatCount", wintypes.WORD),
        ("wVirtualKeyCode", wintypes.WORD),
        ("wVirtualScanCode", wintypes.WORD),
        ("uChar", CHAR_UNION),
        ("dwControlKeyState", wintypes.DWORD),
    ]


class EVENT_UNION(ctypes.Union):
    _fields_ = [("KeyEvent", KEY_EVENT_RECORD)]


class INPUT_RECORD(ctypes.Structure):
    _fields_ = [("EventType", wintypes.WORD), ("Event", EVENT_UNION)]


KEY_EVENT = 0x0001
VK_BACK = 0x08
VK_TAB = 0x09
VK_RETURN = 0x0D
VK_ESCAPE = 0x1B


def _run_quiet(args, timeout=6):
    return subprocess.run(
        args,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=timeout,
        creationflags=CREATE_NO_WINDOW,
    )


def ensure_muxd_started():
    if os.name != "nt":
        return
    try:
        q = _run_quiet(["schtasks", "/Query", "/TN", TASK_NAME, "/FO", "CSV", "/NH"])
        if q.returncode == 0 and "Running" not in q.stdout:
            _run_quiet(["schtasks", "/Run", "/TN", TASK_NAME])
            time.sleep(1.2)
    except Exception:
        pass


def enable_console_modes():
    if os.name != "nt":
        return
    k = ctypes.windll.kernel32
    hout = k.GetStdHandle(STD_OUTPUT_HANDLE)
    out_mode = ctypes.c_uint()
    if k.GetConsoleMode(hout, ctypes.byref(out_mode)):
        k.SetConsoleMode(
            hout,
            out_mode.value
            | ENABLE_PROCESSED_OUTPUT
            | ENABLE_WRAP_AT_EOL_OUTPUT
            | ENABLE_VIRTUAL_TERMINAL_PROCESSING,
        )
    hin = k.GetStdHandle(STD_INPUT_HANDLE)
    in_mode = ctypes.c_uint()
    if k.GetConsoleMode(hin, ctypes.byref(in_mode)):
        # Disable QuickEdit so clicking the local terminal cannot freeze the process.
        k.SetConsoleMode(hin, (in_mode.value | ENABLE_EXTENDED_FLAGS) & ~ENABLE_QUICK_EDIT_MODE)


def visible_size():
    if os.name != "nt":
        try:
            sz = os.get_terminal_size()
            return sz.columns, sz.lines
        except OSError:
            return 120, 30
    k = ctypes.windll.kernel32
    info = CONSOLE_SCREEN_BUFFER_INFO()
    h = k.GetStdHandle(STD_OUTPUT_HANDLE)
    if k.GetConsoleScreenBufferInfo(h, ctypes.byref(info)):
        return (
            int(info.srWindow.Right - info.srWindow.Left + 1),
            int(info.srWindow.Bottom - info.srWindow.Top + 1),
        )
    return 120, 30


def read_visible_screen():
    if os.name != "nt":
        return visible_size()[0], visible_size()[1], ""
    k = ctypes.windll.kernel32
    h = k.GetStdHandle(STD_OUTPUT_HANDLE)
    info = CONSOLE_SCREEN_BUFFER_INFO()
    if not k.GetConsoleScreenBufferInfo(h, ctypes.byref(info)):
        cols, rows = visible_size()
        return cols, rows, ""
    left, top = int(info.srWindow.Left), int(info.srWindow.Top)
    cols = int(info.srWindow.Right - info.srWindow.Left + 1)
    rows = int(info.srWindow.Bottom - info.srWindow.Top + 1)
    lines = []
    read = wintypes.DWORD()
    for y in range(rows):
        buf = ctypes.create_unicode_buffer(cols)
        coord = COORD(left, top + y)
        ok = k.ReadConsoleOutputCharacterW(h, buf, cols, coord, ctypes.byref(read))
        if not ok:
            lines.append("")
        else:
            lines.append(buf.value[: int(read.value)].rstrip())
    return cols, rows, "\r\n".join(lines)


def repaint_frame(cols, rows, text):
    # Remote is a mirror, so repainting the visible screen is acceptable. Local output
    # is not touched; the child owns the local terminal directly.
    return ("\x1b[?25l\x1b[2J\x1b[H" + text + "\x1b[?25h").encode("utf-8", "replace")


def vk_for_char(ch):
    if ch in ("\r", "\n"):
        return VK_RETURN, "\r"
    if ch == "\b":
        return VK_BACK, "\b"
    if ch == "\t":
        return VK_TAB, "\t"
    if ch == "\x1b":
        return VK_ESCAPE, "\x1b"
    return 0, ch


def write_console_input(data: bytes):
    if os.name != "nt" or not data:
        return
    text = data.decode("utf-8", "replace")
    k = ctypes.windll.kernel32
    hin = k.GetStdHandle(STD_INPUT_HANDLE)
    records = []
    for ch in text:
        vk, out_ch = vk_for_char(ch)
        for down in (True, False):
            rec = INPUT_RECORD()
            rec.EventType = KEY_EVENT
            rec.Event.KeyEvent.bKeyDown = bool(down)
            rec.Event.KeyEvent.wRepeatCount = 1
            rec.Event.KeyEvent.wVirtualKeyCode = vk
            rec.Event.KeyEvent.wVirtualScanCode = 0
            rec.Event.KeyEvent.uChar.UnicodeChar = out_ch
            rec.Event.KeyEvent.dwControlKeyState = 0
            records.append(rec)
    if not records:
        return
    arr = (INPUT_RECORD * len(records))(*records)
    written = wintypes.DWORD()
    k.WriteConsoleInputW(hin, arr, len(records), ctypes.byref(written))


async def listen_remote(ws, child):
    async for raw in ws:
        try:
            import json

            m = json.loads(raw if isinstance(raw, str) else raw.decode("utf-8", "replace"))
        except Exception:
            continue
        if m.get("t") == "i":
            write_console_input(base64.b64decode(m.get("d", "")))
        elif m.get("t") == "kill":
            try:
                child.terminate()
            except Exception:
                pass
            return


async def screen_pump(ws, stop):
    import json

    last = None
    last_size = None
    while not stop.is_set():
        cols, rows, text = read_visible_screen()
        size = (cols, rows)
        if size != last_size:
            await ws.send(json.dumps({"t": "size", "cols": cols, "rows": rows}))
            last_size = size
        if text != last:
            frame = repaint_frame(cols, rows, text)
            await ws.send(json.dumps({"t": "o", "d": base64.b64encode(frame).decode("ascii")}))
            last = text
        await asyncio.sleep(0.12)


async def wait_child(child):
    while child.poll() is None:
        await asyncio.sleep(0.2)
    return int(child.returncode or 0)


async def register_owner(args, command, cwd):
    import json

    ws = await websockets.connect(URL, max_size=8_000_000, ping_interval=20, ping_timeout=15)
    try:
        cols, rows = visible_size()
        await ws.send(
            json.dumps(
                {
                    "t": "owner",
                    "s": args.name,
                    "cmd": command,
                    "cwd": cwd,
                    "cols": cols,
                    "rows": rows,
                }
            )
        )
        first = json.loads(await asyncio.wait_for(ws.recv(), 8))
        if first.get("t") == "err":
            raise RuntimeError(first.get("m", "registration failed"))
        return ws
    except Exception:
        try:
            await ws.close()
        except Exception:
            pass
        raise


async def connect_owner(args, command, cwd, child_started):
    deadline = time.monotonic() + (float("inf") if child_started else 20.0)
    backoff = 0.5
    last_error = None
    while time.monotonic() < deadline:
        ensure_muxd_started()
        try:
            return await register_owner(args, command, cwd)
        except RuntimeError as e:
            last_error = e
            msg = str(e)
            if "already has a visible local owner" in msg and not child_started:
                raise
            print("[muxrun] owner registration failed; retrying: " + msg, file=sys.stderr)
        except Exception as e:
            last_error = e
            print("[muxrun] muxd owner link unavailable; retrying: " + str(e), file=sys.stderr)
        await asyncio.sleep(backoff)
        backoff = min(5.0, backoff * 1.6)
    if last_error:
        raise last_error
    raise RuntimeError("timed out registering with muxd")


async def run_owner_link(ws, child):
    import json

    stop = asyncio.Event()
    tasks = [
        asyncio.create_task(listen_remote(ws, child)),
        asyncio.create_task(screen_pump(ws, stop)),
        asyncio.create_task(wait_child(child)),
    ]
    try:
        done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        if tasks[2] in done:
            try:
                await ws.send(json.dumps({"t": "dead"}))
            except Exception:
                pass
            return "child-exit"
        await asyncio.sleep(0.3)
        if child.poll() is not None:
            try:
                await ws.send(json.dumps({"t": "dead"}))
            except Exception:
                pass
            return "child-exit"
        return "link-drop"
    finally:
        stop.set()
        for task in tasks:
            task.cancel()
        try:
            await ws.close()
        except Exception:
            pass


def child_args(command):
    if command:
        encoded = base64.b64encode(command.encode("utf-16le")).decode("ascii")
        return ["powershell.exe", "-NoLogo", "-NoExit", "-EncodedCommand", encoded]
    return ["powershell.exe", "-NoLogo"]


async def main_async(args):
    ensure_muxd_started()
    enable_console_modes()
    command = args.cmd or ""
    if args.cmd_b64:
        command = base64.b64decode(args.cmd_b64).decode("utf-8", "replace")
    cwd = args.cwd if args.cwd and os.path.isdir(args.cwd) else os.getcwd()

    try:
        ws = await connect_owner(args, command, cwd, child_started=False)
    except Exception as e:
        print("[muxrun] " + str(e), file=sys.stderr)
        return 2

    child = subprocess.Popen(child_args(command), cwd=cwd)
    while child.poll() is None:
        try:
            result = await run_owner_link(ws, child)
            if result == "child-exit":
                break
            print("[muxrun] muxd owner link dropped; re-registering while child continues", file=sys.stderr)
            ws = await connect_owner(args, command, cwd, child_started=True)
        except Exception as e:
            if child.poll() is not None:
                break
            print("[muxrun] muxd owner link failed; re-registering while child continues: " + str(e), file=sys.stderr)
            ws = await connect_owner(args, command, cwd, child_started=True)
    return int(child.returncode or 0)


def main():
    p = argparse.ArgumentParser(description="Run a visible local terminal as a multiplex-owned session.")
    p.add_argument("name")
    p.add_argument("--cwd", default="")
    p.add_argument("--cmd", default="")
    p.add_argument("--cmd-b64", default="")
    args = p.parse_args()
    try:
        raise SystemExit(asyncio.run(main_async(args)))
    except KeyboardInterrupt:
        raise SystemExit(130)


if __name__ == "__main__":
    main()
