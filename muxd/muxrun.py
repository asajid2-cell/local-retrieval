# muxrun - visible local terminal owner for multiplex sessions.
#
# The agent runs in this console with inherited stdio. muxrun is only a sidecar:
# it registers the terminal with muxd, mirrors the visible screen to remote viewers,
# and writes remote keystrokes back into this console's input buffer.
import argparse
import asyncio
import atexit
import base64
import ctypes
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes

from profile import PROFILE, ProfileError


def _consume_profile_arg(argv):
    args = list(argv)
    if "--profile" not in args:
        if PROFILE.name != "production":
            raise ProfileError("non-production muxrun requires --profile <profile> identity")
        return args
    index = args.index("--profile")
    if index + 1 >= len(args) or args[index + 1] != PROFILE.name:
        raise ProfileError("--profile does not match MUXD_PROFILE")
    return args[:index] + args[index + 2:]


if PROFILE.name == "production":
    URL = "ws://127.0.0.1:" + os.environ.get("MUXCTL_PORT", "7699")
    TASK_NAME = os.environ.get("MUXD_TASK", "MuxdSessionHost")
else:
    URL = PROFILE.control_url
    TASK_NAME = PROFILE.task_name
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)

try:
    import websockets
except ImportError:
    print("muxrun needs: pip install websockets", file=sys.stderr)
    sys.exit(1)

class OwnerSupersededError(RuntimeError):
    pass

STD_INPUT_HANDLE = -10
STD_OUTPUT_HANDLE = -11
ENABLE_PROCESSED_OUTPUT = 0x0001
ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002
ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004
ENABLE_QUICK_EDIT_MODE = 0x0040
ENABLE_EXTENDED_FLAGS = 0x0080
ENABLE_PROCESSED_INPUT = 0x0001
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9
TH32CS_SNAPPROCESS = 0x00000002
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
CTRL_C_EVENT = 0
LEFT_ALT_PRESSED = 0x0002
LEFT_CTRL_PRESSED = 0x0008
SHIFT_PRESSED = 0x0010
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_TERMINATE = 0x0001
SYNCHRONIZE = 0x00100000
STILL_ACTIVE = 259
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258


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


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", wintypes.LONG),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", ctypes.c_wchar * 260),
    ]


class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_int64),
        ("PerJobUserTimeLimit", ctypes.c_int64),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_uint64),
        ("WriteOperationCount", ctypes.c_uint64),
        ("OtherOperationCount", ctypes.c_uint64),
        ("ReadTransferCount", ctypes.c_uint64),
        ("WriteTransferCount", ctypes.c_uint64),
        ("OtherTransferCount", ctypes.c_uint64),
    ]


class JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION),
        ("IoInfo", IO_COUNTERS),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


class ChildJob:
    def __init__(self):
        self.handle = None
        if os.name != "nt":
            return
        k = ctypes.windll.kernel32
        k.CreateJobObjectW.argtypes = [wintypes.LPVOID, wintypes.LPCWSTR]
        k.CreateJobObjectW.restype = wintypes.HANDLE
        k.SetInformationJobObject.argtypes = [
            wintypes.HANDLE,
            ctypes.c_int,
            wintypes.LPVOID,
            wintypes.DWORD,
        ]
        k.SetInformationJobObject.restype = wintypes.BOOL
        k.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        k.AssignProcessToJobObject.restype = wintypes.BOOL
        k.CloseHandle.argtypes = [wintypes.HANDLE]
        k.CloseHandle.restype = wintypes.BOOL
        handle = k.CreateJobObjectW(None, None)
        if not handle:
            raise ctypes.WinError()
        info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not k.SetInformationJobObject(
            handle,
            JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
            ctypes.byref(info),
            ctypes.sizeof(info),
        ):
            k.CloseHandle(handle)
            raise ctypes.WinError()
        self.handle = handle

    def assign(self, child):
        if self.handle is None:
            return
        if not ctypes.windll.kernel32.AssignProcessToJobObject(self.handle, wintypes.HANDLE(child._handle)):
            raise ctypes.WinError()

    def close(self):
        if self.handle is not None:
            ctypes.windll.kernel32.CloseHandle(self.handle)
            self.handle = None


class AttachedChild:
    """Process-like wrapper for a console process muxrun did not launch."""

    def __init__(self, pid):
        self.pid = int(pid)
        self.returncode = None
        self._handle = None
        self.tree_stop_in_progress = False
        self.tree_stop_error = ""
        self._pending_descendants = []
        if os.name == "nt":
            k = ctypes.windll.kernel32
            k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            k.OpenProcess.restype = wintypes.HANDLE
            self._handle = k.OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE,
                False,
                self.pid,
            )
            if not self._handle:
                raise ctypes.WinError()

    def poll(self):
        if self.returncode is not None:
            return self.returncode
        if os.name != "nt":
            try:
                os.kill(self.pid, 0)
                return None
            except OSError:
                self.returncode = 0
                return self.returncode
        k = ctypes.windll.kernel32
        code = wintypes.DWORD()
        if not k.GetExitCodeProcess(self._handle, ctypes.byref(code)):
            raise ctypes.WinError()
        if code.value != STILL_ACTIVE:
            self.returncode = int(code.value)
            self.close()
            return self.returncode
        return None

    def terminate(self):
        if self.poll() is not None:
            return
        if os.name != "nt":
            os.kill(self.pid, 15)
            return
        if not ctypes.windll.kernel32.TerminateProcess(self._handle, 1):
            raise ctypes.WinError()

    def creation_time(self):
        if os.name != "nt" or not self._handle:
            return 0
        k = ctypes.windll.kernel32
        created = wintypes.FILETIME()
        exited = wintypes.FILETIME()
        kernel = wintypes.FILETIME()
        user = wintypes.FILETIME()
        if not k.GetProcessTimes(
            self._handle,
            ctypes.byref(created),
            ctypes.byref(exited),
            ctypes.byref(kernel),
            ctypes.byref(user),
        ):
            raise ctypes.WinError()
        return (int(created.dwHighDateTime) << 32) | int(created.dwLowDateTime)

    def wait(self, timeout=None):
        if self.returncode is not None:
            return self.returncode
        if os.name != "nt":
            deadline = None if timeout is None else time.monotonic() + max(0.0, timeout)
            while self.poll() is None:
                if deadline is not None and time.monotonic() >= deadline:
                    raise subprocess.TimeoutExpired(str(self.pid), timeout)
                time.sleep(0.05)
            return self.returncode
        milliseconds = 0xFFFFFFFF if timeout is None else max(0, int(timeout * 1000))
        result = ctypes.windll.kernel32.WaitForSingleObject(self._handle, milliseconds)
        if result == WAIT_TIMEOUT:
            raise subprocess.TimeoutExpired(str(self.pid), timeout)
        if result != WAIT_OBJECT_0:
            raise ctypes.WinError()
        return self.poll()

    def close(self):
        for process in getattr(self, "_pending_descendants", []):
            try:
                process.close()
            except Exception:
                pass
        self._pending_descendants = []
        if self._handle:
            ctypes.windll.kernel32.CloseHandle(self._handle)
            self._handle = None

    def retain_live_descendants(self, descendants):
        retained = []
        for process in descendants:
            try:
                if process.poll() is None:
                    retained.append(process)
                else:
                    process.close()
            except Exception:
                retained.append(process)
        self._pending_descendants = retained

    def pending_descendants_alive(self):
        self.retain_live_descendants(getattr(self, "_pending_descendants", []))
        return bool(getattr(self, "_pending_descendants", []))

    def tree_exit_confirmed(self):
        return (
            self.poll() is not None
            and not getattr(self, "tree_stop_in_progress", False)
            and not self.pending_descendants_alive()
        )


def attach_existing_console(pid):
    if os.name != "nt":
        raise RuntimeError("retroactive console attach is Windows-only")
    k = ctypes.windll.kernel32
    k.FreeConsole()
    if not k.AttachConsole(int(pid)):
        raise ctypes.WinError()


def open_attached_console(pid):
    child = AttachedChild(pid)
    try:
        attach_existing_console(pid)
        return child
    except Exception:
        child.close()
        raise


def process_snapshot_edges():
    if os.name != "nt":
        return []
    k = ctypes.windll.kernel32
    k.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    k.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    k.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    k.Process32FirstW.restype = wintypes.BOOL
    k.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    k.Process32NextW.restype = wintypes.BOOL
    snapshot = k.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snapshot == INVALID_HANDLE_VALUE:
        raise ctypes.WinError()
    edges = []
    entry = PROCESSENTRY32W()
    entry.dwSize = ctypes.sizeof(entry)
    try:
        ok = bool(k.Process32FirstW(snapshot, ctypes.byref(entry)))
        while ok:
            edges.append((
                int(entry.th32ProcessID),
                int(entry.th32ParentProcessID),
                str(entry.szExeFile or ""),
            ))
            ok = bool(k.Process32NextW(snapshot, ctypes.byref(entry)))
    finally:
        k.CloseHandle(snapshot)
    return edges


def descendant_pid_candidates(root_pid, edges):
    children = {}
    for row in edges:
        pid, parent_pid = row[:2]
        children.setdefault(int(parent_pid), []).append(int(pid))
    found = []
    seen = {int(root_pid)}
    pending = [int(root_pid)]
    while pending:
        parent = pending.pop(0)
        for pid in children.get(parent, ()):
            if pid in seen:
                continue
            seen.add(pid)
            found.append(pid)
            pending.append(pid)
    return found


def verified_descendant_pids(root_pid, root_created, rows):
    children = {}
    created = {}
    for pid, parent_pid, created_at in rows:
        children.setdefault(int(parent_pid), []).append(int(pid))
        created[int(pid)] = int(created_at)
    verified = []
    seen = {int(root_pid)}
    pending = [(int(root_pid), int(root_created))]
    while pending:
        parent_pid, parent_created = pending.pop(0)
        for pid in children.get(parent_pid, ()):
            if pid in seen:
                continue
            seen.add(pid)
            child_created = created.get(pid, 0)
            if child_created < parent_created:
                continue
            verified.append(pid)
            pending.append((pid, child_created))
    return verified


def pin_verified_descendants(root):
    root_created = root.creation_time()
    edges = process_snapshot_edges()
    parent_by_pid = {int(row[0]): int(row[1]) for row in edges}
    image_by_pid = {
        int(row[0]): str(row[2]).lower()
        for row in edges
        if len(row) > 2
    }
    pinned = {}
    rows = []
    for pid in descendant_pid_candidates(root.pid, edges):
        # Console hosts remain alive while this sidecar is attached to the console. Waiting for
        # them before the sidecar detaches is a lifecycle cycle, and they are not user workloads.
        if image_by_pid.get(pid) in {"conhost.exe", "openconsole.exe", "winpty-agent.exe"}:
            continue
        process = None
        try:
            process = AttachedChild(pid)
            if process.poll() is not None:
                process.close()
                continue
            created = process.creation_time()
        except OSError as error:
            try:
                if process is not None:
                    process.close()
            except Exception:
                pass
            # ERROR_INVALID_PARAMETER means the snapshot raced with a normal exit.
            # AccessDenied and every other open/query failure mean we cannot prove the
            # descendant was terminated, so Stop must fail instead of acknowledging it.
            if getattr(error, "winerror", None) == 87:
                continue
            raise RuntimeError(f"could not pin descendant pid {pid}: {error}") from error
        except Exception as error:
            try:
                if process is not None:
                    process.close()
            except Exception:
                pass
            raise RuntimeError(f"could not pin descendant pid {pid}: {error}") from error
        pinned[pid] = process
        process.image_name = image_by_pid.get(pid, "")
        rows.append((pid, parent_by_pid.get(pid, 0), created))
    verified = verified_descendant_pids(root.pid, root_created, rows)
    for pid in list(pinned):
        if pid not in verified:
            pinned.pop(pid).close()
    return [pinned[pid] for pid in verified if pid in pinned]


KEY_EVENT = 0x0001
VK_BACK = 0x08
VK_TAB = 0x09
VK_RETURN = 0x0D
VK_ESCAPE = 0x1B
VK_SPACE = 0x20
VK_PRIOR = 0x21
VK_NEXT = 0x22
VK_END = 0x23
VK_HOME = 0x24
VK_LEFT = 0x25
VK_UP = 0x26
VK_RIGHT = 0x27
VK_DOWN = 0x28
VK_INSERT = 0x2D
VK_DELETE = 0x2E

VT_KEY_SEQUENCES = {
    b"\x1b[A": (VK_UP, "", 0),
    b"\x1b[B": (VK_DOWN, "", 0),
    b"\x1b[C": (VK_RIGHT, "", 0),
    b"\x1b[D": (VK_LEFT, "", 0),
    b"\x1b[H": (VK_HOME, "", 0),
    b"\x1b[F": (VK_END, "", 0),
    b"\x1bOA": (VK_UP, "", 0),
    b"\x1bOB": (VK_DOWN, "", 0),
    b"\x1bOC": (VK_RIGHT, "", 0),
    b"\x1bOD": (VK_LEFT, "", 0),
    b"\x1bOH": (VK_HOME, "", 0),
    b"\x1bOF": (VK_END, "", 0),
    b"\x1b[1~": (VK_HOME, "", 0),
    b"\x1b[2~": (VK_INSERT, "", 0),
    b"\x1b[3~": (VK_DELETE, "", 0),
    b"\x1b[4~": (VK_END, "", 0),
    b"\x1b[5~": (VK_PRIOR, "", 0),
    b"\x1b[6~": (VK_NEXT, "", 0),
    b"\x1b[Z": (VK_TAB, "\t", SHIFT_PRESSED),
}


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


def restore_mode_bits(current, original, changed_mask):
    return (int(current) & ~int(changed_mask)) | (int(original) & int(changed_mask))


class ConsoleModeState:
    def __init__(self, hin=None, in_mode=None, in_changed=0, hout=None, out_mode=None, out_changed=0):
        self.hin = hin
        self.in_mode = in_mode
        self.in_changed = int(in_changed)
        self.hout = hout
        self.out_mode = out_mode
        self.out_changed = int(out_changed)
        self.restored = False

    def restore(self):
        if self.restored or os.name != "nt":
            self.restored = True
            return
        self.restored = True
        k = ctypes.windll.kernel32
        if self.hin is not None and self.in_mode is not None:
            current = ctypes.c_uint()
            if k.GetConsoleMode(self.hin, ctypes.byref(current)):
                k.SetConsoleMode(
                    self.hin,
                    restore_mode_bits(current.value, self.in_mode, self.in_changed),
                )
        if self.hout is not None and self.out_mode is not None:
            current = ctypes.c_uint()
            if k.GetConsoleMode(self.hout, ctypes.byref(current)):
                k.SetConsoleMode(
                    self.hout,
                    restore_mode_bits(current.value, self.out_mode, self.out_changed),
                )


def enable_console_modes():
    if os.name != "nt":
        return ConsoleModeState()
    k = ctypes.windll.kernel32
    hout = k.GetStdHandle(STD_OUTPUT_HANDLE)
    out_mode = ctypes.c_uint()
    have_out = bool(k.GetConsoleMode(hout, ctypes.byref(out_mode)))
    next_out = out_mode.value
    if have_out:
        next_out = (
            out_mode.value
            | ENABLE_PROCESSED_OUTPUT
            | ENABLE_WRAP_AT_EOL_OUTPUT
            | ENABLE_VIRTUAL_TERMINAL_PROCESSING
        )
        k.SetConsoleMode(
            hout,
            next_out,
        )
    hin = k.GetStdHandle(STD_INPUT_HANDLE)
    in_mode = ctypes.c_uint()
    have_in = bool(k.GetConsoleMode(hin, ctypes.byref(in_mode)))
    next_in = in_mode.value
    if have_in:
        # Disable QuickEdit so clicking the local terminal cannot freeze the process.
        next_in = (in_mode.value | ENABLE_EXTENDED_FLAGS) & ~ENABLE_QUICK_EDIT_MODE
        k.SetConsoleMode(hin, next_in)
    return ConsoleModeState(
        hin if have_in else None,
        in_mode.value if have_in else None,
        in_mode.value ^ next_in if have_in else 0,
        hout if have_out else None,
        out_mode.value if have_out else None,
        out_mode.value ^ next_out if have_out else 0,
    )


def set_console_ctrl_ignored(ignored):
    if os.name != "nt":
        return
    if not ctypes.windll.kernel32.SetConsoleCtrlHandler(None, bool(ignored)):
        raise ctypes.WinError()


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
        return VK_RETURN, "\r", 0
    if ch in ("\b", "\x7f"):
        return VK_BACK, "\b", 0
    if ch == "\t":
        return VK_TAB, "\t", 0
    if ch == "\x1b":
        return VK_ESCAPE, "\x1b", 0
    code = ord(ch)
    if 1 <= code <= 26:
        return 0x40 + code, ch, LEFT_CTRL_PRESSED
    if ch == " ":
        return VK_SPACE, ch, 0
    if "a" <= ch <= "z":
        return ord(ch.upper()), ch, 0
    if "A" <= ch <= "Z":
        return ord(ch), ch, SHIFT_PRESSED
    if "0" <= ch <= "9":
        return ord(ch), ch, 0
    return 0, ch, 0


def console_key_events(data: bytes):
    events = []
    offset = 0
    ordered_sequences = sorted(VT_KEY_SEQUENCES.items(), key=lambda item: len(item[0]), reverse=True)
    while offset < len(data):
        matched = False
        for sequence, event in ordered_sequences:
            if data.startswith(sequence, offset):
                events.append(event)
                offset += len(sequence)
                matched = True
                break
        if matched:
            continue
        if data[offset:offset + 1] == b"\x1b" and offset + 1 < len(data):
            next_byte = data[offset + 1]
            if 0x20 <= next_byte < 0x7F and next_byte not in (ord("["), ord("O")):
                vk, ch, ctrl = vk_for_char(chr(next_byte))
                events.append((vk, ch, ctrl | LEFT_ALT_PRESSED))
                offset += 2
                continue
        lead = data[offset]
        width = 1 if lead < 0x80 else 2 if lead < 0xE0 else 3 if lead < 0xF0 else 4
        chunk = data[offset:offset + width]
        try:
            ch = chunk.decode("utf-8")
            consumed = len(chunk)
        except UnicodeDecodeError:
            ch = "\ufffd"
            consumed = 1
        events.append(vk_for_char(ch))
        offset += consumed
    return events


def input_records(data: bytes):
    records = []
    for vk, out_ch, control_state in console_key_events(data):
        for down in (True, False):
            rec = INPUT_RECORD()
            rec.EventType = KEY_EVENT
            rec.Event.KeyEvent.bKeyDown = bool(down)
            rec.Event.KeyEvent.wRepeatCount = 1
            rec.Event.KeyEvent.wVirtualKeyCode = vk
            rec.Event.KeyEvent.wVirtualScanCode = 0
            rec.Event.KeyEvent.uChar.UnicodeChar = out_ch or "\x00"
            rec.Event.KeyEvent.dwControlKeyState = control_state
            records.append(rec)
    return records


def should_generate_ctrl_c(data, input_mode):
    return data == b"\x03" and bool(int(input_mode) & ENABLE_PROCESSED_INPUT)


def write_console_input(data: bytes):
    if os.name != "nt" or not data:
        return False
    k = ctypes.windll.kernel32
    hin = k.GetStdHandle(STD_INPUT_HANDLE)
    mode = ctypes.c_uint()
    if k.GetConsoleMode(hin, ctypes.byref(mode)) and should_generate_ctrl_c(data, mode.value):
        if k.GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0):
            return True
    records = input_records(data)
    if not records:
        return False
    arr = (INPUT_RECORD * len(records))(*records)
    written = wintypes.DWORD()
    ok = bool(k.WriteConsoleInputW(hin, arr, len(records), ctypes.byref(written)))
    return ok and int(written.value) == len(records)


async def listen_remote(ws, child):
    async for raw in ws:
        try:
            import json

            m = json.loads(raw if isinstance(raw, str) else raw.decode("utf-8", "replace"))
        except Exception:
            continue
        if m.get("t") == "i":
            ok = False
            try:
                ok = write_console_input(base64.b64decode(m.get("d", "")))
            except Exception:
                ok = False
            if m.get("rid"):
                await ws.send(json.dumps({
                    "t": "inputResult",
                    "rid": str(m.get("rid")),
                    "ok": bool(ok),
                }))
        elif m.get("t") == "kill":
            if isinstance(child, AttachedChild):
                child.tree_stop_in_progress = True
            try:
                stopped = await asyncio.get_running_loop().run_in_executor(
                    None, terminate_child_tree, child
                )
            finally:
                if isinstance(child, AttachedChild):
                    child.tree_stop_in_progress = False
            if not stopped:
                await ws.send(json.dumps({
                    "t": "killResult",
                    "ok": False,
                    "m": str(
                        getattr(child, "tree_stop_error", "")
                        or "one or more pinned process descendants remained alive"
                    )[:500],
                }))
                continue
            return


def terminate_child_tree(child):
    if isinstance(child, AttachedChild):
        child.tree_stop_error = ""
        root_alive = child.poll() is None
        descendants = list(getattr(child, "_pending_descendants", []))
        if root_alive:
            try:
                descendants.extend(pin_verified_descendants(child))
            except Exception as error:
                child.tree_stop_error = str(error)
                return False
        unique_descendants = []
        seen = set()
        for process in descendants:
            if process.pid == child.pid or process.pid in seen:
                continue
            seen.add(process.pid)
            unique_descendants.append(process)
        descendants = unique_descendants
        if not root_alive and not descendants:
            return True
        deadline = time.monotonic() + 8
        terminated = True
        failures = []
        processes = ([child] if root_alive else []) + descendants
        for process in processes:
            try:
                process.terminate()
            except Exception as error:
                terminated = False
                failures.append(f"terminate pid {process.pid}: {error}")
        for process in processes:
            try:
                process.wait(timeout=max(0.01, deadline - time.monotonic()))
                if process.poll() is None:
                    terminated = False
                    failures.append(f"pid {process.pid} remained alive")
            except (OSError, subprocess.TimeoutExpired) as error:
                terminated = False
                failures.append(f"wait pid {process.pid}: {error}")
        child.retain_live_descendants(descendants)
        confirmed = (
            terminated
            and child.poll() is not None
            and not child.pending_descendants_alive()
        )
        if not confirmed:
            for process in getattr(child, "_pending_descendants", []):
                label = str(getattr(process, "image_name", "") or "process")
                failures.append(f"{label} pid {process.pid} remained alive")
            child.tree_stop_error = "; ".join(dict.fromkeys(failures)) or (
                "one or more pinned process descendants remained alive"
            )
        return confirmed
    if child.poll() is not None:
        return True
    try:
        subprocess.run(
            ["taskkill.exe", "/PID", str(child.pid), "/T", "/F"],
            timeout=8,
            creationflags=CREATE_NO_WINDOW,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except Exception:
        try:
            child.terminate()
        except Exception:
            pass
    try:
        child.wait(timeout=8)
    except subprocess.TimeoutExpired:
        return False
    return child.poll() is not None


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
    while True:
        if child.poll() is None:
            await asyncio.sleep(0.2)
            continue
        if isinstance(child, AttachedChild) and (
            child.tree_stop_in_progress or child.pending_descendants_alive()
        ):
            await asyncio.sleep(0.2)
            continue
        break
    return int(child.returncode or 0)


async def register_owner(args, command, cwd, owner_key, child_pid=0):
    import json

    ws = await websockets.connect(URL, max_size=8_000_000, ping_interval=20, ping_timeout=15)
    try:
        cols, rows = visible_size()
        frame = {
            "t": "owner",
            "s": args.name,
            "cmd": command,
            "cwd": cwd,
            "cols": cols,
            "rows": rows,
            "ownerKey": owner_key,
            "childPid": int(child_pid or 0),
            "sessionId": args.session_id,
            "aliases": args.alias,
        }
        if args.attach_pid:
            frame.update({"adopted": True, "externalOwner": True, "heal": False})
        await ws.send(json.dumps(frame))
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


async def connect_owner(args, command, cwd, child_started, owner_key, child_pid=0, child=None):
    deadline = time.monotonic() + (float("inf") if child_started else 20.0)
    backoff = 0.5
    last_error = None
    while time.monotonic() < deadline:
        if child_started and child is not None and child.poll() is not None:
            raise RuntimeError("visible local child exited before owner registration completed")
        ensure_muxd_started()
        try:
            return await register_owner(args, command, cwd, owner_key, child_pid)
        except RuntimeError as e:
            last_error = e
            msg = str(e)
            if "already has a visible local owner" in msg and (
                not child_started or bool(args.attach_pid)
            ):
                raise OwnerSupersededError(msg) from None
            print("[muxrun] owner registration failed; retrying: " + msg, file=sys.stderr)
        except Exception as e:
            last_error = e
            print("[muxrun] muxd owner link unavailable; retrying: " + str(e), file=sys.stderr)
        if child_started and child is not None and child.poll() is not None:
            raise RuntimeError("visible local child exited before owner registration completed")
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
            return "child-exit"
        if tasks[0] in done:
            tasks[0].result()
        await asyncio.sleep(0.3)
        if child.poll() is not None:
            return "child-exit"
        return "link-drop"
    finally:
        stop.set()
        for task in tasks:
            task.cancel()


def child_args(command, start_gate):
    quoted_gate = start_gate.replace("'", "''")
    gate = (
        f"while(-not (Test-Path -LiteralPath '{quoted_gate}'))"
        "{Start-Sleep -Milliseconds 20}; "
        f"Remove-Item -LiteralPath '{quoted_gate}' -Force -ErrorAction SilentlyContinue; "
    )
    encoded = base64.b64encode((gate + command).encode("utf-16le")).decode("ascii")
    return ["powershell.exe", "-NoLogo", "-NoExit", "-EncodedCommand", encoded]


def launch_contained_child(command, cwd):
    fd, start_gate = tempfile.mkstemp(prefix="muxrun-start-", suffix=".gate")
    os.close(fd)
    os.remove(start_gate)
    job = ChildJob()
    child = None
    try:
        child = subprocess.Popen(child_args(command, start_gate), cwd=cwd)
        job.assign(child)
        with open(start_gate, "wb"):
            pass
        return child, job
    except Exception:
        if child is not None:
            try:
                child.kill()
                child.wait(timeout=5)
            except Exception:
                pass
        job.close()
        try:
            os.remove(start_gate)
        except OSError:
            pass
        raise


async def main_async(args):
    ensure_muxd_started()
    attached = bool(args.attach_pid)
    child = open_attached_console(args.attach_pid) if attached else None
    # The adopted console belongs to the user. Do not change its persistent input/output
    # modes: a force-killed sidecar would have no opportunity to restore them.
    console_modes = ConsoleModeState() if attached else enable_console_modes()
    restore_console = console_modes.restore
    atexit.register(restore_console)
    ctrl_ignored = False
    command = args.cmd or ""
    if args.cmd_b64:
        command = base64.b64decode(args.cmd_b64).decode("utf-8", "replace")
    cwd = args.cwd if args.cwd and os.path.isdir(args.cwd) else os.getcwd()
    owner_key = secrets.token_urlsafe(32)

    job = None
    ws = None
    try:
        if attached:
            set_console_ctrl_ignored(True)
            ctrl_ignored = True
        try:
            ws = await connect_owner(
                args,
                command,
                cwd,
                child_started=False,
                owner_key=owner_key,
                child_pid=child.pid if child is not None else 0,
            )
        except OwnerSupersededError as e:
            print("[muxrun] mirror superseded; exiting sidecar: " + str(e), file=sys.stderr)
            return 0
        except Exception as e:
            print("[muxrun] " + str(e), file=sys.stderr)
            return 2

        if child is None:
            child, job = launch_contained_child(command, cwd)
        while child.poll() is None:
            try:
                await ws.send(json.dumps({"t": "child", "pid": child.pid}))
                result = await run_owner_link(ws, child)
                if result == "child-exit":
                    break
                print("[muxrun] muxd owner link dropped; re-registering while child continues", file=sys.stderr)
                try:
                    await ws.close()
                except Exception:
                    pass
                try:
                    ws = await connect_owner(
                        args,
                        command,
                        cwd,
                        child_started=True,
                        owner_key=owner_key,
                        child_pid=child.pid,
                        child=child,
                    )
                except OwnerSupersededError as e:
                    print("[muxrun] mirror superseded; exiting sidecar: " + str(e), file=sys.stderr)
                    return 0
            except Exception as e:
                if child.poll() is not None:
                    break
                print("[muxrun] muxd owner link failed; re-registering while child continues: " + str(e), file=sys.stderr)
                try:
                    ws = await connect_owner(
                        args,
                        command,
                        cwd,
                        child_started=True,
                        owner_key=owner_key,
                        child_pid=child.pid,
                        child=child,
                    )
                except OwnerSupersededError as superseded:
                    print("[muxrun] mirror superseded; exiting sidecar: " + str(superseded), file=sys.stderr)
                    return 0
        return int(child.returncode or 0)
    finally:
        # Closing the kill-on-close Job Object is the point at which every descendant is forced down.
        # Only after that may muxd truthfully acknowledge the visible-owner session as dead.
        if job is not None:
            job.close()
        restore_console()
        atexit.unregister(restore_console)
        if ctrl_ignored:
            try:
                set_console_ctrl_ignored(False)
            except Exception:
                pass
        child_exit_confirmed = False
        if child is not None:
            try:
                child_exit_confirmed = (
                    child.tree_exit_confirmed()
                    if isinstance(child, AttachedChild)
                    else child.poll() is not None
                )
            except Exception:
                child_exit_confirmed = False
        if attached and child is not None:
            child.close()
        if ws is not None:
            if child_exit_confirmed or job is not None:
                try:
                    await ws.send(json.dumps({"t": "dead"}))
                except Exception:
                    pass
            try:
                await ws.close()
            except Exception:
                pass


def main():
    p = argparse.ArgumentParser(description="Mirror a visible local terminal into multiplex.")
    p.add_argument("--attach-pid", type=int, default=0,
                   help="adopt and mirror an already-running process console instead of launching --cmd")
    p.add_argument("name")
    p.add_argument("--cwd", default="")
    p.add_argument("--cmd", default="")
    p.add_argument("--cmd-b64", default="")
    p.add_argument("--session-id", default="")
    p.add_argument("--alias", action="append", default=[])
    args = p.parse_args(_consume_profile_arg(sys.argv[1:]))
    try:
        raise SystemExit(asyncio.run(main_async(args)))
    except KeyboardInterrupt:
        raise SystemExit(130)


if __name__ == "__main__":
    main()
