# muxd — PC-local terminal session host for the multiplex.
#
# THE POINT (from full-review.md): the agent's console must be owned by THIS machine, not by an
# ssh pipe from the VPS. muxd owns a ConPTY per session; claude/codex live in it. Wi-Fi drops,
# VPS reboots, relay deploys — the agent never notices; viewers just reattach. muxd dials OUT
# to the VPS relay (no inbound port on the PC) and multiplexes all sessions over one WebSocket.
#
# Protocol (JSON text frames over ws):
#   muxd -> relay:  hello{host,sessions} · sessions{list} · o{s,d:b64} · sb{s,d:b64} · killed{s} · pong
#   relay -> muxd:  create{s,cmd,cwd,cols,rows} · i{s,d:b64} · resize{s,cols,rows} · kill{s} · sb{s} · ping
#
# State: sessions.json manifest (resume commands) -> muxd restart / PC reboot auto-recreates and
# re-runs each session's resume, so even the "PC reboot" failure self-heals.
import asyncio, base64, collections, json, os, queue, re, sys, threading, time, traceback
import faulthandler
try: faulthandler.enable(open(os.path.join(os.path.expanduser("~"), "muxd", "muxd.crash"), "a"))
except Exception: pass
from winpty import PtyProcess

HOME = os.path.expanduser("~")
DIR = os.path.join(HOME, "muxd")
MANIFEST = os.path.join(DIR, "sessions.json")
LOG = os.path.join(DIR, "muxd.log")
ENVF = os.path.join(DIR, "muxd.env")

def log(msg):
    line = time.strftime("%m-%d %H:%M:%S") + " " + msg
    try:
        if os.path.exists(LOG) and os.path.getsize(LOG) > 2_000_000:
            os.replace(LOG, LOG + ".1")
        with open(LOG, "a", encoding="utf-8") as f: f.write(line + "\n")
    except Exception: pass

def loadenv():
    env = {}
    try:
        for ln in open(ENVF, encoding="utf-8"):
            ln = ln.strip()
            if ln and not ln.startswith("#") and "=" in ln:
                k, v = ln.split("=", 1); env[k.strip()] = v.strip()
    except Exception: pass
    return env

ENV = loadenv()
TOKEN = ENV.get("MUX_HOST_TOKEN", "")
RELAYS = [u for u in [ENV.get("RELAY_LAN", ""), ENV.get("RELAY_PUBLIC", "")] if u]
DEFAULT_CWD = ENV.get("DEFAULT_CWD", r"Z:\328\CMPUT328-A2\codexworks\301")
LOCAL_PORT = int(ENV.get("LOCAL_PORT", "7699"))   # muxctl local-attach loopback port
RING_CAP = 800_000           # per-session scrollback bytes kept
SB_SEND = 260_000            # bytes replayed to a newly-attached viewer

class Session:
    def __init__(self, name, cmd, cwd, cols, rows, loop, outq, heal=False, spawn_now=True):
        self.name, self.cmd, self.cwd = name, cmd or "", cwd or DEFAULT_CWD
        self.heal = bool(heal)          # opt-in: ONLY healed (user-armed) sessions auto-start at boot / auto-respawn
        self.cols, self.rows = max(20, cols or 140), max(8, rows or 40)
        self.created = time.time(); self.last_out = time.time()
        self.ring = collections.deque(); self.ring_len = 0
        self.dead = False; self.user_killed = False
        self.deaths = []
        self.loop, self.outq = loop, outq
        self.pending = bytearray(); self.plock = threading.Lock()   # output coalescing (flushed by the pump)
        self.local = set()                                          # local (muxctl) viewer queues — fanned the same output
        self.wq = queue.Queue()                                     # input write queue → serialized, chunked writes
        self.pty = None
        threading.Thread(target=self._writer, daemon=True).start()
        if spawn_now: self.spawn()
        else: self.dead = True          # placeholder tab: NOTHING runs until the user attaches (revive) or arms it

    def spawn(self):
        self.pty = PtyProcess.spawn("powershell.exe -NoLogo", dimensions=(self.rows, self.cols), cwd=self.cwd)
        self.dead = False
        t = threading.Thread(target=self._reader, args=(self.pty,), daemon=True); t.start()
        if self.cmd:
            threading.Timer(2.5, self._type_cmd, args=(self.pty,)).start()
        log(f"[{self.name}] spawned pty ({self.cols}x{self.rows}) cmd={'yes' if self.cmd else 'no'}")

    def _type_cmd(self, pty):
        try:
            if pty is self.pty and not self.dead: pty.write(self.cmd + "\r")
        except Exception as e: log(f"[{self.name}] type_cmd failed: {e}")

    def _reader(self, pty):
        while True:
            try: data = pty.read(8192)
            except Exception:
                if pty is self.pty:
                    self.dead = True
                    self.loop.call_soon_threadsafe(self.outq.put_nowait, ("dead", self.name, ""))
                    log(f"[{self.name}] pty EOF (shell exited or killed)")
                return
            if not data: continue
            if pty is not self.pty: return          # superseded by a respawn
            b = data.encode("utf-8", "replace")
            self.ring.append(b); self.ring_len += len(b); self.last_out = time.time()
            while self.ring_len > RING_CAP:
                old = self.ring.popleft(); self.ring_len -= len(old)
            with self.plock:                           # coalesced; the pump flushes on a ~12ms timer
                self.pending += b                       # backpressure: if a flood outruns a slow link, keep the
                if len(self.pending) > 2_000_000:       # last ~2MB unsent (the ring still holds full history for reattach)
                    del self.pending[:len(self.pending) - 2_000_000]

    def drain(self):
        if not self.pending: return None
        with self.plock:
            chunk = bytes(self.pending); self.pending = bytearray()
        return chunk

    def _writer(self):
        # serialize input; slice large pastes into <=1KB writes so a big paste can't stall/garble ConPTY input.
        while True:
            s = self.wq.get()
            if s is None: return
            try:
                if self.pty is None or self.dead: continue
                if len(s) <= 1024:
                    self.pty.write(s)
                else:
                    for i in range(0, len(s), 1024):
                        if self.dead: break
                        self.pty.write(s[i:i+1024]); time.sleep(0.004)
            except Exception as e: log(f"[{self.name}] write failed: {e}")

    def write(self, data: bytes):
        try: self.wq.put(data.decode("utf-8", "replace"))
        except Exception as e: log(f"[{self.name}] enqueue failed: {e}")

    def resize(self, cols, rows):
        cols, rows = max(20, int(cols)), max(8, int(rows))
        if (cols, rows) == (self.cols, self.rows): return
        self.cols, self.rows = cols, rows
        try: self.pty.setwinsize(rows, cols)
        except Exception as e: log(f"[{self.name}] resize failed: {e}")

    def scrollback(self):
        out, n = [], 0
        for b in reversed(self.ring):
            out.append(b); n += len(b)
            if n >= SB_SEND: break
        return b"".join(reversed(out))

    def tail_text(self, nbytes=1600, lines=0):
        raw = bytearray()
        for b in reversed(self.ring):
            raw[:0] = b
            if len(raw) >= nbytes: break
        s = bytes(raw[-nbytes:]).decode("utf-8", "replace")
        s = re.sub(r"\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|[\x00-\x08\x0b\x0c\x0e-\x1f]", "", s)
        if lines: return "\n".join(s.split("\n")[-lines:]).rstrip()
        return s[-900:]

    def alive(self):
        try: return (not self.dead) and self.pty.isalive()
        except Exception: return False

    def kill(self, by_user=True):
        self.user_killed = by_user
        try: self.pty.terminate(force=True)
        except Exception: pass
        self.dead = True

sessions = {}    # name -> Session

def manifest_save():
    try:
        data = {n: {"cmd": s.cmd, "cwd": s.cwd, "cols": s.cols, "rows": s.rows, "heal": s.heal}
                for n, s in sessions.items() if not s.user_killed}
        tmp = MANIFEST + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f: json.dump(data, f)
        os.replace(tmp, MANIFEST)
    except Exception as e: log(f"manifest save failed: {e}")

def manifest_load():
    try: return json.load(open(MANIFEST, encoding="utf-8"))
    except Exception: return {}

SAFE = lambda s: re.sub(r"[^A-Za-z0-9_.-]", "", str(s or ""))[:48]

def sess_list():
    return [{"name": n, "alive": s.alive(), "created": int(s.created * 1000),
             "lastOut": int(s.last_out * 1000), "cols": s.cols, "rows": s.rows,
             "tail": s.tail_text()} for n, s in sessions.items()]

async def main():
    loop = asyncio.get_running_loop()
    outq = asyncio.Queue()

    # boot policy (user-specified): agents NEVER auto-start on a fresh boot unless the session was
    # ARMED (auto-resume on). Armed -> recreate + resume now. Unarmed -> a dead placeholder tab that
    # stays dormant until an explicit create/relaunch sends a non-empty resume command.
    for name, m in manifest_load().items():
        if SAFE(name) and name not in sessions:
            try:
                heal = bool(m.get("heal"))
                sessions[name] = Session(name, m.get("cmd", ""), m.get("cwd", ""), m.get("cols", 140), m.get("rows", 40), loop, outq, heal=heal, spawn_now=heal)
                log(f"[boot] {'recreated + resumed (armed)' if heal else 'listed as dormant (unarmed - explicit relaunch required)'}: {name}")
            except Exception as e: log(f"[boot] {name} failed: {e}")

    async def self_heal_tick():
        # a session whose SHELL died (pty EOF) is useless — recreate + re-run its resume (max 3/10min).
        while True:
            await asyncio.sleep(15)
            for s in list(sessions.values()):
                if s.dead and not s.user_killed and s.cmd and s.heal:   # opt-in only: unarmed sessions stay down
                    now = time.time()
                    s.deaths = [t for t in s.deaths if now - t < 600]
                    if len(s.deaths) >= 3: continue
                    s.deaths.append(now)
                    try: s.spawn(); log(f"[heal] {s.name} shell died -> respawned + resume queued")
                    except Exception as e: log(f"[heal] {s.name} respawn failed: {e}")
    asyncio.create_task(self_heal_tick())

    async def flush_out():
        # coalesce each session's output into ONE ws frame per ~12ms tick — far fewer frames/less b64+JSON
        # overhead, smoother phone rendering, and a natural place to add backpressure.
        while True:
            await asyncio.sleep(0.012)
            for s in list(sessions.values()):
                chunk = s.drain()
                if chunk:
                    outq.put_nowait(("o", s.name, chunk))
                    for lq in list(s.local):
                        try: lq.put_nowait(chunk)
                        except Exception: pass
    asyncio.create_task(flush_out())

    # ---- LOCAL attach server (muxctl): loopback-only, no token — a raw console client streams a session
    # exactly like a web viewer, so you get `tmux attach`-style parity from a Windows Terminal on the PC.
    async def local_serve():
        import websockets as _ws
        async def handler(ws):
            try:
                first = json.loads(await ws.recv())
            except Exception:
                return
            if first.get("t") == "ls":
                await ws.send(json.dumps({"t": "ls", "list": sess_list()})); return
            name = SAFE(first.get("s", ""))
            if name not in sessions:
                await ws.send(json.dumps({"t": "err", "m": "no such session: " + name})); return
            s = sessions[name]
            lq = asyncio.Queue(); s.local.add(lq)
            if first.get("cols"): s.resize(int(first.get("cols")), int(first.get("rows") or 40))
            try:
                await ws.send(json.dumps({"t": "o", "d": base64.b64encode(s.scrollback()).decode()}))
                async def pump():
                    while True:
                        data = await lq.get()
                        await ws.send(json.dumps({"t": "o", "d": base64.b64encode(data).decode()}))
                pt = asyncio.create_task(pump())
                try:
                    async for raw in ws:
                        try: m = json.loads(raw)
                        except Exception: continue
                        if m.get("t") == "i": s.write(base64.b64decode(m.get("d", "")))
                        elif m.get("t") == "resize": s.resize(m.get("cols", 140), m.get("rows", 40))
                finally: pt.cancel()
            finally:
                s.local.discard(lq)
        try:
            async with _ws.serve(handler, "127.0.0.1", LOCAL_PORT):
                await asyncio.Future()
        except Exception as e:
            log(f"local attach server failed on :{LOCAL_PORT}: {e}")
    asyncio.create_task(local_serve())

    import websockets
    backoff = 1
    while True:
        url = None
        for cand in RELAYS:
            url = cand + ("&" if "?" in cand else "?") + "token=" + TOKEN
            try:
                async with websockets.connect(url, max_size=8_000_000, ping_interval=20, ping_timeout=15, open_timeout=8) as ws:
                    backoff = 1
                    log(f"connected to relay {cand}")
                    await ws.send(json.dumps({"t": "hello", "host": os.environ.get("COMPUTERNAME", "pc"), "sessions": sess_list()}))

                    async def pump_out():
                        while True:
                            kind, name, data = await outq.get()
                            if kind == "o":
                                await ws.send(json.dumps({"t": "o", "s": name, "d": base64.b64encode(data).decode()}))
                            elif kind == "dead":
                                await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))

                    async def pump_status():
                        while True:
                            await asyncio.sleep(5)
                            await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))

                    async def lan_return():
                        # A2 #5: we're on a FALLBACK link (e.g. public wss) — periodically check if the
                        # preferred (LAN) relay is reachable again; if so, drop this link so the outer loop
                        # reconnects starting at RELAYS[0] and we stop paying the fallback latency tax.
                        while True:
                            await asyncio.sleep(180)
                            try:
                                lan = RELAYS[0] + ("&" if "?" in RELAYS[0] else "?") + "token=" + TOKEN
                                async with websockets.connect(lan, open_timeout=6) as p:
                                    await p.close()
                                log("preferred (LAN) relay reachable again → switching back")
                                await ws.close(); return
                            except Exception:
                                pass

                    tasks = [asyncio.create_task(pump_out()), asyncio.create_task(pump_status())]
                    if cand != RELAYS[0]:
                        tasks.append(asyncio.create_task(lan_return()))
                    try:
                        async for raw in ws:
                            try: m = json.loads(raw)
                            except Exception: continue
                            t, name = m.get("t"), SAFE(m.get("s", ""))
                            if t == "create" and name:
                                if name in sessions and sessions[name].alive():
                                    pass                                    # already hosted + alive
                                else:
                                    prev = sessions.get(name)               # reviving a DEAD session → keep its cmd/cwd/size (don't wipe the resume)
                                    requested_cmd = (m.get("cmd", "") or "").strip()
                                    requested_heal = bool(m.get("heal")) if ("heal" in m) else bool(prev.heal if prev else False)
                                    # Plain web attach to a dormant, unarmed placeholder sends create{cmd:""}.
                                    # Do not translate that into "resume the saved command"; only an explicit
                                    # Relaunch/Create request (non-empty cmd) or an armed watcher may start work.
                                    if prev and prev.dead and not prev.heal and not requested_heal and not requested_cmd:
                                        if m.get("cols"): prev.cols = int(m.get("cols") or prev.cols)
                                        if m.get("rows"): prev.rows = int(m.get("rows") or prev.rows)
                                        log(f"[{name}] attach to dormant unarmed session - left stopped")
                                        await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))
                                        continue
                                    cmd = requested_cmd or (prev.cmd if prev else "")
                                    cwd = m.get("cwd", "") or (prev.cwd if prev else "")
                                    cols = int(m.get("cols") or (prev.cols if prev else 140))
                                    rows = int(m.get("rows") or (prev.rows if prev else 40))
                                    heal = requested_heal
                                    if prev: prev.kill(by_user=False)
                                    sessions[name] = Session(name, cmd, cwd, cols, rows, loop, outq, heal=heal)
                                    manifest_save()
                                await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))
                            elif t == "heal" and name in sessions:
                                sessions[name].heal = bool(m.get("on")); manifest_save()
                            elif t == "i" and name in sessions:
                                sessions[name].write(base64.b64decode(m.get("d", "")))
                            elif t == "resize" and name in sessions:
                                sessions[name].resize(m.get("cols", 140), m.get("rows", 40))
                            elif t == "sb" and name in sessions:
                                await ws.send(json.dumps({"t": "sb", "s": name,
                                                          "d": base64.b64encode(sessions[name].scrollback()).decode()}))
                            elif t == "rename" and name in sessions:
                                to = SAFE(m.get("to", ""))
                                if to and to not in sessions:
                                    s = sessions.pop(name); s.name = to; sessions[to] = s; manifest_save()
                                    await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))
                            elif t == "tail" and name in sessions:
                                await ws.send(json.dumps({"t": "tailr", "rid": m.get("rid", ""),
                                                          "text": sessions[name].tail_text(nbytes=200000, lines=int(m.get("lines") or 40))}))
                            elif t == "kill" and name in sessions:
                                sessions[name].kill(by_user=True)
                                del sessions[name]; manifest_save()
                                await ws.send(json.dumps({"t": "killed", "s": name}))
                                await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))
                    finally:
                        for tk in tasks: tk.cancel()
            except Exception as e:
                log(f"relay link ({cand}) dropped/failed: {type(e).__name__}: {e}")
            # try next candidate immediately; back off only after all fail
        await asyncio.sleep(backoff)
        backoff = min(15, backoff * 2)

if __name__ == "__main__":
    os.makedirs(DIR, exist_ok=True)
    log("=== muxd starting ===")
    if not TOKEN or not RELAYS:
        log("FATAL: muxd.env needs MUX_HOST_TOKEN and RELAY_LAN/RELAY_PUBLIC"); sys.exit(1)
    while True:                      # top-level crash guard: muxd must never die quietly
        try: asyncio.run(main())
        except KeyboardInterrupt: sys.exit(0)
        except Exception:
            log("CRASH:\n" + traceback.format_exc()); time.sleep(3)
