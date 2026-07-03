# muxctl — local terminal attach for muxd sessions (tmux-attach parity from a Windows Terminal).
#   muxctl ls               list hosted sessions
#   muxctl create <name>    create/revive a local muxd session without attaching
#   muxctl attach <name>    raw interactive attach (Ctrl-] to detach)
#   muxctl open [name]      create/revive if needed, then attach (Ctrl-] to detach)
import asyncio, base64, json, sys, os, ctypes, threading
try:
    import websockets
except ImportError:
    print("muxctl needs: pip install websockets"); sys.exit(1)
import msvcrt

URL = "ws://127.0.0.1:" + os.environ.get("MUXCTL_PORT", "7699")
# extended-key scan codes (after 0x00/0xe0 prefix) -> VT sequences
EXT = {b'H': b'\x1b[A', b'P': b'\x1b[B', b'M': b'\x1b[C', b'K': b'\x1b[D',
       b'G': b'\x1b[H', b'O': b'\x1b[F', b'I': b'\x1b[5~', b'Q': b'\x1b[6~',
       b'S': b'\x1b[3~', b'R': b'\x1b[2~', b'\x89': b'\x1b[Z'}

def enable_vt_out():
    k = ctypes.windll.kernel32
    h = k.GetStdHandle(-11)
    m = ctypes.c_uint(); k.GetConsoleMode(h, ctypes.byref(m))
    k.SetConsoleMode(h, m.value | 0x0004)   # ENABLE_VIRTUAL_TERMINAL_PROCESSING

def term_size():
    try:
        sz = os.get_terminal_size()
        return sz.columns, sz.lines
    except OSError:
        return 140, 40

async def fetch_list():
    async with websockets.connect(URL) as ws:
        await ws.send(json.dumps({"t": "ls"}))
        return json.loads(await ws.recv()).get("list", [])

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
    cols, rows = term_size()
    async with websockets.connect(URL) as ws:
        await ws.send(json.dumps({"t": "create", "s": name, "cols": cols, "rows": rows}))
        m = json.loads(await ws.recv())
        if m.get("t") == "err":
            sys.stderr.write("[muxctl] " + m.get("m", "error") + "\n"); return 2
        print("%s %s" % ("created" if m.get("created") else "ready", m.get("s", name)))
        return 0

async def do_attach(name, create=False):
    enable_vt_out()
    cols, rows = term_size()
    async with websockets.connect(URL, max_size=8_000_000) as ws:
        await ws.send(json.dumps({"t": "open" if create else "attach", "s": name, "cols": cols, "rows": rows}))
        loop = asyncio.get_event_loop()
        stop = asyncio.Event()

        def input_thread():
            while True:
                try: ch = msvcrt.getch()
                except Exception: return
                if ch in (b'\x00', b'\xe0'):
                    seq = EXT.get(msvcrt.getch())
                    if not seq: continue
                    data = seq
                elif ch == b'\x1d':                    # Ctrl-] = detach
                    loop.call_soon_threadsafe(stop.set); return
                else:
                    data = ch
                asyncio.run_coroutine_threadsafe(ws.send(json.dumps({"t": "i", "d": base64.b64encode(data).decode()})), loop)
        threading.Thread(target=input_thread, daemon=True).start()

        async def recv():
            try:
                async for raw in ws:
                    try: m = json.loads(raw)
                    except Exception: continue
                    if m.get("t") == "o":
                        sys.stdout.buffer.write(base64.b64decode(m.get("d", ""))); sys.stdout.buffer.flush()
                    elif m.get("t") == "err":
                        sys.stderr.write("\r\n[muxctl] " + m.get("m", "error") + "\r\n"); return
            finally:
                stop.set()
        rt = asyncio.create_task(recv())
        await stop.wait()
        rt.cancel()
    sys.stdout.write("\r\n[muxctl] detached\r\n")

def main():
    a = sys.argv[1:]
    try:
        if not a or a[0] in ("ls", "list"):
            asyncio.run(do_ls())
        elif a[0] in ("create", "new") and len(a) >= 2:
            raise SystemExit(asyncio.run(do_create(a[1])))
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
            print("usage: muxctl ls | muxctl create <session> | muxctl attach <session> | muxctl open [session]")
    except OSError as e:
        sys.stderr.write("[muxctl] cannot reach muxd at %s: %s\n" % (URL, e))
        raise SystemExit(2)
    except Exception as e:
        # websockets changed exception classes across releases; keep the CLI failure readable.
        sys.stderr.write("[muxctl] failed: %s\n" % e)
        raise SystemExit(2)

if __name__ == "__main__":
    main()
