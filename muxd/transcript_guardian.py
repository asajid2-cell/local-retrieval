#!/usr/bin/env python3
# Transcript Guardian — never lose a claude/codex chat again.
#
# WHY THIS EXISTS
#   Claude Code / Codex own their transcript files (~/.claude/projects/**/*.jsonl,
#   ~/.codex/sessions/**/*.jsonl). Under a fork, a double-open, a resume race, or an
#   internal writer bug, one of those files can be TRUNCATED or silently stop being
#   written — and hours of a conversation vanish. We cannot patch the agents' writers.
#   So instead of trying to prevent every cause, this daemon makes the loss impossible
#   to KEEP: it byte-mirrors every actively-written transcript, and the instant a file
#   shrinks or is rewritten it captures a full point-in-time snapshot of the content it
#   had a moment earlier. The mirror + snapshots are an append-only safety net that no
#   truncation upstream can erase.
#
# WHAT IT WRITES  (under %LOCALAPPDATA%/CodexLocalRetrieval/transcript-guardian, or $GUARDIAN_DIR)
#   mirror/<key>.jsonl                     live faithful copy of the current transcript
#   snapshots/<key>/<seq>-<reason>-<ts>.jsonl   full copy captured at each risky event
#   manifest.json                          key -> {path, tool, sessionId}
#   state.json                             per-file tail offsets + signatures (crash-safe)
#   guardian.log
#
# It ONLY mirrors files that are warm (recently modified) — a cold historical transcript
# is not being written by anyone and cannot be truncated, so we never bulk-copy the whole
# history (keeps disk bounded on a near-full C:). Retention: last N snapshots per session
# + a global size cap, oldest pruned first. Mirrors of hot sessions are never pruned.
#
# RUN
#   python transcript_guardian.py            # daemon (single instance via loopback lock)
#   python transcript_guardian.py --once     # one scan pass then exit (tests)
#   python transcript_guardian.py --status   # print what's tracked + disk use
#   python transcript_guardian.py --recover <sid-substring>   # list recoverable artifacts

import os, sys, re, json, time, socket, glob, shutil, hashlib, threading, traceback

HOME = os.path.expanduser("~")

def default_data_dir():
    lad = os.environ.get("LOCALAPPDATA") or os.path.join(HOME, "AppData", "Local")
    return os.path.join(lad, "CodexLocalRetrieval", "transcript-guardian")

DATA      = os.environ.get("GUARDIAN_DIR") or default_data_dir()
MIRROR    = os.path.join(DATA, "mirror")
SNAPS     = os.path.join(DATA, "snapshots")
LOG_FILE  = os.path.join(DATA, "guardian.log")
STATE_F   = os.path.join(DATA, "state.json")
MANIFEST_F= os.path.join(DATA, "manifest.json")

CLAUDE_GLOB = os.path.join(HOME, ".claude", "projects", "*", "*.jsonl")
CODEX_GLOB  = os.path.join(HOME, ".codex", "sessions", "*", "*", "*", "*.jsonl")

HOT_WINDOW      = float(os.environ.get("GUARDIAN_HOT_HOURS", "6")) * 3600  # actively mirror files touched within this
HOT_POLL        = 2.0        # seconds between polls of hot files
DISCOVER_EVERY  = 20.0       # seconds between full globs for new/warmed files
PERIODIC_SNAP   = 15 * 60    # rolling snapshot of a CHANGED mirror even without a shrink
COOL_DROP       = 24 * 3600  # stop polling a file cold this long (mirror/state stay on disk)
MAX_SNAPS       = int(os.environ.get("GUARDIAN_MAX_SNAPS", "6"))        # per session
TOTAL_CAP       = int(float(os.environ.get("GUARDIAN_CAP_GB", "1")) * (1 << 30))
HEAD_N, TAIL_N  = 512, 256
LOCK_PORT       = 7690

# --------------------------------------------------------------------------- logging
def log(msg):
    line = time.strftime("%m-%d %H:%M:%S") + " " + msg
    try:
        os.makedirs(DATA, exist_ok=True)
        if os.path.exists(LOG_FILE) and os.path.getsize(LOG_FILE) > 3_000_000:
            try: os.replace(LOG_FILE, LOG_FILE + ".1")
            except OSError: pass
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass
    # also to stdout when interactive / --once
    try: print(line, flush=True)
    except Exception: pass

# --------------------------------------------------------------------------- identity
def parse_meta(path):
    """Return (tool, sessionId) for a transcript path."""
    p = path.replace("\\", "/")
    stem = os.path.splitext(os.path.basename(p))[0]
    if "/.claude/projects/" in p:
        return "claude", stem                       # claude file stem IS the session uuid
    if "/.codex/sessions/" in p:
        m = re.search(r'([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})', stem)
        return "codex", (m.group(1) if m else stem)  # uuid embedded in rollout filename
    return "other", stem

def key_for(path):
    tool, sid = parse_meta(path)
    h = hashlib.sha1(os.path.abspath(path).encode("utf-8", "replace")).hexdigest()[:6]
    safe_sid = re.sub(r'[^A-Za-z0-9._-]', '_', sid)[:60]
    return f"{tool}-{safe_sid}-{h}", tool, sid

# --------------------------------------------------------------------------- fs helpers
def read_head_tail(path, size):
    """(head_hex, tail_hex) signature of a file's ends — cheap change detector."""
    head = tail = b""
    try:
        with open(path, "rb") as f:
            head = f.read(HEAD_N)
            if size > HEAD_N:
                f.seek(max(0, size - TAIL_N))
                tail = f.read(TAIL_N)
    except OSError:
        return None, None
    return hashlib.sha1(head).hexdigest(), hashlib.sha1(tail).hexdigest()

def read_slice(path, start, end):
    with open(path, "rb") as f:
        f.seek(start)
        return f.read(end - start)

def append_bytes(path, data):
    with open(path, "ab") as f:
        f.write(data)

def dir_size(root):
    total = 0
    for dp, _dn, fns in os.walk(root):
        for fn in fns:
            try: total += os.path.getsize(os.path.join(dp, fn))
            except OSError: pass
    return total

# --------------------------------------------------------------------------- state
def load_json(path, default):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return default

def save_json(path, obj):
    tmp = path + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(obj, f)
        os.replace(tmp, path)
    except OSError:
        pass

class Guardian:
    def __init__(self):
        os.makedirs(MIRROR, exist_ok=True)
        os.makedirs(SNAPS, exist_ok=True)
        self.state = load_json(STATE_F, {})       # key -> record
        self.manifest = load_json(MANIFEST_F, {})  # key -> {path, tool, sessionId}
        self.last_discover = 0.0
        self.last_prune = 0.0

    # ---- discovery
    def discover(self):
        now = time.time()
        found = []
        for pat in (CLAUDE_GLOB, CODEX_GLOB):
            try: found.extend(glob.glob(pat))
            except OSError: pass
        for path in found:
            try: mtime = os.path.getmtime(path)
            except OSError: continue
            key, tool, sid = key_for(path)
            warm = (now - mtime) <= HOT_WINDOW
            if key in self.state:
                self.state[key]["mtime_disk"] = mtime
                continue
            if warm:
                self._track(key, path, tool, sid)

    def _track(self, key, path, tool, sid):
        self.manifest[key] = {"path": path, "tool": tool, "sessionId": sid}
        try: size = os.path.getsize(path)
        except OSError: size = 0
        # Baseline: copy the current content into the mirror so we have the full "before".
        mpath = os.path.join(MIRROR, key + ".jsonl")
        try:
            shutil.copyfile(path, mpath)
        except OSError:
            size = 0
        head, tail = read_head_tail(path, size)
        self.state[key] = {
            "offset": size, "size": size, "head": head, "tail": tail,
            "mtime": self._mtime(path), "last_snap": 0.0, "seq": 0,
        }
        log(f"[track] {self.manifest[key]['tool']} {sid}  ({size} bytes) -> mirror/{key}.jsonl")

    @staticmethod
    def _mtime(path):
        try: return os.path.getmtime(path)
        except OSError: return 0.0

    # ---- per-file processing
    def process(self, key):
        rec = self.state.get(key); man = self.manifest.get(key)
        if not rec or not man: return
        path = man["path"]
        try:
            size = os.path.getsize(path)
        except OSError:
            return  # vanished (rare) — keep mirror; nothing to do
        mtime = self._mtime(path)
        mpath = os.path.join(MIRROR, key + ".jsonl")

        # Cold? stop actively polling (dropped from the hot set by caller); mirror/state persist.
        offset = rec.get("offset", 0)

        if size < offset:
            # ---- TRUNCATION: file shrank below what we already mirrored. The mirror still holds the
            # full pre-shrink content — snapshot it NOW (this is the recoverable gold), then rebaseline.
            self._snapshot(key, mpath, reason="truncate", detail=f"{offset}->{size}")
            self._rebaseline(key, path, size)
            log(f"[TRUNCATE] {man['tool']} {man['sessionId']}  {offset} -> {size} bytes  (pre-image snapshotted)")
            return

        head, tail = read_head_tail(path, size)
        if head is not None and rec.get("head") is not None and head != rec["head"]:
            # ---- REWRITE-IN-PLACE: same-or-larger size but the beginning changed (a fork/branch
            # rewrote the file). Snapshot the old mirror, then rebaseline from the new content.
            self._snapshot(key, mpath, reason="rewrite", detail=f"head-changed@{size}")
            self._rebaseline(key, path, size)
            log(f"[REWRITE] {man['tool']} {man['sessionId']}  head changed at {size} bytes  (pre-image snapshotted)")
            return

        if size > offset:
            # ---- GROWTH: append the new bytes to the mirror (the normal, happy path). But FIRST verify
            # continuity: the byte just before our offset must still equal the mirror's last byte. If it
            # doesn't, the prefix was rewritten (a branch reusing the same head that grew past our offset)
            # and this is NOT a clean append — snapshot the old mirror and rebaseline instead of corrupting it.
            if offset > 0:
                try:
                    probe = read_slice(path, offset - 1, offset)
                    with open(mpath, "rb") as mf:
                        mf.seek(-1, os.SEEK_END); mtail = mf.read(1)
                except OSError:
                    return
                if probe != mtail:
                    self._snapshot(key, mpath, reason="diverge", detail=f"prefix-rewrite@{offset}")
                    self._rebaseline(key, path, size)
                    log(f"[DIVERGE] {man['tool']} {man['sessionId']}  prefix rewritten before offset {offset}")
                    return
            try:
                chunk = read_slice(path, offset, size)
            except OSError:
                return  # writer holds it exclusively this instant; retry next tick
            try:
                append_bytes(mpath, chunk)
            except OSError:
                return
            rec["offset"] = size

        elif size == offset and mtime != rec.get("mtime") and tail is not None and tail != rec.get("tail"):
            # Same length, but the tail bytes changed — an equal-size in-place edit. Treat as a rewrite:
            # snapshot the old mirror, rebaseline. (Rare; covers the one case size+head checks miss.)
            self._snapshot(key, mpath, reason="edit", detail=f"tail-changed@{size}")
            self._rebaseline(key, path, size)
            log(f"[EDIT] {man['tool']} {man['sessionId']}  equal-size tail change at {size} bytes")
            return

        rec["size"], rec["head"], rec["tail"], rec["mtime"] = size, head, tail, mtime

        # Rolling snapshot: even with no shrink, keep a recent full copy so a same-size stealth rewrite
        # (which the signatures could theoretically miss) still has a recent recoverable image. Only roll
        # when the mirror actually CHANGED since the last roll — a warm-but-idle session must not re-copy
        # identical content every interval (wasteful on a near-full disk).
        now = time.time()
        if (now - rec.get("last_snap", 0.0) >= PERIODIC_SNAP
                and size > 0 and size != rec.get("last_snap_size")):
            self._snapshot(key, mpath, reason="roll", detail=f"{size}")
            rec["last_snap"] = now
            rec["last_snap_size"] = size

    def _rebaseline(self, key, path, size):
        """Reset the mirror to the current file content and re-arm signatures."""
        mpath = os.path.join(MIRROR, key + ".jsonl")
        try:
            shutil.copyfile(path, mpath)
        except OSError:
            return
        head, tail = read_head_tail(path, size)
        rec = self.state[key]
        rec.update(offset=size, size=size, head=head, tail=tail, mtime=self._mtime(path))

    def _snapshot(self, key, mpath, reason, detail):
        try:
            if not os.path.exists(mpath) or os.path.getsize(mpath) == 0:
                return
        except OSError:
            return
        rec = self.state.setdefault(key, {})
        rec["seq"] = int(rec.get("seq", 0)) + 1
        sdir = os.path.join(SNAPS, key)
        os.makedirs(sdir, exist_ok=True)
        ts = time.strftime("%Y%m%d-%H%M%S")
        dest = os.path.join(sdir, f"{rec['seq']:04d}-{reason}-{ts}.jsonl")
        try:
            shutil.copyfile(mpath, dest)
        except OSError:
            return
        self._prune_session(sdir)

    def _prune_session(self, sdir):
        try:
            snaps = sorted(glob.glob(os.path.join(sdir, "*.jsonl")), key=os.path.getmtime)
        except OSError:
            return
        for old in snaps[:-MAX_SNAPS]:
            try: os.remove(old)
            except OSError: pass

    def prune_total(self):
        now = time.time()
        if now - self.last_prune < 60:
            return
        self.last_prune = now
        total = dir_size(DATA)
        if total <= TOTAL_CAP:
            return
        # Prune oldest snapshots across all sessions until under cap. Never touch mirrors.
        snaps = []
        for dp, _dn, fns in os.walk(SNAPS):
            for fn in fns:
                fp = os.path.join(dp, fn)
                try: snaps.append((os.path.getmtime(fp), os.path.getsize(fp), fp))
                except OSError: pass
        snaps.sort()
        for _mt, sz, fp in snaps:
            if total <= TOTAL_CAP: break
            try:
                os.remove(fp); total -= sz
            except OSError: pass
        log(f"[prune] over cap — trimmed snapshots to {total//(1<<20)} MB")

    # ---- main loop
    def tick(self):
        now = time.time()
        if now - self.last_discover >= DISCOVER_EVERY:
            self.discover(); self.last_discover = now
        for key in list(self.state.keys()):
            man = self.manifest.get(key)
            if not man:
                continue
            # only actively poll warm files; cold ones can't be written so they can't be lost
            cold = (now - self.state[key].get("mtime_disk", self.state[key].get("mtime", 0))) > COOL_DROP
            if cold:
                continue
            try:
                self.process(key)
            except Exception:
                log("[err] " + traceback.format_exc().splitlines()[-1])
        save_json(STATE_F, self.state)
        save_json(MANIFEST_F, self.manifest)
        self.prune_total()

    def run(self):
        log(f"guardian up — data={DATA}  hot_window={HOT_WINDOW/3600:.0f}h  cap={TOTAL_CAP//(1<<30)}GB")
        self.discover(); self.last_discover = time.time()
        while True:
            try:
                self.tick()
            except Exception:
                log("[loop-err] " + traceback.format_exc().splitlines()[-1])
            time.sleep(HOT_POLL)

# --------------------------------------------------------------------------- lock / cli
PIDFILE = os.path.join(DATA, "guardian.pid")

def acquire_lock():
    """Single-instance mutex: bind the loopback port (the authority — bind fails if another instance
    holds it). Then publish our pid and drain the accept backlog so external liveness probes stay clean."""
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.bind(("127.0.0.1", LOCK_PORT))
        s.listen(16)
    except OSError:
        try: s.close()
        except OSError: pass
        return None
    try:
        os.makedirs(DATA, exist_ok=True)
        with open(PIDFILE, "w", encoding="utf-8") as f:
            f.write(str(os.getpid()))
    except OSError:
        pass
    def _drain():
        while True:
            try:
                conn, _ = s.accept(); conn.close()
            except OSError:
                return
    threading.Thread(target=_drain, name="guardian-lock-drain", daemon=True).start()
    return s  # keep ref alive for process lifetime

def cmd_status():
    man = load_json(MANIFEST_F, {}); st = load_json(STATE_F, {})
    print(f"guardian data dir: {DATA}")
    print(f"tracked sessions:  {len(man)}")
    for key, m in sorted(man.items()):
        sz = st.get(key, {}).get("size", 0)
        sdir = os.path.join(SNAPS, key)
        nsnap = len(glob.glob(os.path.join(sdir, "*.jsonl"))) if os.path.isdir(sdir) else 0
        print(f"  {m['tool']:6} {m['sessionId']:40} mirror={sz:>9}B snaps={nsnap}")
    if os.path.isdir(DATA):
        print(f"disk use:          {dir_size(DATA)//(1<<20)} MB (cap {TOTAL_CAP//(1<<30)} GB)")

def cmd_recover(needle):
    man = load_json(MANIFEST_F, {})
    hits = [(k, m) for k, m in man.items() if needle.lower() in (m["sessionId"] + " " + m["path"]).lower()]
    if not hits:
        print(f"no tracked session matches '{needle}'"); return
    for key, m in hits:
        print(f"\n== {m['tool']} {m['sessionId']}")
        print(f"   source:   {m['path']}")
        mpath = os.path.join(MIRROR, key + ".jsonl")
        if os.path.exists(mpath):
            print(f"   MIRROR:   {mpath}  ({os.path.getsize(mpath)} bytes)  <- current full copy")
        sdir = os.path.join(SNAPS, key)
        for sp in sorted(glob.glob(os.path.join(sdir, "*.jsonl"))):
            print(f"   snapshot: {sp}  ({os.path.getsize(sp)} bytes)")

def main():
    args = sys.argv[1:]
    if args and args[0] == "--status":  return cmd_status()
    if args and args[0] == "--recover": return cmd_recover(args[1] if len(args) > 1 else "")
    g = Guardian()
    if args and args[0] == "--once":
        g.discover(); g.tick(); log("[--once] pass complete"); return
    lock = acquire_lock()
    if lock is None:
        log("another guardian instance already holds the lock — exiting")
        return
    g.run()

if __name__ == "__main__":
    main()
