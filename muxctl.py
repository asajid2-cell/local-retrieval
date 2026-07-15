# muxctl — local terminal attach for muxd sessions (tmux-attach parity from a Windows Terminal).
#   muxctl ls               list hosted sessions
#   muxctl create <name>    create/revive a local muxd session without attaching
#   muxctl attach <name>    raw interactive attach (Ctrl-] to detach)
#   muxctl open [name]      create/revive if needed, then attach (Ctrl-] to detach)
#   muxctl kill <name>      kill and remove a local muxd session
import asyncio, base64, json, sys, os, ctypes, threading, subprocess, time, shutil, contextlib, atexit, queue
try:
    import websockets
except ImportError:
    print("muxctl needs: pip install websockets"); sys.exit(1)
import msvcrt

URL = "ws://127.0.0.1:" + os.environ.get("MUXCTL_PORT", "7699")
SHORT_TIMEOUT = float(os.environ.get("MUXCTL_TIMEOUT", "5"))
TASK_NAME = os.environ.get("MUXD_TASK", "MuxdSessionHost")
AUTOSTART = os.environ.get("MUXCTL_AUTOSTART", "1").lower() not in ("0", "false", "no", "off")
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
LOCAL_SCROLLBACK = max(0, int(os.environ.get("MUXCTL_SCROLLBACK", "60000")))

def env_truthy(name):
    return os.environ.get(name, "").lower() in ("1", "true", "yes", "on")

# extended-key scan codes (after 0x00/0xe0 prefix) -> VT sequences
EXT = {b'H': b'\x1b[A', b'P': b'\x1b[B', b'M': b'\x1b[C', b'K': b'\x1b[D',
       b'G': b'\x1b[H', b'O': b'\x1b[F', b'I': b'\x1b[5~', b'Q': b'\x1b[6~',
       b'S': b'\x1b[3~', b'R': b'\x1b[2~', b'\x89': b'\x1b[Z'}

STD_INPUT_HANDLE = -10
STD_OUTPUT_HANDLE = -11
ENABLE_PROCESSED_INPUT = 0x0001
ENABLE_LINE_INPUT = 0x0002
ENABLE_ECHO_INPUT = 0x0004
ENABLE_WINDOW_INPUT = 0x0008
ENABLE_MOUSE_INPUT = 0x0010
ENABLE_QUICK_EDIT_MODE = 0x0040
ENABLE_EXTENDED_FLAGS = 0x0080
ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200
ENABLE_PROCESSED_OUTPUT = 0x0001
ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002
ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004
DISABLE_NEWLINE_AUTO_RETURN = 0x0008

def attach_input_mode(current, vt_input=False):
    next_mode = current | ENABLE_EXTENDED_FLAGS
    next_mode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_QUICK_EDIT_MODE |
                   ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_PROCESSED_INPUT)
    if vt_input:
        next_mode |= ENABLE_VIRTUAL_TERMINAL_INPUT
    else:
        next_mode &= ~ENABLE_VIRTUAL_TERMINAL_INPUT
    return next_mode

def flush_console_input():
    if os.name != "nt":
        return
    try:
        k = ctypes.windll.kernel32
        k.FlushConsoleInputBuffer(k.GetStdHandle(STD_INPUT_HANDLE))
    except Exception:
        pass

class COORD(ctypes.Structure):
    _fields_ = [("X", ctypes.c_short), ("Y", ctypes.c_short)]

class SMALL_RECT(ctypes.Structure):
    _fields_ = [("Left", ctypes.c_short), ("Top", ctypes.c_short),
                ("Right", ctypes.c_short), ("Bottom", ctypes.c_short)]

class CONSOLE_SCREEN_BUFFER_INFO(ctypes.Structure):
    _fields_ = [("dwSize", COORD), ("dwCursorPosition", COORD),
                ("wAttributes", ctypes.c_ushort), ("srWindow", SMALL_RECT),
                ("dwMaximumWindowSize", COORD)]

def enable_vt_out():
    k = ctypes.windll.kernel32
    h = k.GetStdHandle(STD_OUTPUT_HANDLE)
    m = ctypes.c_uint()
    if k.GetConsoleMode(h, ctypes.byref(m)):
        k.SetConsoleMode(h, m.value | ENABLE_PROCESSED_OUTPUT | ENABLE_WRAP_AT_EOL_OUTPUT |
                         ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN)

@contextlib.contextmanager
def terminal_attach_mode():
    """Put the local console in pass-through VT mode while muxctl owns it."""
    k = ctypes.windll.kernel32
    hin = k.GetStdHandle(STD_INPUT_HANDLE)
    hout = k.GetStdHandle(STD_OUTPUT_HANDLE)
    in_mode = ctypes.c_uint()
    out_mode = ctypes.c_uint()
    have_in = bool(k.GetConsoleMode(hin, ctypes.byref(in_mode)))
    have_out = bool(k.GetConsoleMode(hout, ctypes.byref(out_mode)))
    if have_out:
        k.SetConsoleMode(hout, out_mode.value | ENABLE_PROCESSED_OUTPUT | ENABLE_WRAP_AT_EOL_OUTPUT |
                         ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN)
    if have_in:
        # Stay in classic key-input mode by default. VT/mouse/window input can make
        # terminal-generated reports and mouse coordinates look like typed bytes to
        # msvcrt.getch(), which then injects them into the hosted shell.
        k.SetConsoleMode(hin, attach_input_mode(in_mode.value, env_truthy("MUXCTL_VT_INPUT")))
        flush_console_input()
    try:
        yield
    finally:
        if have_in:
            k.SetConsoleMode(hin, in_mode.value)
        if have_out:
            k.SetConsoleMode(hout, out_mode.value)

def term_size():
    # On Windows, os.get_terminal_size() can report the scrollback buffer width,
    # not the visible viewport. Resizing the hosted PTY to that value corrupts
    # full-screen terminal rendering for the actual local window.
    if os.name == "nt":
        k = ctypes.windll.kernel32
        info = CONSOLE_SCREEN_BUFFER_INFO()
        for handle_id in (STD_OUTPUT_HANDLE, STD_INPUT_HANDLE):
            h = k.GetStdHandle(handle_id)
            if k.GetConsoleScreenBufferInfo(h, ctypes.byref(info)):
                cols = int(info.srWindow.Right - info.srWindow.Left + 1)
                rows = int(info.srWindow.Bottom - info.srWindow.Top + 1)
                if cols > 0 and rows > 0:
                    return cols, rows
    try:
        sz = os.get_terminal_size()
        return sz.columns, sz.lines
    except OSError:
        return 140, 40

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
    if os.name != "nt" or not AUTOSTART:
        return False
    try:
        q = _run_quiet(["schtasks", "/Query", "/TN", TASK_NAME, "/FO", "CSV", "/NH"])
    except Exception as e:
        sys.stderr.write("[muxctl] could not query scheduled task %s: %s\n" % (TASK_NAME, e))
        return False
    if q.returncode != 0:
        detail = (q.stderr or q.stdout or "").strip()
        sys.stderr.write("[muxctl] scheduled task %s is not available: %s\n" % (TASK_NAME, detail))
        return False
    if "Running" in q.stdout:
        return False
    try:
        r = _run_quiet(["schtasks", "/Run", "/TN", TASK_NAME])
    except Exception as e:
        sys.stderr.write("[muxctl] could not start scheduled task %s: %s\n" % (TASK_NAME, e))
        return False
    if r.returncode != 0:
        detail = (r.stderr or r.stdout or "").strip()
        sys.stderr.write("[muxctl] scheduled task %s failed to start: %s\n" % (TASK_NAME, detail))
        return False
    sys.stderr.write("[muxctl] started scheduled task %s; waiting for muxd...\n" % TASK_NAME)
    time.sleep(1.2)
    return True

async def request_json_once(payload):
    async with websockets.connect(URL, open_timeout=SHORT_TIMEOUT, close_timeout=1, ping_interval=None) as ws:
        await asyncio.wait_for(ws.send(json.dumps(payload)), SHORT_TIMEOUT)
        raw = await asyncio.wait_for(ws.recv(), SHORT_TIMEOUT)
        try:
            return json.loads(raw)
        except Exception as e:
            cmd = payload.get("t", "request")
            raise RuntimeError("muxd returned a non-JSON response to %s; the running muxd may not have that local handler loaded yet" % cmd) from e

async def request_json(payload):
    try:
        return await request_json_once(payload)
    except (OSError, asyncio.TimeoutError):
        if ensure_muxd_started():
            return await request_json_once(payload)
        raise

async def fetch_list():
    return (await request_json({"t": "ls"})).get("list", [])

async def fetch_info():
    m = await request_json({"t": "info"})
    if m.get("t") != "info":
        raise RuntimeError("running muxd does not advertise protocol info; restart MuxdSessionHost to load the current muxd")
    return m

async def require_cap(cap):
    info = await fetch_info()
    caps = set(info.get("caps") or [])
    if cap not in caps:
        raise RuntimeError("running muxd protocol %s lacks '%s'; restart MuxdSessionHost to load the current muxd" % (info.get("protocol", "?"), cap))
    return info

async def do_ls():
    sess = await fetch_list()
    if not sess:
        print("(no sessions hosted on muxd)"); return
    print("  %-38s %-6s %-5s %s" % ("SESSION", "STATE", "HEAL", "SIZE"))
    for s in sess:
        print("  %-38s %-6s %-5s %sx%s" % (s["name"], "alive" if s.get("alive") else "DEAD", "on" if s.get("heal") else "off", s.get("cols"), s.get("rows")))
    print("\nattach with:  muxctl attach <session>   (Ctrl-] to detach)")

def next_name(sess):
    existing = {str(s.get("name", "")).lower() for s in sess}
    name, i = "ai", 1
    while name.lower() in existing:
        i += 1; name = "ai%d" % i
    return name

async def do_create(name):
    await require_cap("create")
    cols, rows = term_size()
    m = await request_json({"t": "create", "s": name, "cols": cols, "rows": rows})
    if m.get("t") == "err":
        sys.stderr.write("[muxctl] " + m.get("m", "error") + "\n"); return 2
    print("%s %s" % ("created" if m.get("created") else "ready", m.get("s", name)))
    return 0

async def do_kill(name):
    await require_cap("kill")
    m = await request_json({"t": "kill", "s": name})
    if m.get("t") == "err":
        sys.stderr.write("[muxctl] " + m.get("m", "error") + "\n"); return 2
    print("killed %s" % m.get("s", name))
    return 0

async def do_status():
    info = await fetch_info()
    print("muxd protocol %s" % info.get("protocol", "?"))
    print("host: %s" % info.get("host", "pc"))
    print("caps: %s" % ", ".join(info.get("caps") or []))
    print("sessions: %s" % info.get("sessions", "?"))
    if "pid" in info:
        print("pid: %s uptime: %ss" % (info.get("pid"), info.get("uptimeSec", "?")))
    if "loopLagMs" in info:
        print("loop lag: %sms current, %sms max" % (info.get("loopLagMs"), info.get("maxLoopLagMs")))
    if "localTotal" in info:
        print("local control: active=%s total=%s errors=%s last=%sms %s" % (
            info.get("localActive", "?"), info.get("localTotal", "?"), info.get("localErrors", "?"),
            info.get("lastLocalMs", "?"), info.get("lastLocalT", "")))
    return 0

def check_shell_entrypoints():
    mux = shutil.which("mux") or shutil.which("mux.cmd") or ""
    multiplex = shutil.which("multiplex") or shutil.which("multiplex.cmd") or ""
    if mux:
        print("mux shim: %s" % mux)
    if multiplex:
        print("multiplex shim: %s" % multiplex)
    ps = shutil.which("powershell") or shutil.which("powershell.exe")
    if not ps:
        return 0
    script = (
        "$c=Get-Command multiplex -ErrorAction SilentlyContinue; "
        "if($c){"
        "$d=($c.Definition -replace \"`r?`n\", \" \"); "
        "[Console]::Out.WriteLine($c.CommandType.ToString() + '|' + [string]$c.Source + '|' + $d)"
        "}"
    )
    try:
        p = subprocess.run([ps, "-NoLogo", "-Command", script], stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, text=True, timeout=8, creationflags=CREATE_NO_WINDOW)
    except Exception as e:
        sys.stderr.write("[muxctl] could not inspect PowerShell multiplex command: %s\n" % e)
        return 1
    line = (p.stdout or "").strip().splitlines()[-1:] or [""]
    parts = line[0].split("|", 2)
    if len(parts) != 3:
        return 0
    kind, source, definition = parts
    if kind.lower() == "function":
        if "muxctl.py" not in definition:
            sys.stderr.write("[muxctl] notice: PowerShell 'multiplex' is a deprecated profile Function and is ignored.\n")
            sys.stderr.write("[muxctl]         Canonical local command: mux. The stale VPS helper fails closed, so this is not a muxd health failure.\n")
            return 0
    elif source and ".local\\bin\\multiplex.cmd" not in source.lower():
        sys.stderr.write("[muxctl] warning: PowerShell 'multiplex' resolves to %s\n" % source)
        return 1
    return 0

async def do_doctor():
    rc = 0
    try:
        await do_status()
    except Exception as e:
        sys.stderr.write("[muxctl] muxd status failed: %s\n" % e)
        rc = 2
    rc = max(rc, check_shell_entrypoints())
    return rc

async def do_next_name():
    print(next_name(await fetch_list()))
    return 0

async def do_attach(name, create=False):
    mode = terminal_attach_mode()
    mode.__enter__()
    restored_console = False
    def restore_console():
        nonlocal restored_console
        if not restored_console:
            restored_console = True
            mode.__exit__(None, None, None)
    atexit.register(restore_console)
    cols, rows = term_size()
    if create:
        sess = await fetch_list()
        exists = any(str(s.get("name", "")).lower() == str(name).lower() for s in sess)
        if exists:
            create = False
            try:
                await require_cap("attach")
            except Exception as e:
                sys.stderr.write("[muxctl] warning: %s; attaching to existing session only\n" % e)
        else:
            await require_cap("create")
    else:
        # Preflight also gives raw attach the same scheduled-task autostart path as open/create.
        await fetch_list()
    async with websockets.connect(URL, max_size=8_000_000, open_timeout=8) as ws:
        await ws.send(json.dumps({"t": "open" if create else "attach", "s": name, "cols": cols, "rows": rows,
                                  "sb": LOCAL_SCROLLBACK}))
        loop = asyncio.get_event_loop()
        stop = asyncio.Event()
        sendq = asyncio.Queue()
        send_lock = asyncio.Lock()
        outq = queue.Queue()

        def output_thread():
            while True:
                data = outq.get()
                if data is None:
                    return
                try:
                    sys.stdout.buffer.write(data)
                    sys.stdout.buffer.flush()
                except Exception:
                    return
        threading.Thread(target=output_thread, daemon=True).start()

        async def safe_send(payload):
            async with send_lock:
                await ws.send(payload)

        def input_thread():
            while True:
                try: ch = msvcrt.getch()
                except Exception: return
                if ch in (b'\x00', b'\xe0'):
                    seq = EXT.get(msvcrt.getch())
                    if not seq: continue
                    data = seq
                elif ch == b'\x1d':                    # Ctrl-] = detach
                    loop.call_soon_threadsafe(stop.set)
                    loop.call_soon_threadsafe(sendq.put_nowait, None)
                    return
                else:
                    data = ch
                loop.call_soon_threadsafe(sendq.put_nowait, data)
        threading.Thread(target=input_thread, daemon=True).start()

        async def send_input():
            while True:
                data = await sendq.get()
                if data is None: return
                buf = bytearray(data)
                deadline = loop.time() + 0.004
                while len(buf) < 4096:
                    timeout = max(0, deadline - loop.time())
                    if timeout <= 0: break
                    try: more = await asyncio.wait_for(sendq.get(), timeout)
                    except asyncio.TimeoutError: break
                    if more is None: return
                    buf += more
                await safe_send(bytes(buf))

        async def resize_watch():
            last = (cols, rows)
            while True:
                await asyncio.sleep(0.25)
                cur = term_size()
                if cur == last: continue
                last = cur
                await safe_send(json.dumps({"t": "resize", "cols": cur[0], "rows": cur[1]}))

        async def recv():
            first_binary = True
            try:
                async for raw in ws:
                    if isinstance(raw, (bytes, bytearray)):
                        data = bytes(raw)
                        if first_binary:
                            first_binary = False
                            # Do NOT tail-truncate: muxd already bounds the replay server-side, and its FRONT
                            # carries the mode prefix (alt-screen enter etc.). Cutting the front stripped the
                            # prefix and started the screen mid-escape -> corrupted/black local attach.
                            if LOCAL_SCROLLBACK == 0:
                                data = b""
                        if data:
                            outq.put(data)
                        continue
                    try: m = json.loads(raw)
                    except Exception: continue
                    if m.get("t") == "o":
                        outq.put(base64.b64decode(m.get("d", "")))
                    elif m.get("t") == "err":
                        sys.stderr.write("\r\n[muxctl] " + m.get("m", "error") + "\r\n"); return
            finally:
                stop.set()
                try: sendq.put_nowait(None)
                except Exception: pass
                try: outq.put_nowait(None)
                except Exception: pass
        tasks = [asyncio.create_task(recv()), asyncio.create_task(send_input()), asyncio.create_task(resize_watch())]
        await stop.wait()
        for task in tasks: task.cancel()
    restore_console()
    code = getattr(ws, "close_code", None)
    reason = (getattr(ws, "close_reason", "") or "").strip()
    if code and code != 1000:
        sys.stdout.write("\r\n[muxctl] detached (code %s%s)\r\n" % (code, ": " + reason if reason else ""))
    else:
        sys.stdout.write("\r\n[muxctl] detached\r\n")

def main():
    a = sys.argv[1:]
    try:
        if not a or a[0] in ("ls", "list"):
            asyncio.run(do_ls())
        elif a[0] in ("create", "new") and len(a) >= 2:
            raise SystemExit(asyncio.run(do_create(a[1])))
        elif a[0] in ("kill", "rm", "delete") and len(a) >= 2:
            raise SystemExit(asyncio.run(do_kill(a[1])))
        elif a[0] in ("status", "info"):
            raise SystemExit(asyncio.run(do_status()))
        elif a[0] in ("doctor", "diag"):
            raise SystemExit(asyncio.run(do_doctor()))
        elif a[0] in ("next-name", "next"):
            raise SystemExit(asyncio.run(do_next_name()))
        elif a[0] in ("attach", "a") and len(a) >= 2:
            try: asyncio.run(do_attach(a[1], create=False))
            except KeyboardInterrupt: pass
        elif a[0] in ("open", "o"):
            name = a[1] if len(a) >= 2 else asyncio.run(fetch_list())
            if isinstance(name, list): name = next_name(name)
            print("muxctl -> opening local muxd session '%s' (Ctrl-] to detach)" % name)
            try: asyncio.run(do_attach(name, create=True))
            except KeyboardInterrupt: pass
        else:
            print("usage: muxctl ls | muxctl status | muxctl doctor | muxctl next-name | muxctl create <session> | muxctl kill <session> | muxctl attach <session> | muxctl open [session]")
    except OSError as e:
        sys.stderr.write("[muxctl] cannot reach muxd at %s: %s\n" % (URL, e))
        raise SystemExit(2)
    except Exception as e:
        # websockets changed exception classes across releases; keep the CLI failure readable.
        sys.stderr.write("[muxctl] failed: %s\n" % e)
        raise SystemExit(2)

if __name__ == "__main__":
    main()
