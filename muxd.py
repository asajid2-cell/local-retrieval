# muxd — PC-local terminal session host for the multiplex.
#
# THE POINT (from full-review.md): the agent's console must be owned by THIS machine, not by an
# ssh pipe from the VPS. muxd owns a ConPTY per session; claude/codex live in it. Wi-Fi drops,
# VPS reboots, relay deploys — the agent never notices; viewers just reattach. muxd dials OUT
# to the VPS relay (no inbound port on the PC) and multiplexes all sessions over one WebSocket.
#
# Protocol (JSON text frames over ws):
#   muxd -> relay:  hello{host,sessions} . sessions{list} . o{s,d:b64} . sb{s,d:b64} . killed{s} . pong
#   relay -> muxd:  create{s,cmd,cwd,cols,rows} . i{s,d:b64} . resize{s,cols,rows} . kill{s} . sb{s} . ping
#
# State: sessions.json manifest (resume commands) -> muxd restart / PC reboot lists unarmed sessions
# as dormant placeholders. Only sessions explicitly armed with heal auto-start.
import asyncio, base64, collections, hashlib, json, os, queue, re, subprocess, sys, threading, time, traceback
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
LAN_RETURN_INTERVAL = max(10, int(ENV.get("LAN_RETURN_INTERVAL", "30")))
RING_CAP = 800_000           # per-session scrollback bytes kept
SB_SEND = 260_000            # bytes replayed to a newly-attached viewer
LOCAL_SB_SEND = int(ENV.get("LOCAL_SB_SEND", "60000"))  # local muxctl attach should become live fast
LOCAL_FIRST_TIMEOUT = float(ENV.get("LOCAL_FIRST_TIMEOUT", "3"))
LOOP_WATCHDOG_WARN = float(ENV.get("LOOP_WATCHDOG_WARN", "30"))
LOOP_WATCHDOG_EXIT = float(ENV.get("LOOP_WATCHDOG_EXIT", "12"))
PROTOCOL = 2
CAPS = ["ls", "info", "create", "open", "attach", "kill", "rename", "heal", "tail", "scrollback", "resize", "owner"]
STARTED = time.time()
AGENT_WORKING_FRESH = float(ENV.get("AGENT_WORKING_FRESH", "25"))
AGENT_STARTING_GRACE = float(ENV.get("AGENT_STARTING_GRACE", "45"))
ANSI_RE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]")
STALE_CSI_RE = re.compile(r"\[[0-?]*[ -/]*[@-~]")
CTRL_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")

WATCH = {
    "last_tick": time.monotonic(),
    "last_lag": 0.0,
    "max_lag": 0.0,
    "local_active": 0,
    "local_total": 0,
    "local_errors": 0,
    "last_local_ms": 0.0,
    "last_local_t": "",
    "last_local_peer": "",
}
WATCH_LOCK = threading.Lock()
WATCHDOG_STARTED = False

def watch_snapshot():
    with WATCH_LOCK:
        return dict(WATCH)

def clean_terminal_text(s):
    # xterm/codex emits CSI sequences with intermediate bytes, e.g. ESC[0 q.
    # Older sanitizing stripped ESC but left "[0 q", so remove those leftovers too.
    s = ANSI_RE.sub("", str(s or ""))
    s = STALE_CSI_RE.sub("", s)
    return CTRL_RE.sub("", s)

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
        cmdline = "powershell.exe -NoLogo"
        direct_cmd = False
        if self.cmd:
            encoded = base64.b64encode(self.cmd.encode("utf-16le")).decode("ascii")
            cmdline = subprocess.list2cmdline(["powershell.exe", "-NoLogo", "-NoExit", "-EncodedCommand", encoded])
            direct_cmd = True
        try:
            self.pty = PtyProcess.spawn(cmdline, dimensions=(self.rows, self.cols), cwd=self.cwd)
        except Exception:
            if not self.cmd:
                raise
            # If a saved command hits a Windows command-line edge case, keep the session usable and
            # fall back to typing the command into an already-started shell.
            self.pty = PtyProcess.spawn("powershell.exe -NoLogo", dimensions=(self.rows, self.cols), cwd=self.cwd)
            direct_cmd = False
        self.dead = False
        t = threading.Thread(target=self._reader, args=(self.pty,), daemon=True); t.start()
        if self.cmd and not direct_cmd:
            threading.Timer(0.8, self._type_cmd, args=(self.pty,)).start()
        log(f"[{self.name}] spawned pty ({self.cols}x{self.rows}) cmd={'direct' if direct_cmd else ('typed' if self.cmd else 'no')}")

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
        pty = self.pty
        if pty is None or self.dead:
            return
        def do_resize():
            try:
                if pty is self.pty and not self.dead:
                    pty.setwinsize(rows, cols)
            except Exception as e:
                log(f"[{self.name}] resize failed: {e}")
        threading.Thread(target=do_resize, name=f"{self.name}-resize", daemon=True).start()

    def scrollback(self, limit=SB_SEND):
        try: limit = max(0, min(RING_CAP, int(limit)))
        except Exception: limit = SB_SEND
        out, n = [], 0
        for b in reversed(self.ring):
            out.append(b); n += len(b)
            if n >= limit: break
        return b"".join(reversed(out))

    def tail_text(self, nbytes=1600, lines=0):
        raw = bytearray()
        for b in reversed(self.ring):
            raw[:0] = b
            if len(raw) >= nbytes: break
        s = clean_terminal_text(bytes(raw[-nbytes:]).decode("utf-8", "replace"))
        if lines: return "\n".join(s.split("\n")[-lines:]).rstrip()
        return s[-900:]

    def alive(self):
        # Keep status/list paths off winpty. The reader thread owns EOF detection and flips
        # self.dead; probing ConPTY here can block the shared asyncio loop for every session.
        return (not self.dead) and self.pty is not None

    def kill(self, by_user=True):
        self.user_killed = by_user
        pty = self.pty
        self.pty = None
        self.dead = True
        if pty is not None:
            def terminate():
                try: pty.terminate(force=True)
                except Exception: pass
            threading.Thread(target=terminate, name=f"{self.name}-terminate", daemon=True).start()

class OwnerSession:
    # A visible local terminal owns the agent. muxd only relays that terminal's screen
    # snapshots to the VPS and forwards remote keystrokes back into the owner sidecar.
    def __init__(self, name, cmd, cwd, cols, rows, loop, outq, owner_ws, heal=False):
        self.name, self.cmd, self.cwd = name, cmd or "", cwd or DEFAULT_CWD
        self.heal = bool(heal)
        self.cols, self.rows = max(20, int(cols or 140)), max(8, int(rows or 40))
        self.created = time.time(); self.last_out = time.time()
        self.ring = collections.deque(); self.ring_len = 0
        self.dead = False; self.user_killed = False
        self.deaths = []
        self.loop, self.outq = loop, outq
        self.pending = bytearray(); self.plock = threading.Lock()
        self.local = set()
        self.owner_ws = owner_ws
        self.owner = True

    def ingest(self, data: bytes):
        if not data: return
        self.ring.append(data); self.ring_len += len(data); self.last_out = time.time()
        while self.ring_len > RING_CAP:
            old = self.ring.popleft(); self.ring_len -= len(old)
        with self.plock:
            self.pending += data
            if len(self.pending) > 2_000_000:
                del self.pending[:len(self.pending) - 2_000_000]

    def drain(self):
        if not self.pending: return None
        with self.plock:
            chunk = bytes(self.pending); self.pending = bytearray()
        return chunk

    def _send_owner(self, obj):
        try:
            asyncio.run_coroutine_threadsafe(self.owner_ws.send(json.dumps(obj)), self.loop)
        except Exception as e:
            log(f"[{self.name}] owner send failed: {e}")

    def write(self, data: bytes):
        self._send_owner({"t": "i", "d": base64.b64encode(data).decode("ascii")})

    def resize(self, cols, rows):
        # Remote viewers never own size for an owner-backed session. The sidecar reports
        # the visible terminal size, and web follows it.
        return

    def scrollback(self, limit=SB_SEND):
        try: limit = max(0, min(RING_CAP, int(limit)))
        except Exception: limit = SB_SEND
        out, n = [], 0
        for b in reversed(self.ring):
            out.append(b); n += len(b)
            if n >= limit: break
        return b"".join(reversed(out))

    def tail_text(self, nbytes=1600, lines=0):
        raw = bytearray()
        for b in reversed(self.ring):
            raw[:0] = b
            if len(raw) >= nbytes: break
        s = clean_terminal_text(bytes(raw[-nbytes:]).decode("utf-8", "replace"))
        if lines: return "\n".join(s.split("\n")[-lines:]).rstrip()
        return s[-900:]

    def alive(self):
        return not self.dead

    def kill(self, by_user=True):
        self.user_killed = by_user
        self.dead = True
        self._send_owner({"t": "kill"})

sessions = {}    # name -> Session

def live_session_names():
    names = []
    for name, sess in list(sessions.items()):
        try:
            if sess.alive():
                names.append(name)
        except Exception:
            pass
    return names

def dump_thread_stacks(reason):
    try:
        frames = sys._current_frames()
        lines = [time.strftime("%m-%d %H:%M:%S") + " " + reason]
        for th in threading.enumerate():
            lines.append(f"\n--- thread {th.name} ident={th.ident} daemon={th.daemon} ---")
            frame = frames.get(th.ident)
            if frame is None:
                lines.append("(no frame)")
            else:
                lines.extend(traceback.format_stack(frame))
        path = LOG + ".stacks"
        with open(path, "a", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        log(f"[watchdog] dumped thread stacks to {path}")
    except Exception as e:
        log(f"[watchdog] stack dump failed: {e}")

def start_watchdog_thread():
    global WATCHDOG_STARTED
    if WATCHDOG_STARTED:
        return
    WATCHDOG_STARTED = True

    def run():
        last_report = 0.0
        while True:
            time.sleep(2)
            snap = watch_snapshot()
            stale = time.monotonic() - float(snap.get("last_tick", 0.0))
            if stale < LOOP_WATCHDOG_WARN:
                continue
            now = time.monotonic()
            if now - last_report < 30:
                continue
            last_report = now
            live = live_session_names()
            reason = f"[watchdog] event loop has not ticked for {stale:.1f}s; live={live}"
            log(reason)
            dump_thread_stacks(reason)
            if stale >= LOOP_WATCHDOG_EXIT:
                log("[watchdog] auto-restart disabled; leaving muxd running so lag cannot reset session state")

    threading.Thread(target=run, name="muxd-watchdog", daemon=True).start()

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

def normalized_cmd(cmd):
    return (cmd or "").strip()

def command_sig(cmd):
    cmd = normalized_cmd(cmd)
    return hashlib.sha256(cmd.encode("utf-8")).hexdigest()[:16] if cmd else ""

def session_has_command(sess):
    return bool(normalized_cmd(getattr(sess, "cmd", "")))

def fmt_age(seconds):
    try: seconds = max(0.0, float(seconds))
    except Exception: return "unknown"
    if seconds < 1.5: return "just now"
    if seconds < 60: return "%ds ago" % round(seconds)
    if seconds < 3600: return "%dm ago" % round(seconds / 60)
    return "%dh ago" % round(seconds / 3600)

def tail_looks_at_shell_prompt(tail):
    lines = [ln.strip() for ln in clean_terminal_text(tail).splitlines() if ln.strip()][-8:]
    for line in lines:
        if re.match(r"^(?:PS\s+)?[A-Za-z]:\\[^>]{0,180}>\s*$", line):
            return True
        if re.match(r"^[\w.\-]+@[\w.\-]+:[^#$]{0,180}[#$]\s*$", line):
            return True
    return False

def session_agent_status(sess, alive=None, tail=None, now=None):
    if now is None: now = time.time()
    if alive is None:
        try: alive = bool(sess.alive())
        except Exception: alive = False
    has_cmd = session_has_command(sess)
    if not alive:
        return {"agentState": "dormant", "agentLabel": "dormant",
                "agentDetail": "no shell or agent is running until you relaunch it",
                "agentConfidence": "high", "needsAttention": False}
    if not has_cmd:
        return {"agentState": "neutral", "agentLabel": "plain shell",
                "agentDetail": "live terminal, no agent command registered",
                "agentConfidence": "high", "needsAttention": False}
    if tail is None:
        try: tail = sess.tail_text(nbytes=4000)
        except Exception: tail = ""
    if tail_looks_at_shell_prompt(tail):
        return {"agentState": "stopped", "agentLabel": "agent stopped",
                "agentDetail": "the command-backed session returned to a shell prompt",
                "agentConfidence": "high", "needsAttention": True}
    last_out = float(getattr(sess, "last_out", 0) or 0)
    created = float(getattr(sess, "created", 0) or 0)
    last_age = (now - last_out) if last_out else float("inf")
    created_age = (now - created) if created else float("inf")
    if last_age <= AGENT_WORKING_FRESH or created_age <= AGENT_STARTING_GRACE:
        return {"agentState": "working", "agentLabel": "agent working",
                "agentDetail": "terminal output updated " + fmt_age(last_age),
                "agentConfidence": "medium", "needsAttention": False,
                "lastOutAgeMs": int(max(0.0, last_age) * 1000)}
    return {"agentState": "attention", "agentLabel": "waiting for you",
            "agentDetail": "no terminal output for " + fmt_age(last_age),
            "agentConfidence": "medium", "needsAttention": True,
            "lastOutAgeMs": int(max(0.0, last_age) * 1000)}

def session_payload(name, sess):
    alive = False
    try: alive = bool(sess.alive())
    except Exception: alive = False
    has_cmd = session_has_command(sess)
    owner = bool(getattr(sess, "owner", False))
    kind = "command" if has_cmd else ("shell" if alive else "dormant")
    tail = sess.tail_text()
    agent = session_agent_status(sess, alive=alive, tail=tail)
    return {"name": name, "alive": alive, "created": int(sess.created * 1000),
            "lastOut": int(sess.last_out * 1000), "cols": sess.cols, "rows": sess.rows,
            "tail": tail, "heal": sess.heal, "localViewers": len(sess.local),
            "localFirst": owner or len(sess.local) > 0, "owner": owner,
            "hasCommand": has_cmd, "shellOnly": alive and not has_cmd,
            "ready": alive, "kind": kind, "cmdSig": command_sig(getattr(sess, "cmd", "")),
            "agentState": agent["agentState"], "agentLabel": agent["agentLabel"],
            "agentDetail": agent["agentDetail"], "agentConfidence": agent["agentConfidence"],
            "needsAttention": agent["needsAttention"], "lastOutAgeMs": agent.get("lastOutAgeMs", 0)}

def needs_relaunch_for_command(prev, requested_cmd):
    requested = normalized_cmd(requested_cmd)
    if not requested:
        return False
    try:
        if not prev.alive():
            return True
    except Exception:
        return True
    return command_sig(getattr(prev, "cmd", "")) != command_sig(requested)

def sess_list():
    return [session_payload(n, s) for n, s in sessions.items()]

async def spawn_session_off_loop(s):
    await asyncio.get_running_loop().run_in_executor(None, s.spawn)
    return s

async def new_session_off_loop(name, cmd, cwd, cols, rows, loop, outq, heal=False):
    s = Session(name, cmd, cwd, cols, rows, loop, outq, heal=heal, spawn_now=False)
    await spawn_session_off_loop(s)
    return s

async def main():
    start_watchdog_thread()
    loop = asyncio.get_running_loop()
    outq = asyncio.Queue()

    async def loop_monitor():
        last = time.monotonic()
        last_warn = 0.0
        while True:
            await asyncio.sleep(0.5)
            now = time.monotonic()
            lag = max(0.0, now - last - 0.5)
            last = now
            with WATCH_LOCK:
                WATCH["last_tick"] = now
                WATCH["last_lag"] = lag
                WATCH["max_lag"] = max(float(WATCH.get("max_lag", 0.0)), lag)
            if lag >= LOOP_WATCHDOG_WARN and now - last_warn >= 30:
                last_warn = now
                log(f"[watchdog] event loop lag {lag:.3f}s")
    asyncio.create_task(loop_monitor())

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
                    try: await spawn_session_off_loop(s); log(f"[heal] {s.name} shell died -> respawned + resume queued")
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
        async def ensure_local_session(first, spawn_if_missing):
            name = SAFE(first.get("s", ""))
            if not name:
                return None, "session name required", False
            prev = sessions.get(name)
            requested_cmd = (first.get("cmd", "") or "").strip()
            requested_cwd = first.get("cwd", "") or ""
            requested_heal = bool(first.get("heal")) if ("heal" in first) else bool(prev.heal if prev else False)
            cols = int(first.get("cols") or (prev.cols if prev else 140))
            rows = int(first.get("rows") or (prev.rows if prev else 40))
            if prev and prev.alive() and not needs_relaunch_for_command(prev, requested_cmd):
                prev.heal = requested_heal
                if requested_cmd and requested_cmd != prev.cmd:
                    prev.cmd = requested_cmd
                if requested_cwd:
                    prev.cwd = requested_cwd
                if first.get("cols"): prev.resize(cols, rows)
                manifest_save()
                return prev, "", False
            if prev and not spawn_if_missing:
                return prev, "", False
            if not prev and not spawn_if_missing:
                return None, "no such session: " + name, False

            # Local `open` is an explicit user start/attach action. If a dormant placeholder has a
            # saved resume command, reuse it; plain web tab selection still sends relay create{cmd:""}
            # and remains stopped for unarmed sessions.
            cmd = requested_cmd or (prev.cmd if prev else "")
            cwd = requested_cwd or (prev.cwd if prev else "")
            if prev:
                prev.kill(by_user=False)
            try:
                s = await new_session_off_loop(name, cmd, cwd, cols, rows, loop, outq, heal=requested_heal)
            except Exception as e:
                return None, "spawn failed: " + str(e), False
            sessions[name] = s
            manifest_save()
            return s, "", True

        async def handler(ws):
            peer = str(getattr(ws, "remote_address", ""))
            started = time.perf_counter()
            req_t = "?"
            failed = False
            with WATCH_LOCK:
                WATCH["local_active"] += 1
                WATCH["local_total"] += 1
            try:
                try:
                    raw_first = await asyncio.wait_for(ws.recv(), LOCAL_FIRST_TIMEOUT)
                    first = json.loads(raw_first if isinstance(raw_first, str) else raw_first.decode("utf-8", "replace"))
                    req_t = str(first.get("t", "?"))
                except asyncio.TimeoutError:
                    failed = True
                    log(f"[local] first frame timeout from {peer}")
                    return
                except Exception as e:
                    failed = True
                    log(f"[local] invalid first frame from {peer}: {type(e).__name__}: {e}")
                    return

                if first.get("t") == "info":
                    snap = watch_snapshot()
                    await ws.send(json.dumps({"t": "info", "protocol": PROTOCOL, "caps": CAPS,
                                              "host": os.environ.get("COMPUTERNAME", "pc"),
                                              "sessions": len(sessions), "pid": os.getpid(),
                                              "uptimeSec": int(time.time() - STARTED),
                                              "loopLagMs": round(float(snap.get("last_lag", 0.0)) * 1000, 1),
                                              "maxLoopLagMs": round(float(snap.get("max_lag", 0.0)) * 1000, 1),
                                              "localActive": max(0, int(snap.get("local_active", 0)) - 1),
                                              "localTotal": snap.get("local_total", 0),
                                              "localErrors": snap.get("local_errors", 0),
                                              "lastLocalMs": round(float(snap.get("last_local_ms", 0.0)), 1),
                                              "lastLocalT": snap.get("last_local_t", "")})); return
                if first.get("t") == "ls":
                    await ws.send(json.dumps({"t": "ls", "list": sess_list()})); return
                if first.get("t") == "kill":
                    name = SAFE(first.get("s", ""))
                    if not name or name not in sessions:
                        await ws.send(json.dumps({"t": "err", "m": "no such session: " + name})); return
                    sessions[name].kill(by_user=True)
                    del sessions[name]
                    manifest_save()
                    await ws.send(json.dumps({"t": "killed", "s": name})); return
                if first.get("t") == "owner":
                    name = SAFE(first.get("s", ""))
                    if not name:
                        await ws.send(json.dumps({"t": "err", "m": "session name required"})); return
                    prev = sessions.get(name)
                    if prev and prev.alive():
                        if bool(getattr(prev, "owner", False)):
                            await ws.send(json.dumps({"t": "err", "m": "session already has a visible local owner: " + name})); return
                        prev.kill(by_user=False)
                    owner = OwnerSession(name, first.get("cmd", ""), first.get("cwd", ""),
                                         int(first.get("cols") or 140), int(first.get("rows") or 40),
                                         loop, outq, ws, heal=bool(first.get("heal")))
                    sessions[name] = owner
                    manifest_save()
                    await ws.send(json.dumps({"t": "owner-ok", "s": name, "alive": True}))
                    outq.put_nowait(("dead", name, ""))  # force relay session-list refresh
                    try:
                        async for raw in ws:
                            try:
                                m = json.loads(raw if isinstance(raw, str) else raw.decode("utf-8", "replace"))
                            except Exception:
                                continue
                            mt = m.get("t")
                            if mt == "o":
                                owner.ingest(base64.b64decode(m.get("d", "")))
                            elif mt == "size":
                                owner.cols = max(20, int(m.get("cols") or owner.cols))
                                owner.rows = max(8, int(m.get("rows") or owner.rows))
                            elif mt == "dead":
                                owner.dead = True
                                break
                    finally:
                        if sessions.get(name) is owner:
                            owner.dead = True
                            outq.put_nowait(("dead", name, ""))
                    return
                if first.get("t") == "create":
                    s, err, created = await ensure_local_session(first, True)
                    if err:
                        await ws.send(json.dumps({"t": "err", "m": err})); return
                    await ws.send(json.dumps({"t": "created", "s": s.name, "created": created, "alive": s.alive()})); return
                if first.get("t") == "open":
                    s, err, _created = await ensure_local_session(first, True)
                else:
                    s, err, _created = await ensure_local_session(first, False)
                if err:
                    await ws.send(json.dumps({"t": "err", "m": err})); return
                lq = asyncio.Queue(); s.local.add(lq)
                if first.get("cols"): s.resize(int(first.get("cols")), int(first.get("rows") or 40))
                try:
                    sb_limit = int(first.get("sb") if first.get("sb") is not None else LOCAL_SB_SEND)
                    if sb_limit > 0:
                        await ws.send(s.scrollback(sb_limit))
                    async def pump():
                        while True:
                            data = await lq.get()
                            await ws.send(data)
                    pt = asyncio.create_task(pump())
                    try:
                        async for raw in ws:
                            if isinstance(raw, (bytes, bytearray)):
                                s.write(bytes(raw)); continue
                            try: m = json.loads(raw)
                            except Exception: continue
                            if m.get("t") == "i": s.write(base64.b64decode(m.get("d", "")))
                            elif m.get("t") == "resize": s.resize(m.get("cols", 140), m.get("rows", 40))
                    finally: pt.cancel()
                finally:
                    s.local.discard(lq)
            except Exception as e:
                failed = True
                log(f"[local] {req_t} handler failed for {peer}: {type(e).__name__}: {e}")
                try:
                    await ws.send(json.dumps({"t": "err", "m": f"local handler failed: {type(e).__name__}"}))
                except Exception:
                    pass
            finally:
                elapsed_ms = (time.perf_counter() - started) * 1000
                with WATCH_LOCK:
                    WATCH["local_active"] = max(0, int(WATCH.get("local_active", 0)) - 1)
                    WATCH["last_local_ms"] = elapsed_ms
                    WATCH["last_local_t"] = req_t
                    WATCH["last_local_peer"] = peer
                    if failed:
                        WATCH["local_errors"] += 1
                if failed or (req_t in ("info", "ls", "kill", "create") and elapsed_ms > 1000):
                    log(f"[local] {req_t} from {peer} finished in {elapsed_ms:.1f}ms failed={failed}")
        try:
            async with _ws.serve(handler, "127.0.0.1", LOCAL_PORT, ping_interval=20,
                                 ping_timeout=10, close_timeout=2, max_queue=32):
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
                    await ws.send(json.dumps({"t": "hello", "host": os.environ.get("COMPUTERNAME", "pc"),
                                              "protocol": PROTOCOL, "caps": CAPS, "sessions": sess_list()}))

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
                            await asyncio.sleep(LAN_RETURN_INTERVAL)
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
                                prev = sessions.get(name)
                                requested_cmd = (m.get("cmd", "") or "").strip()
                                if prev and prev.alive() and not needs_relaunch_for_command(prev, requested_cmd):
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
                                    try:
                                        sessions[name] = await new_session_off_loop(name, cmd, cwd, cols, rows, loop, outq, heal=heal)
                                    except Exception as e:
                                        log(f"[{name}] spawn failed: {e}")
                                        await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))
                                        continue
                                    manifest_save()
                                await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))
                            elif t == "heal" and name in sessions:
                                sessions[name].heal = bool(m.get("on")); manifest_save()
                            elif t == "i" and name in sessions:
                                sessions[name].write(base64.b64decode(m.get("d", "")))
                            elif t == "resize" and name in sessions:
                                s = sessions[name]
                                if s.local:
                                    log(f"[{name}] ignored remote resize while local viewer is attached")
                                else:
                                    s.resize(m.get("cols", 140), m.get("rows", 40))
                            elif t == "sb" and name in sessions:
                                await ws.send(json.dumps({"t": "sb", "s": name,
                                                          "d": base64.b64encode(sessions[name].scrollback(m.get("max", SB_SEND))).decode()}))
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
