# muxd — PC-local terminal session host for the multiplex.
#
# THE POINT (from full-review.md): the agent's console must be owned by THIS machine, not by an
# ssh pipe from the VPS. muxd owns a ConPTY per session; claude/codex live in it. Wi-Fi drops,
# VPS reboots, relay deploys — the agent never notices; viewers just reattach. muxd dials OUT
# to the VPS relay (no inbound port on the PC) and multiplexes all sessions over one WebSocket.
#
# Protocol (JSON text frames over ws):
#   muxd -> relay:  hello{host,sessions} . sessions{list} . o{s,d:b64} . sb{s,d:b64} . killed{s} . pong
#   relay -> muxd:  create{s,rid,cols,rows,relaunch,heal} . i{s,d:b64} . resize{s,cols,rows} .
#                    kill{s} . rename{s,to} . heal{s,on} . tail{s,rid,lines} . sb{s} . ping
#
# State: sessions.json manifest (resume commands) -> muxd restart / PC reboot lists unarmed sessions
# as dormant placeholders. Only sessions explicitly armed with heal auto-start.
import asyncio, base64, collections, ctypes, glob, hashlib, json, os, queue, re, socket, subprocess, sys, tempfile, threading, time, traceback
from ctypes import wintypes
from datetime import datetime, timedelta, timezone
import faulthandler
try: faulthandler.enable(open(os.path.join(os.path.expanduser("~"), "muxd", "muxd.crash"), "a"))
except Exception: pass
from winpty import PtyProcess

HOME = os.path.expanduser("~")
DIR = os.path.join(HOME, "muxd")
MANIFEST = os.path.join(DIR, "sessions.json")
LOG = os.path.join(DIR, "muxd.log")
ENVF = os.path.join(DIR, "muxd.env")
# {name: {pid, cwd, alive}} — the desktop app reads this to link a live claude/codex process to its mux
# TAB by walking the process's ancestor pids to a shell pid here (deterministic; no folder guessing). Lets
# a shell-launched agent be added to a collection / relaunched by its real chat id.
LIVE_TABS = os.path.join(DIR, "live-tabs.json")

_DURABLE_TEMP_SEQUENCE = 0

def _durable_checkpoint(fault, stage):
    if fault is not None:
        fault(stage)
    fault_file = os.environ.get("MUXD_TEST_PERSIST_FAULT_FILE", "")
    if not fault_file or not os.path.exists(fault_file):
        return
    try:
        with open(fault_file, encoding="utf-8") as stream:
            requested = json.load(stream)
    except Exception:
        return
    if (
        isinstance(requested, dict)
        and requested.get("stage") == stage
        and (not requested.get("file") or requested.get("file") == os.path.basename(MANIFEST))
    ):
        try:
            os.remove(fault_file)
        except OSError:
            pass
        raise OSError(f"injected persistence failure at {stage} for {os.path.basename(MANIFEST)}")

def _replace_write_through(source, destination):
    if os.name != "nt":
        os.replace(source, destination)
        return
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    move = kernel32.MoveFileExW
    move.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD]
    move.restype = wintypes.BOOL
    flags = 0x1 | 0x8  # MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
    if not move(source, destination, flags):
        raise ctypes.WinError(ctypes.get_last_error())

def _fsync_directory(directory):
    if os.name == "nt":
        return
    fd = os.open(directory, os.O_RDONLY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)

def _durable_commit_bytes(path, payload, fault=None):
    global _DURABLE_TEMP_SEQUENCE
    directory = os.path.dirname(path) or "."
    os.makedirs(directory, exist_ok=True)
    _DURABLE_TEMP_SEQUENCE += 1
    tmp = f"{path}.{os.getpid()}.{_DURABLE_TEMP_SEQUENCE}.tmp"
    try:
        _durable_checkpoint(fault, "before_write")
        with open(tmp, "xb", buffering=0) as stream:
            stream.write(payload)
            _durable_checkpoint(fault, "before_file_fsync")
            os.fsync(stream.fileno())
        _durable_checkpoint(fault, "before_replace")
        _replace_write_through(tmp, path)
        _durable_checkpoint(fault, "before_directory_fsync")
        _fsync_directory(directory)
        _durable_checkpoint(fault, "before_readback")
        with open(path, "rb") as stream:
            committed = stream.read()
        if committed != payload:
            raise OSError("committed state did not read back identically: " + path)
    finally:
        try:
            os.remove(tmp)
        except FileNotFoundError:
            pass

def durable_json_write(path, value, fault=None):
    payload = json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    if os.path.exists(path):
        with open(path, "rb") as stream:
            previous = stream.read()
        _durable_commit_bytes(path + ".bak", previous)
    _durable_commit_bytes(path, payload, fault=fault)

def _decode_persisted_json(path, payload):
    try:
        return json.loads(payload.decode("utf-8"))
    except Exception as exc:
        raise OSError("invalid persisted JSON: " + path) from exc

def durable_json_load(path, fallback):
    if os.path.exists(path):
        try:
            with open(path, "rb") as stream:
                return _decode_persisted_json(path, stream.read())
        except OSError as primary_error:
            backup = path + ".bak"
            if not os.path.exists(backup):
                raise primary_error
            with open(backup, "rb") as stream:
                backup_payload = stream.read()
            restored = _decode_persisted_json(backup, backup_payload)
            _durable_commit_bytes(path, backup_payload)
            return restored
    backup = path + ".bak"
    if os.path.exists(backup):
        with open(backup, "rb") as stream:
            backup_payload = stream.read()
        restored = _decode_persisted_json(backup, backup_payload)
        _durable_commit_bytes(path, backup_payload)
        return restored
    return fallback

# ---- SINGLE-OWNER GATE ---------------------------------------------------------------------------
# A session's transcript is safe ONLY while exactly one live agent owns it. Two live processes on the
# same session id is what silently freezes/truncates a Claude conversation. muxd must therefore NEVER
# resume a session id that is already alive somewhere else on this PC — not on boot-arm, not on
# self-heal, not on an explicit create. These helpers answer "is this id live right now?" from ground
# truth (Claude's own per-process registry + live agent command lines), so muxd can refuse a duplicate.

def _pid_alive(pid):
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return False
    if pid <= 0:
        return False
    try:
        k32 = ctypes.windll.kernel32
        h = k32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return False
        code = ctypes.c_ulong()
        k32.GetExitCodeProcess(h, ctypes.byref(code))
        k32.CloseHandle(h)
        return code.value == 259                   # STILL_ACTIVE
    except Exception:
        return False

def _parse_resume_id(cmd):
    cmd = cmd or ""
    m = re.search(r'--resume\s+["\']?([0-9A-Za-z][0-9A-Za-z._-]*)["\']?', cmd)   # claude --resume <id>
    if m:
        return m.group(1)
    m = re.search(r'\bresume\b(.*)', cmd)                            # codex resume [flags] <id>
    if m:
        for parts in re.findall(r'"([^"]*)"|\'([^\']*)\'|(\S+)', m.group(1)):
            tok = next((x for x in parts if x), "")
            if tok.startswith('-'):
                continue
            if tok.lower() in ("resume", "--include-non-interactive"):
                continue
            return tok
    return ""

def _try_agent_cmdlines():
    """[(pid, cmdline)] for every live claude.exe/codex.exe — one CIM query (used for codex, which has
    no registry). Best-effort: a failure just means codex-resume conflicts aren't caught this pass."""
    out = []
    try:
        ps = ("Get-CimInstance Win32_Process -Filter \"Name='claude.exe' or Name='codex.exe' or Name='node.exe'\" | "
              "Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress")
        r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps],
                           capture_output=True, text=True, timeout=15)
        if r.returncode != 0:
            return False, out, (r.stderr or r.stdout or "CIM process query failed").strip()
        data = json.loads(r.stdout) if r.stdout.strip() else []
        if isinstance(data, dict):
            data = [data]
        for d in data:
            try:
                out.append((int(d.get("ProcessId") or 0), d.get("CommandLine") or ""))
            except Exception:
                pass
    except Exception as e:
        return False, out, str(e)
    return True, out, ""

def _agent_cmdlines():
    ok, out, _ = _try_agent_cmdlines()
    return out

def try_live_session_ids():
    """sid(lower) -> pid for every live claude/codex session on this PC. Claude sessions come from its
    own registry (~/.claude/sessions/<pid>.json — covers IDLE and FORKED ones with no id on the command
    line); codex + any explicit resume come from live agent command lines."""
    out = {}
    try:
        for fn in glob.glob(os.path.join(HOME, ".claude", "sessions", "*.json")):
            file_pid = 0
            try: file_pid = int(os.path.splitext(os.path.basename(fn))[0])
            except Exception: pass
            if file_pid and not _pid_alive(file_pid):
                continue
            try:
                with open(fn, encoding="utf-8") as f:
                    j = json.load(f)
                pid = j.get("pid"); sid = j.get("sessionId")
                if sid and _pid_alive(pid):
                    out.setdefault(str(sid).lower(), int(pid))
            except Exception as e:
                return False, out, f"could not verify Claude live-session registry file {os.path.basename(fn)}: {e}"
    except Exception as e:
        return False, out, f"could not enumerate Claude live-session registry: {e}"
    ok, cmdlines, detail = _try_agent_cmdlines()
    if not ok:
        return False, out, detail or "could not verify live claude/codex processes"
    for pid, cl in cmdlines:
        sid = _parse_resume_id(cl)
        if sid and _pid_alive(pid):
            out.setdefault(sid.lower(), pid)
    return True, out, ""

def live_session_ids():
    ok, out, _ = try_live_session_ids()
    return out

def _candidate_resume_ids(cmd, ids=None):
    out = []
    def add(v):
        v = str(v or "").strip()
        if v and v.lower() not in [x.lower() for x in out]:
            out.append(v)
    add(_parse_resume_id(cmd))
    if ids:
        for v in ids:
            add(v)
    return out

def conflict_detail(conflict):
    if len(conflict) > 2 and conflict[2]:
        return conflict[2]
    return f"session {conflict[0]} already live (pid {conflict[1]})"

def resume_conflict(cmd, live=None, ids=None):
    """If cmd would resume a session id that is ALREADY live elsewhere, return (sid, pid); else None.
    A fresh session (no resume id) never conflicts."""
    candidates = _candidate_resume_ids(cmd, ids)
    if not candidates:
        return None
    detail = ""
    if live is None:
        ok, live, detail = try_live_session_ids()
    elif isinstance(live, tuple):
        ok, live, detail = live
    else:
        ok = True
    if not ok:
        return (candidates[0], 0, detail or "could not verify live claude/codex processes")
    for sid in candidates:
        pid = live.get(sid.lower())
        if pid:
            return (sid, pid, "")
    return None

# ---- TRANSCRIPT GUARDIAN keep-alive --------------------------------------------------------------
# The guardian (transcript_guardian.py) byte-mirrors every live transcript so no fork/truncation can
# erase a conversation. muxd is the always-on process, so muxd keeps it running — decoupled from WHO
# launched it: if nothing is listening on the guardian's single-instance lock port, spawn one.
GUARDIAN_PY   = os.path.join(DIR, "transcript_guardian.py")
GUARDIAN_PORT = 7690
def _guardian_data_dir():
    lad = os.environ.get("LOCALAPPDATA") or os.path.join(HOME, "AppData", "Local")
    return os.environ.get("GUARDIAN_DIR") or os.path.join(lad, "CodexLocalRetrieval", "transcript-guardian")
GUARDIAN_PIDFILE = os.path.join(_guardian_data_dir(), "guardian.pid")

def _guardian_running():
    # Primary signal: the guardian's published pid is a live process. Robust (no TCP-backlog fragility).
    try:
        with open(GUARDIAN_PIDFILE, encoding="utf-8") as f:
            if _pid_alive(int(f.read().strip())):
                return True
    except (OSError, ValueError):
        pass
    # Fallback: probe the single-instance lock port (covers a missing/stale pidfile with a live instance).
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(0.4)
    try:
        s.connect(("127.0.0.1", GUARDIAN_PORT))
        return True
    except OSError:
        return False
    finally:
        try: s.close()
        except OSError: pass

def _guardian_keepalive():
    while True:
        try:
            if os.path.exists(GUARDIAN_PY) and not _guardian_running():
                flags = 0x00000008 | 0x08000000   # DETACHED_PROCESS | CREATE_NO_WINDOW
                subprocess.Popen([sys.executable, GUARDIAN_PY],
                                 creationflags=flags, close_fds=True,
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                log("[guardian] not running — launched transcript_guardian.py")
        except Exception as e:
            log(f"[guardian] keepalive error: {e}")
        time.sleep(30)

LOG_Q = queue.Queue(maxsize=4000)
INSTANCE_MUTEX_NAME = os.environ.get("INSTANCE_MUTEX_NAME", "Local\\CodexMuxdSessionHost")
_INSTANCE_MUTEX_HANDLE = None

def _log_writer():
    while True:
        line = LOG_Q.get()
        if line is None:
            return
        try:
            if os.path.exists(LOG) and os.path.getsize(LOG) > 2_000_000:
                os.replace(LOG, LOG + ".1")
            with open(LOG, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except Exception:
            pass

threading.Thread(target=_log_writer, name="muxd-log-writer", daemon=True).start()

def log(msg):
    line = time.strftime("%m-%d %H:%M:%S") + " " + msg
    try:
        LOG_Q.put_nowait(line)
    except Exception: pass

def _log_now(msg):
    line = time.strftime("%m-%d %H:%M:%S") + " " + msg
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass

def acquire_single_instance():
    """Return (ok, detail). On Windows, only one muxd may own relay/local/session state."""
    global _INSTANCE_MUTEX_HANDLE
    if os.name != "nt":
        return True, ""
    if _INSTANCE_MUTEX_HANDLE:
        return True, ""
    try:
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateMutexW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR]
        k32.CreateMutexW.restype = wintypes.HANDLE
        k32.CloseHandle.argtypes = [wintypes.HANDLE]
        k32.CloseHandle.restype = wintypes.BOOL
        handle = k32.CreateMutexW(None, False, INSTANCE_MUTEX_NAME)
        if not handle:
            return False, f"CreateMutexW failed: {ctypes.get_last_error()}"
        err = ctypes.get_last_error()
        if err == 183:  # ERROR_ALREADY_EXISTS
            k32.CloseHandle(handle)
            return False, "another muxd instance already owns the single-instance mutex"
        _INSTANCE_MUTEX_HANDLE = handle
        return True, ""
    except Exception as e:
        return False, f"single-instance mutex check failed: {e}"

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
CLAIM_ROOT = ENV.get("LAUNCH_CLAIM_ROOT") or os.path.join(
    os.environ.get("LOCALAPPDATA") or tempfile.gettempdir(),
    "CodexLocalRetrieval",
    "launch-claims",
)
CLAIM_TTL_SECONDS = max(10, int(ENV.get("LAUNCH_CLAIM_TTL_SECONDS", "120")))
PROTOCOL = 3
CAPS = ["ls", "info", "create", "createAck", "bind", "input", "open", "attach", "kill", "rename", "heal", "tail", "scrollback", "resize", "owner", "relaunch"]
STARTED = time.time()
AGENT_WORKING_FRESH = float(ENV.get("AGENT_WORKING_FRESH", "25"))
AGENT_STARTING_GRACE = float(ENV.get("AGENT_STARTING_GRACE", "45"))
ANSI_RE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]")
STALE_CSI_RE = re.compile(r"\[[0-?]*[ -/]*[@-~]")
CTRL_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")


def launch_candidate_ids(cmd="", ids=None):
    candidates = _candidate_resume_ids(cmd, ids)
    unique = {}
    for value in candidates:
        value = str(value or "").strip()
        if value and re.fullmatch(r"[A-Za-z0-9._-]+", value):
            unique.setdefault(value.lower(), value)
    return sorted(unique.values(), key=str.lower)


def _safe_identity(value):
    value = str(value or "").strip()
    return value if re.fullmatch(r"[A-Za-z0-9._-]+", value) else ""


def resolve_session_identity(cmd="", session_id="", aliases=None, ids=None):
    command_id = _safe_identity(_parse_resume_id(cmd))
    requested_id = _safe_identity(session_id)
    # The executable resume target is ground truth. A supplied id may be an archive alias, but it
    # must never hide which identity the process was actually told to run.
    canonical = command_id or requested_id
    alias_values = []
    for value in ([requested_id] if requested_id else []) + list(aliases or []) + _candidate_resume_ids(cmd, ids):
        value = _safe_identity(value)
        if not value or (canonical and value.lower() == canonical.lower()):
            continue
        if value.lower() not in [x.lower() for x in alias_values]:
            alias_values.append(value)
    all_ids = launch_candidate_ids(cmd, ([canonical] if canonical else []) + alias_values)
    return canonical, tuple(alias_values), all_ids


def claim_file_name(session_id):
    session_id = str(session_id or "").strip()
    cleaned = "".join(ch for ch in session_id if ch.isascii() and (ch.isalnum() or ch in "-_"))[:36] or "session"
    digest = hashlib.sha256(session_id.lower().encode("utf-8")).hexdigest()[:16]
    return f"{cleaned}-{digest}.claim.json"


def _claim_path(session_id):
    return os.path.join(CLAIM_ROOT, claim_file_name(session_id))


def _claim_timestamp(value):
    if not value:
        return None
    try:
        return datetime.fromisoformat(str(value).replace("Z", "+00:00")).astimezone(timezone.utc)
    except (TypeError, ValueError):
        return None


def _read_claim_metadata(path):
    try:
        with open(path, encoding="utf-8") as f:
            value = json.load(f)
        return value if isinstance(value, dict) else None
    except Exception:
        return None


def _claim_expiry(path, metadata):
    parsed = _claim_timestamp((metadata or {}).get("ExpiresUtc"))
    if parsed is not None:
        return parsed
    try:
        return datetime.fromtimestamp(os.path.getmtime(path), timezone.utc) + timedelta(seconds=CLAIM_TTL_SECONDS)
    except OSError:
        return datetime.now(timezone.utc) + timedelta(seconds=CLAIM_TTL_SECONDS)


def _blocking_claim_detail(session_id, path, metadata):
    expiry = _claim_expiry(path, metadata).astimezone()
    owner = "unknown owner"
    if metadata:
        owner = f"{metadata.get('OwnerProcess') or 'unknown'} pid {metadata.get('OwnerPid') or 0}"
    return (
        f"launch already pending for session {session_id} "
        f"({owner}, expires {expiry.strftime('%Y-%m-%d %H:%M:%S %Z')}); "
        "refusing to start another writer"
    )


class LaunchClaim:
    def __init__(self, ids, held):
        self.ids = tuple(ids)
        self._held = dict(held)
        self.paths = tuple(self._held)
        self._released = False

    def release(self):
        if self._released:
            return
        self._released = True
        for path, fd in list(self._held.items()):
            try:
                os.close(fd)
            except OSError:
                pass
            try:
                os.remove(path)
            except FileNotFoundError:
                pass
            except OSError as e:
                log(f"launch claim release failed for {path}: {e}")
        self._held.clear()


def _without_ignored_live(live, ignored):
    if not isinstance(live, tuple):
        live = (True, live, "")
    ok, values, detail = live
    filtered = dict(values or {})
    for session_id, pid in (ignored or {}).items():
        if filtered.get(str(session_id).lower()) == pid:
            filtered.pop(str(session_id).lower(), None)
    return ok, filtered, detail


def _pid_descends_from(pid, ancestor_pid):
    try:
        pid = int(pid)
        ancestor_pid = int(ancestor_pid)
    except (TypeError, ValueError):
        return None
    if pid <= 0 or ancestor_pid <= 0:
        return None
    if pid == ancestor_pid:
        return True
    script = (
        f"$p={pid};$a={ancestor_pid};$seen=@{{}};"
        "while($p -gt 0 -and -not $seen.ContainsKey($p)){"
        "$seen[$p]=$true;if($p -eq $a){Write-Output true;exit 0};"
        "$row=Get-CimInstance Win32_Process -Filter \"ProcessId=$p\" -ErrorAction Stop;"
        "if($null -eq $row){break};$p=[int]$row.ParentProcessId};"
        "Write-Output false"
    )
    try:
        result = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=15,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        if result.returncode != 0:
            return None
        value = result.stdout.strip().lower()
        return True if value == "true" else False if value == "false" else None
    except Exception:
        return None


def session_owned_live_ids(session, live):
    if not isinstance(live, tuple):
        live = (True, live, "")
    ok, values, detail = live
    if not ok:
        return False, {}, detail
    pty = getattr(session, "pty", None)
    root_pid = int(getattr(pty, "pid", 0) or 0) if pty is not None else 0
    ignored = {}
    for session_id in launch_candidate_ids(getattr(session, "cmd", ""), getattr(session, "ids", None)):
        pid = (values or {}).get(session_id.lower())
        if not pid:
            continue
        owned = _pid_descends_from(pid, root_pid)
        if owned is None:
            return False, {}, f"could not verify whether pid {pid} belongs to mux session {session.name}"
        if owned:
            ignored[session_id.lower()] = pid
    return True, ignored, ""

def owner_reconnect_ignored_live(candidate_ids, child_pid, live):
    if not isinstance(live, tuple):
        live = (True, live, "")
    ok, values, detail = live
    if not ok:
        return False, {}, detail
    try:
        child_pid = int(child_pid)
    except (TypeError, ValueError):
        child_pid = 0
    if child_pid <= 0:
        return False, {}, "visible owner reconnect did not report its live child pid"
    ignored = {}
    for session_id in candidate_ids:
        pid = (values or {}).get(str(session_id).lower())
        if not pid:
            continue
        owned = _pid_descends_from(pid, child_pid)
        if owned is None:
            return False, {}, f"could not verify whether pid {pid} belongs to the reconnecting visible owner"
        if not owned:
            return False, {}, f"session {session_id} is live outside the reconnecting visible owner"
        ignored[str(session_id).lower()] = pid
    return True, ignored, ""


def acquire_launch_claim(cmd="", ids=None, reason="muxd session launch", live=None, ignored_live=None):
    candidate_ids = launch_candidate_ids(cmd, ids)
    if not candidate_ids:
        return None, ""

    if live is None:
        live = try_live_session_ids()
    conflict = resume_conflict(cmd, live=_without_ignored_live(live, ignored_live), ids=candidate_ids)
    if conflict:
        return None, "refused: " + conflict_detail(conflict)

    try:
        os.makedirs(CLAIM_ROOT, exist_ok=True)
    except OSError as e:
        return None, f"couldn't create launch-claim directory: {e}"

    now = datetime.now(timezone.utc)
    targets = [(session_id, _claim_path(session_id)) for session_id in candidate_ids]
    for session_id, path in targets:
        if not os.path.exists(path):
            continue
        metadata = _read_claim_metadata(path)
        expiry = _claim_expiry(path, metadata)
        owner_is_dead_muxd = (
            str((metadata or {}).get("OwnerProcess") or "").lower() == "muxd"
            and not _pid_alive((metadata or {}).get("OwnerPid"))
        )
        if expiry <= now or owner_is_dead_muxd:
            try:
                os.remove(path)
                continue
            except FileNotFoundError:
                continue
            except OSError as e:
                return None, (
                    f"expired launch claim for session {session_id} could not be cleared ({e}); "
                    "refusing to risk a second writer"
                )
        return None, _blocking_claim_detail(session_id, path, metadata)

    expires = now + timedelta(seconds=CLAIM_TTL_SECONDS)
    metadata = {
        "SessionId": candidate_ids[0],
        "CandidateIds": candidate_ids,
        "OwnerPid": os.getpid(),
        "OwnerProcess": "muxd",
        "CreatedUtc": now.isoformat().replace("+00:00", "Z"),
        "ExpiresUtc": expires.isoformat().replace("+00:00", "Z"),
        "Reason": str(reason or "muxd session launch").strip(),
    }
    payload = json.dumps(metadata, indent=2).encode("utf-8")
    held = {}
    try:
        for session_id, path in sorted(targets, key=lambda item: item[1].lower()):
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_RDWR)
            held[path] = fd
            os.write(fd, payload)
            os.fsync(fd)
    except FileExistsError:
        LaunchClaim(candidate_ids, held).release()
        metadata = _read_claim_metadata(path)
        return None, _blocking_claim_detail(session_id, path, metadata)
    except Exception as e:
        LaunchClaim(candidate_ids, held).release()
        return None, f"couldn't reserve launch for session {session_id}: {e}"

    second_live = _without_ignored_live(try_live_session_ids(), ignored_live)
    conflict = resume_conflict(cmd, live=second_live, ids=candidate_ids)
    if conflict:
        LaunchClaim(candidate_ids, held).release()
        return None, "refused: " + conflict_detail(conflict)
    return LaunchClaim(candidate_ids, held), "reserved"


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


class RelayOutQueue(asyncio.Queue):
    """Bound relay backlog so an outage cannot exhaust memory and kill hosted sessions."""

    def __init__(self, maxsize=64):
        super().__init__(maxsize=maxsize)
        self.dropped = 0

    def put_nowait(self, item):
        if self.full():
            self.dropped += 1
            return False
        super().put_nowait(item)
        return True

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
    def __init__(self, name, cmd, cwd, cols, rows, loop, outq, heal=False, spawn_now=True,
                 ids=None, session_id="", aliases=None):
        self.name, self.cmd, self.cwd = name, cmd or "", cwd or DEFAULT_CWD
        self.session_id, self.aliases, self.ids = resolve_session_identity(
            self.cmd, session_id, aliases, ids
        )
        self.claim_paths = []
        self._launch_claim = None
        self.expected_owner = False
        self.owner_key = ""
        self.identity_pending = False
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
                if len(self.pending) > 512_000:         # last ~512KB unsent (the ring still holds history for reattach)
                    del self.pending[:len(self.pending) - 512_000]

    def drain(self):
        if not self.pending: return None
        with self.plock:
            chunk = bytes(self.pending); self.pending = bytearray()
        return chunk

    def _writer(self):
        # serialize input; slice large pastes into <=1KB writes so a big paste can't stall/garble ConPTY input.
        while True:
            item = self.wq.get()
            if item is None: return
            if isinstance(item, tuple):
                s, completed, outcome = item
            else:
                s, completed, outcome = item, None, None
            ok, detail = False, "PTY is not live"
            try:
                if self.pty is not None and not self.dead:
                    if len(s) <= 1024:
                        self.pty.write(s)
                        ok, detail = True, ""
                    else:
                        ok, detail = True, ""
                        for i in range(0, len(s), 1024):
                            if self.dead:
                                ok, detail = False, "PTY exited during input"
                                break
                            self.pty.write(s[i:i+1024]); time.sleep(0.004)
            except Exception as e:
                detail = "PTY input write failed"
                log(f"[{self.name}] write failed: {e}")
            finally:
                if completed is not None:
                    outcome.append((ok, detail))
                    completed.set()

    def write(self, data: bytes):
        try: self.wq.put(data.decode("utf-8", "replace"))
        except Exception as e: log(f"[{self.name}] enqueue failed: {e}")

    def write_confirmed(self, data: bytes, timeout=8):
        completed = threading.Event()
        outcome = []
        try:
            self.wq.put((data.decode("utf-8", "replace"), completed, outcome))
        except Exception as e:
            log(f"[{self.name}] confirmed enqueue failed: {e}")
            return False, "PTY input queue failed"
        if not completed.wait(timeout):
            return False, "PTY input write timed out"
        return outcome[0] if outcome else (False, "PTY input write did not report a result")

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
    def __init__(self, name, cmd, cwd, cols, rows, loop, outq, owner_ws, heal=False,
                 ids=None, session_id="", aliases=None, owner_key=""):
        self.name, self.cmd, self.cwd = name, cmd or "", cwd or DEFAULT_CWD
        self.session_id, self.aliases, self.ids = resolve_session_identity(
            self.cmd, session_id, aliases, ids
        )
        self.claim_paths = []
        self._launch_claim = None
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
        self.expected_owner = True
        self.owner_key = str(owner_key or "")
        self.identity_pending = False
        self.owner_exit_confirmed = False
        self.input_waiters = {}
        self.input_seq = 0

    def ingest(self, data: bytes):
        if not data: return
        self.ring.append(data); self.ring_len += len(data); self.last_out = time.time()
        while self.ring_len > RING_CAP:
            old = self.ring.popleft(); self.ring_len -= len(old)
        with self.plock:
            self.pending += data
            if len(self.pending) > 512_000:
                del self.pending[:len(self.pending) - 512_000]

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

    async def write_confirmed(self, data: bytes, timeout=8):
        self.input_seq += 1
        rid = f"input-{self.input_seq}"
        future = self.loop.create_future()
        self.input_waiters[rid] = future
        try:
            await self.owner_ws.send(json.dumps({
                "t": "i",
                "rid": rid,
                "d": base64.b64encode(data).decode("ascii"),
            }))
            return await asyncio.wait_for(future, timeout)
        except Exception:
            return False, "visible terminal input failed"
        finally:
            self.input_waiters.pop(rid, None)

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

def manifest_payload(source=None):
    source = sessions if source is None else source
    return {
        n: {"cmd": s.cmd, "cwd": s.cwd, "cols": s.cols, "rows": s.rows, "heal": s.heal,
            "sessionId": getattr(s, "session_id", "") or "",
            "aliases": list(getattr(s, "aliases", []) or []),
            "ids": list(getattr(s, "ids", []) or []),
            "owner": bool(getattr(s, "owner", False) or getattr(s, "expected_owner", False)),
            "ownerKey": str(getattr(s, "owner_key", "") or ""),
            "identityPending": bool(getattr(s, "identity_pending", False)),
            "deaths": [float(value) for value in list(getattr(s, "deaths", []) or [])[-16:]]}
        for n, s in source.items() if not s.user_killed
    }

def manifest_save(source=None):
    durable_json_write(MANIFEST, manifest_payload(source))

def manifest_load():
    return durable_json_load(MANIFEST, {})

_PERSISTED_SESSION_FIELDS = (
    "cmd",
    "cwd",
    "cols",
    "rows",
    "heal",
    "session_id",
    "aliases",
    "ids",
    "owner",
    "expected_owner",
    "owner_key",
    "identity_pending",
    "deaths",
    "user_killed",
)

def persisted_session_snapshot(session):
    snapshot = {}
    for field in _PERSISTED_SESSION_FIELDS:
        value = getattr(session, field, None)
        if isinstance(value, list):
            value = list(value)
        elif isinstance(value, tuple):
            value = tuple(value)
        snapshot[field] = value
    snapshot["_launch_claim"] = getattr(session, "_launch_claim", None)
    snapshot["claim_paths"] = list(getattr(session, "claim_paths", []) or [])
    return snapshot

def restore_persisted_session(session, snapshot):
    for field in _PERSISTED_SESSION_FIELDS:
        setattr(session, field, snapshot[field])
    session._launch_claim = snapshot["_launch_claim"]
    session.claim_paths = list(snapshot["claim_paths"])

def live_tabs_snapshot():
    out = {}
    for n, s in sessions.items():
        pid = 0
        try:
            p = getattr(s, "pty", None)
            if p is not None: pid = int(getattr(p, "pid", 0) or 0)
        except Exception:
            pid = 0
        try: alive = bool(s.alive())
        except Exception: alive = False
        out[n] = {
            "pid": pid,
            "cwd": getattr(s, "cwd", "") or "",
            "alive": alive,
            "hasCommand": session_has_command(s),
            "sessionId": getattr(s, "session_id", "") or "",
            "identityPending": bool(getattr(s, "identity_pending", False)),
        }
    return out

def write_live_tabs():
    try:
        durable_json_write(LIVE_TABS, live_tabs_snapshot())
    except Exception as e:
        log(f"live-tabs write failed: {e}")

SAFE = lambda s: re.sub(r"[^A-Za-z0-9_.-]", "", str(s or ""))[:48]

def strict_mux_name(value):
    raw = str(value or "").strip()
    return raw if raw and SAFE(raw) == raw else ""

REMOTE_CREATE_FIELDS = {"t", "s", "rid", "cols", "rows", "relaunch", "heal"}

def remote_create_violation(frame):
    if not isinstance(frame, dict):
        return "create frame must be an object"
    extra = set(frame) - REMOTE_CREATE_FIELDS
    if extra:
        return "create frame contains forbidden fields"
    if frame.get("t") != "create":
        return "not a create frame"
    if not strict_mux_name(frame.get("s", "")):
        return "invalid mux session name"
    rid = str(frame.get("rid", "") or "")
    if not re.fullmatch(r"[A-Za-z0-9._-]{1,128}", rid):
        return "invalid create request id"
    for key, low, high in (("cols", 20, 500), ("rows", 8, 200)):
        if key not in frame:
            continue
        value = frame.get(key)
        if isinstance(value, bool):
            return f"invalid {key}"
        try:
            value = int(value)
        except (TypeError, ValueError):
            return f"invalid {key}"
        if value < low or value > high:
            return f"invalid {key}"
    for key in ("relaunch", "heal"):
        if key in frame and not isinstance(frame.get(key), bool):
            return f"invalid {key}"
    return ""

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
            "ready": alive, "kind": kind,
            "sessionId": getattr(sess, "session_id", "") or "",
            "aliases": list(getattr(sess, "aliases", []) or []),
            "identityPending": bool(getattr(sess, "identity_pending", False)),
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

async def new_session_off_loop(name, cmd, cwd, cols, rows, loop, outq, heal=False, ids=None,
                               session_id="", aliases=None):
    s = Session(
        name, cmd, cwd, cols, rows, loop, outq, heal=heal, spawn_now=False,
        ids=ids, session_id=session_id, aliases=aliases
    )
    await spawn_session_off_loop(s)
    return s


def release_session_claim(s):
    claim = getattr(s, "_launch_claim", None)
    if claim is not None:
        claim.release()
    s._launch_claim = None
    s.claim_paths = []


async def terminate_session_off_loop(s, by_user=True, timeout=12, release_claim_on_success=True):
    """Stop a session and do not return success until its PTY process is confirmed gone."""
    if s is None:
        return True, "already stopped"
    s.user_killed = by_user
    if bool(getattr(s, "owner", False)):
        try:
            s.kill(by_user=by_user)
        except Exception as e:
            return False, f"owner stop request failed: {e}"
        deadline = time.monotonic() + max(0.1, timeout)
        while not bool(getattr(s, "owner_exit_confirmed", False)):
            if time.monotonic() >= deadline:
                return False, "visible local owner did not confirm child-process exit"
            await asyncio.sleep(0.05)
        s.dead = True
        if release_claim_on_success:
            release_session_claim(s)
        return True, "owner exited"

    pty = getattr(s, "pty", None)
    pid = int(getattr(pty, "pid", 0) or 0) if pty is not None else 0
    if pty is None:
        if release_claim_on_success:
            release_session_claim(s)
        return True, "already stopped"
    s.dead = True

    def terminate_and_verify():
        terminate_error = None
        try:
            pty.terminate(force=True)
        except Exception as e:
            terminate_error = e
        if pid > 0 and _pid_alive(pid):
            try:
                subprocess.run(
                    ["taskkill.exe", "/PID", str(pid), "/T", "/F"],
                    timeout=max(2, int(timeout)),
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                )
            except Exception:
                pass
        deadline = time.monotonic() + max(0.1, timeout)
        while pid > 0 and _pid_alive(pid) and time.monotonic() < deadline:
            time.sleep(0.05)
        if pid > 0 and _pid_alive(pid):
            return False, f"process {pid} is still alive after termination"
        if terminate_error is not None and pid <= 0:
            return False, f"PTY termination failed: {terminate_error}"
        return True, "process exited"

    try:
        result = await asyncio.wait_for(
            asyncio.get_running_loop().run_in_executor(None, terminate_and_verify),
            timeout=max(1, timeout + 2),
        )
        if result[0]:
            if s.pty is pty:
                s.pty = None
            try:
                s.wq.put_nowait(None)
            except Exception:
                pass
            if release_claim_on_success:
                release_session_claim(s)
        elif s.pty is pty:
            s.dead = False
        return result
    except asyncio.TimeoutError:
        if s.pty is pty:
            s.dead = False
        return False, "process termination verification timed out"


async def main():
    start_watchdog_thread()
    threading.Thread(target=_guardian_keepalive, name="guardian-keepalive", daemon=True).start()
    loop = asyncio.get_running_loop()
    outq = RelayOutQueue()
    launch_locks = {}

    def launch_lock(name):
        lock = launch_locks.get(name)
        if lock is None:
            lock = asyncio.Lock()
            launch_locks[name] = lock
        return lock

    async def remove_session(name, by_user):
        async with launch_lock(name):
            current = sessions.get(name)
            if current is None:
                return False, "no such session: " + name
            candidate = dict(sessions)
            candidate.pop(name, None)
            try:
                manifest_save(candidate)
            except Exception as e:
                return False, "could not durably record session stop: " + str(e)
            ok, detail = await terminate_session_off_loop(current, by_user=by_user)
            if not ok:
                current.user_killed = False
                try:
                    manifest_save(sessions)
                except Exception as restore_error:
                    return False, detail + "; manifest rollback also failed: " + str(restore_error)
                return False, detail
            if sessions.get(name) is current:
                del sessions[name]
            return True, detail

    async def reserve_launch_claim(name, cmd, candidate_ids, prev=None, ignored_live_override=None):
        if not cmd:
            return None, False, ""
        existing_claim = getattr(prev, "_launch_claim", None) if prev else None
        if existing_claim is not None and set(existing_claim.ids) == set(candidate_ids):
            return existing_claim, True, ""

        live = await asyncio.get_running_loop().run_in_executor(None, try_live_session_ids)
        ignored_live = dict(ignored_live_override or {})
        if prev and prev.alive() and ignored_live_override is None:
            owned_ok, ignored_live, owned_detail = await asyncio.get_running_loop().run_in_executor(
                None, lambda: session_owned_live_ids(prev, live)
            )
            if not owned_ok:
                return None, False, owned_detail or "could not verify current mux session ownership"
        claim, detail = await asyncio.get_running_loop().run_in_executor(
            None,
            lambda: acquire_launch_claim(
                cmd,
                candidate_ids,
                f"muxd session {name}",
                live=live,
                ignored_live=ignored_live,
            ),
        )
        return claim, False, detail

    async def coordinate_owner_registration(first, ws):
        name = strict_mux_name(first.get("s", ""))
        if not name:
            return None, "session name required"
        owner_key = str(first.get("ownerKey", "") or "")
        if len(owner_key) < 24:
            return None, "visible owner registration requires a reconnect key"

        async with launch_lock(name):
            prev = sessions.get(name)
            cmd = (first.get("cmd", "") or "").strip() or (prev.cmd if prev else "")
            ids = first.get("ids") if isinstance(first.get("ids"), list) else []
            requested_aliases = first.get("aliases") if isinstance(first.get("aliases"), list) else []
            requested_session_id = _safe_identity(first.get("sessionId", ""))
            canonical_id, identity_aliases, candidate_ids = resolve_session_identity(
                cmd,
                requested_session_id or (getattr(prev, "session_id", "") if prev else ""),
                requested_aliases or (getattr(prev, "aliases", ()) if prev else ()),
                ids or (prev.ids if prev else None),
            )
            live_owner_reconnect = bool(
                prev
                and getattr(prev, "owner", False)
                and not prev.alive()
                and getattr(prev, "owner_key", "") == owner_key
            )
            restored_owner_reconnect = bool(
                prev
                and getattr(prev, "expected_owner", False)
                and not getattr(prev, "owner", False)
                and not prev.alive()
                and getattr(prev, "owner_key", "") == owner_key
            )
            reconnect = live_owner_reconnect or restored_owner_reconnect
            if prev and prev.alive():
                return None, "session already has a visible local owner: " + name
            if prev and (getattr(prev, "owner", False) or getattr(prev, "expected_owner", False)) and not reconnect:
                return None, "visible owner reconnect key did not match: " + name
            if reconnect and (
                normalized_cmd(getattr(prev, "cmd", "")) != normalized_cmd(cmd)
                or set(getattr(prev, "ids", []) or []) != set(candidate_ids)
            ):
                return None, "visible owner reconnect identity changed: " + name

            ignored_live_override = None
            if restored_owner_reconnect and candidate_ids:
                live = await asyncio.get_running_loop().run_in_executor(None, try_live_session_ids)
                owned_ok, ignored_live_override, owned_detail = await asyncio.get_running_loop().run_in_executor(
                    None,
                    lambda: owner_reconnect_ignored_live(candidate_ids, first.get("childPid", 0), live),
                )
                if not owned_ok:
                    return None, owned_detail or "could not verify reconnecting visible owner"

            claim, preserve_existing_claim, claim_detail = await reserve_launch_claim(
                name, cmd, candidate_ids, prev, ignored_live_override=ignored_live_override
            )
            existing_claim = getattr(prev, "_launch_claim", None) if prev else None
            if candidate_ids and claim is None:
                return None, claim_detail or "could not reserve visible owner"

            tombstoned = False
            if prev and not reconnect:
                candidate = dict(sessions)
                candidate.pop(name, None)
                try:
                    manifest_save(candidate)
                    tombstoned = True
                except Exception as e:
                    if claim is not existing_claim and claim is not None:
                        claim.release()
                    return None, "could not durably reserve owner replacement: " + str(e)
                ok, detail = await terminate_session_off_loop(
                    prev,
                    by_user=False,
                    release_claim_on_success=not preserve_existing_claim,
                )
                if not ok:
                    if claim is not getattr(prev, "_launch_claim", None) and claim is not None:
                        claim.release()
                    return None, "previous session did not exit: " + detail
            if prev and preserve_existing_claim:
                prev._launch_claim = None
                prev.claim_paths = []

            owner = OwnerSession(
                name,
                cmd,
                first.get("cwd", "") or (prev.cwd if prev else ""),
                int(first.get("cols") or 140),
                int(first.get("rows") or 40),
                loop,
                outq,
                ws,
                heal=bool(first.get("heal")) if "heal" in first else bool(prev.heal if prev else False),
                ids=candidate_ids,
                session_id=canonical_id,
                aliases=identity_aliases,
                owner_key=owner_key,
            )
            owner._launch_claim = claim
            owner.claim_paths = list(claim.paths) if claim is not None else []
            candidate = dict(sessions)
            candidate[name] = owner
            try:
                manifest_save(candidate)
            except Exception as e:
                if claim is not existing_claim and claim is not None:
                    claim.release()
                if preserve_existing_claim and prev is not None:
                    prev._launch_claim = existing_claim
                    prev.claim_paths = list(existing_claim.paths) if existing_claim is not None else []
                if tombstoned and sessions.get(name) is prev:
                    del sessions[name]
                return None, "could not durably register visible owner: " + str(e)
            sessions[name] = owner
            return owner, ""

    async def coordinate_session_request(first, spawn_if_missing, leave_unarmed_dormant=False):
        name = SAFE(first.get("s", ""))
        if not name:
            return None, "session name required", False
        async with launch_lock(name):
            prev = sessions.get(name)
            requested_cmd = (first.get("cmd", "") or "").strip()
            requested_ids = first.get("ids") if isinstance(first.get("ids"), list) else []
            requested_aliases = first.get("aliases") if isinstance(first.get("aliases"), list) else []
            requested_session_id = _safe_identity(first.get("sessionId", ""))
            requested_relaunch = bool(first.get("relaunch"))
            requested_cwd = first.get("cwd", "") or ""
            requested_heal = bool(first.get("heal")) if ("heal" in first) else bool(prev.heal if prev else False)
            requested_identity_pending = (
                bool(first.get("identityPending"))
                if "identityPending" in first
                else bool(getattr(prev, "identity_pending", False) if prev else False)
            )
            cols = int(first.get("cols") or (prev.cols if prev else 140))
            rows = int(first.get("rows") or (prev.rows if prev else 40))
            cmd = requested_cmd or (prev.cmd if prev else "")
            cwd = requested_cwd or (prev.cwd if prev else "")
            canonical_id, identity_aliases, candidate_ids = resolve_session_identity(
                cmd,
                requested_session_id or (getattr(prev, "session_id", "") if prev else ""),
                requested_aliases or (getattr(prev, "aliases", ()) if prev else ()),
                requested_ids or (prev.ids if prev else None),
            )
            if canonical_id:
                requested_identity_pending = False
            if requested_identity_pending and requested_heal:
                return None, "cannot arm auto-resume until the fresh session identity is captured", False

            if prev and prev.alive() and not requested_relaunch and not needs_relaunch_for_command(prev, requested_cmd):
                claim, _, claim_detail = await reserve_launch_claim(name, cmd, candidate_ids, prev)
                if candidate_ids and claim is None:
                    return None, claim_detail or "could not reserve running session ownership", False
                snapshot = persisted_session_snapshot(prev)
                old_claim = snapshot["_launch_claim"]
                prev.heal = requested_heal
                if requested_cmd and requested_cmd != prev.cmd:
                    prev.cmd = requested_cmd
                prev.ids = candidate_ids
                prev.session_id = canonical_id
                prev.aliases = identity_aliases
                prev.identity_pending = requested_identity_pending
                if claim is not None:
                    prev._launch_claim = claim
                    prev.claim_paths = list(claim.paths)
                if requested_cwd:
                    prev.cwd = requested_cwd
                if first.get("cols"):
                    prev.cols = cols
                    prev.rows = rows
                try:
                    manifest_save()
                except Exception as e:
                    restore_persisted_session(prev, snapshot)
                    if claim is not None and claim is not old_claim:
                        claim.release()
                    return None, "could not durably update session: " + str(e), False
                if old_claim is not None and old_claim is not claim:
                    old_claim.release()
                if first.get("cols"):
                    prev.resize(cols, rows)
                return prev, "", False
            if prev and not spawn_if_missing:
                return prev, "", False
            if not prev and not spawn_if_missing:
                return None, "no such session: " + name, False
            if (
                leave_unarmed_dormant
                and prev
                and prev.dead
                and not prev.heal
                and not requested_heal
                and not requested_cmd
                and not requested_relaunch
            ):
                if first.get("cols"):
                    prev.cols = cols
                if first.get("rows"):
                    prev.rows = rows
                log(f"[{name}] attach to dormant unarmed session - left stopped")
                return prev, "", False

            if requested_relaunch and not cmd:
                return None, "no saved command for " + name, False
            if requested_relaunch and prev and getattr(prev, "identity_pending", False):
                return None, "fresh session identity has not been captured; refusing to start a different chat", False
            if prev and prev.alive() and bool(getattr(prev, "owner", False)):
                return None, "session has a visible local owner; close it explicitly before relaunch", False

            claim, preserve_existing_claim, claim_detail = await reserve_launch_claim(
                name, cmd, candidate_ids, prev
            )
            existing_claim = getattr(prev, "_launch_claim", None) if prev else None
            prior_deaths = list(getattr(prev, "deaths", []) or []) if prev else []
            if candidate_ids and claim is None:
                return None, claim_detail or "could not reserve session launch", False

            if prev:
                candidate = dict(sessions)
                candidate.pop(name, None)
                try:
                    manifest_save(candidate)
                except Exception as e:
                    if claim is not existing_claim and claim is not None:
                        claim.release()
                    return None, "could not durably reserve session replacement: " + str(e), False
                ok, detail = await terminate_session_off_loop(
                    prev,
                    by_user=False,
                    release_claim_on_success=not preserve_existing_claim,
                )
                if not ok:
                    prev.user_killed = False
                    try:
                        manifest_save(sessions)
                    except Exception as restore_error:
                        detail += "; manifest rollback also failed: " + str(restore_error)
                    if claim is not existing_claim and claim is not None:
                        claim.release()
                    return None, "previous session did not exit: " + detail, False
                if preserve_existing_claim:
                    prev._launch_claim = None
                    prev.claim_paths = []
                if sessions.get(name) is prev:
                    del sessions[name]
            try:
                created = await new_session_off_loop(
                    name, cmd, cwd, cols, rows, loop, outq, heal=requested_heal,
                    ids=candidate_ids, session_id=canonical_id, aliases=identity_aliases
                )
            except Exception as e:
                if claim is not None:
                    claim.release()
                return None, "spawn failed: " + str(e), False
            created._launch_claim = claim
            created.claim_paths = list(claim.paths) if claim is not None else []
            created.identity_pending = requested_identity_pending
            created.deaths = prior_deaths
            candidate = dict(sessions)
            candidate[name] = created
            try:
                manifest_save(candidate)
            except Exception as e:
                stopped, stop_detail = await terminate_session_off_loop(created, by_user=False)
                if not stopped:
                    sessions[name] = created
                    return None, "manifest commit failed and spawned session could not be stopped: " + stop_detail, False
                return None, "could not durably record spawned session: " + str(e), False
            sessions[name] = created
            return created, "", True

    async def bind_session_identity(first):
        name = strict_mux_name(first.get("s", ""))
        if not name:
            return None, "session name required"
        cmd = (first.get("cmd", "") or "").strip()
        command_id = _safe_identity(_parse_resume_id(cmd))
        if not cmd or not command_id:
            return None, "identity binding requires a trusted resume command"
        requested_session_id = _safe_identity(first.get("sessionId", ""))
        aliases = first.get("aliases") if isinstance(first.get("aliases"), list) else []
        canonical_id, identity_aliases, candidate_ids = resolve_session_identity(
            cmd,
            requested_session_id,
            aliases,
            first.get("ids") if isinstance(first.get("ids"), list) else [],
        )
        if not canonical_id or not candidate_ids:
            return None, "identity binding did not contain an opaque session id"

        async with launch_lock(name):
            current = sessions.get(name)
            if current is None or not current.alive():
                return None, "session is not live: " + name
            if not getattr(current, "identity_pending", False):
                current_ids = {str(value).lower() for value in ([getattr(current, "session_id", "")] + list(getattr(current, "aliases", []) or [])) if value}
                if canonical_id.lower() in current_ids:
                    return current, ""
                return None, "session already has a different canonical identity"

            pty = getattr(current, "pty", None)
            root_pid = int(getattr(pty, "pid", 0) or 0) if pty is not None else 0
            live = await asyncio.get_running_loop().run_in_executor(None, try_live_session_ids)
            owned_ok, ignored_live, owned_detail = await asyncio.get_running_loop().run_in_executor(
                None,
                lambda: owner_reconnect_ignored_live(candidate_ids, root_pid, live),
            )
            if not owned_ok:
                return None, owned_detail or "could not verify pending session ownership"
            claim, _, claim_detail = await reserve_launch_claim(
                name,
                cmd,
                candidate_ids,
                current,
                ignored_live_override=ignored_live,
            )
            if claim is None:
                return None, claim_detail or "could not reserve captured session identity"

            snapshot = persisted_session_snapshot(current)
            old_claim = snapshot["_launch_claim"]
            current.cmd = cmd
            current.session_id = canonical_id
            current.aliases = identity_aliases
            current.ids = candidate_ids
            current._launch_claim = claim
            current.claim_paths = list(claim.paths)
            current.identity_pending = False
            try:
                manifest_save()
            except Exception as e:
                restore_persisted_session(current, snapshot)
                if claim is not old_claim:
                    claim.release()
                return None, "could not durably bind session identity: " + str(e)
            if old_claim is not None and old_claim is not claim:
                old_claim.release()
            return current, ""

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
    # liveness runs a PowerShell CIM query (blocking) — NEVER call it on the event-loop thread or muxd
    # freezes and hosted sessions drop. Always hop to a worker thread.
    for name, m in manifest_load().items():
        if strict_mux_name(name) and name not in sessions:
            try:
                heal = bool(m.get("heal"))
                mcmd = m.get("cmd", "")
                ids = m.get("ids") if isinstance(m.get("ids"), list) else []
                aliases = m.get("aliases") if isinstance(m.get("aliases"), list) else []
                restored = Session(
                    name, mcmd, m.get("cwd", ""), m.get("cols", 140), m.get("rows", 40),
                    loop, outq, heal=heal, spawn_now=False, ids=ids,
                    session_id=m.get("sessionId", ""), aliases=aliases
                )
                restored.expected_owner = bool(m.get("owner"))
                restored.owner_key = str(m.get("ownerKey", "") or "")
                restored.identity_pending = bool(m.get("identityPending"))
                restored.deaths = [
                    float(value)
                    for value in (m.get("deaths") if isinstance(m.get("deaths"), list) else [])
                    if isinstance(value, (int, float))
                ][-16:]
                sessions[name] = restored
                if restored.expected_owner:
                    log(f"[boot] waiting for visible owner reconnect: {name}")
                    continue
                if restored.identity_pending:
                    log(f"[boot] fresh identity was not captured for {name}; left dormant")
                    continue
                if heal:
                    _session, detail, created = await coordinate_session_request(
                        {"t": "create", "s": name, "relaunch": True, "heal": True, "ids": ids},
                        True,
                    )
                    if detail:
                        log(f"[boot] REFUSED auto-resume of {name}: {detail}; left dormant")
                    elif created:
                        log(f"[boot] recreated + resumed (armed): {name}")
                    continue
                log(f"[boot] listed as dormant (unarmed - explicit relaunch required): {name}")
            except Exception as e: log(f"[boot] {name} failed: {e}")

    async def self_heal_tick():
        # a session whose SHELL died (pty EOF) is useless — recreate + re-run its resume (max 3/10min).
        while True:
            await asyncio.sleep(15)
            for s in list(sessions.values()):
                if (
                    s.dead
                    and not s.user_killed
                    and s.cmd
                    and s.heal
                    and not getattr(s, "expected_owner", False)
                    and not getattr(s, "identity_pending", False)
                ):   # opt-in only: unarmed and externally-owned sessions stay down
                    now = time.time()
                    s.deaths = [t for t in s.deaths if now - t < 600]
                    if len(s.deaths) >= 3: continue
                    s.deaths.append(now)
                    _session, detail, created = await coordinate_session_request(
                        {"t": "create", "s": s.name, "relaunch": True, "heal": True, "ids": s.ids},
                        True,
                    )
                    if detail:
                        log(f"[heal] SKIP respawn of {s.name}: {detail}")
                    elif created:
                        log(f"[heal] {s.name} shell died -> respawned + resume queued")
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
            return await coordinate_session_request(first, spawn_if_missing)

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
                    ok, detail = await remove_session(name, by_user=True)
                    if not ok:
                        await ws.send(json.dumps({"t": "err", "m": detail})); return
                    await ws.send(json.dumps({"t": "killed", "s": name})); return
                if first.get("t") == "owner":
                    name = SAFE(first.get("s", ""))
                    owner, detail = await coordinate_owner_registration(first, ws)
                    if owner is None:
                        await ws.send(json.dumps({"t": "err", "m": detail})); return
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
                            elif mt == "inputResult":
                                waiter = owner.input_waiters.get(str(m.get("rid", "")))
                                if waiter is not None and not waiter.done():
                                    waiter.set_result((
                                        bool(m.get("ok")),
                                        "" if m.get("ok") else "visible terminal rejected input",
                                    ))
                            elif mt == "dead":
                                owner.owner_exit_confirmed = True
                                owner.dead = True
                                break
                    finally:
                        for waiter in list(owner.input_waiters.values()):
                            if not waiter.done():
                                waiter.set_result((False, "visible owner disconnected during input"))
                        if sessions.get(name) is owner:
                            owner.dead = True
                            outq.put_nowait(("dead", name, ""))
                    return
                if first.get("t") == "bind":
                    s, detail = await bind_session_identity(first)
                    if s is None:
                        await ws.send(json.dumps({"t": "err", "m": detail})); return
                    outq.put_nowait(("dead", s.name, ""))  # force relay session-list refresh
                    await ws.send(json.dumps({"t": "bind-ok", "s": s.name, "sessionId": s.session_id})); return
                if first.get("t") == "create":
                    s, err, created = await ensure_local_session(first, True)
                    if err:
                        await ws.send(json.dumps({"t": "err", "m": err})); return
                    await ws.send(json.dumps({"t": "created", "s": s.name, "created": created, "alive": s.alive()})); return
                if first.get("t") == "input":
                    name = SAFE(first.get("s", ""))
                    s = sessions.get(name)
                    if s is None or not s.alive():
                        await ws.send(json.dumps({"t": "err", "m": "session is not live: " + name})); return
                    try:
                        data = base64.b64decode(first.get("d", ""), validate=True)
                    except Exception:
                        await ws.send(json.dumps({"t": "err", "m": "invalid input payload"})); return
                    if not data:
                        await ws.send(json.dumps({"t": "err", "m": "input payload is empty"})); return
                    if isinstance(s, OwnerSession):
                        ok, detail = await s.write_confirmed(data)
                    else:
                        ok, detail = await asyncio.get_running_loop().run_in_executor(
                            None, lambda: s.write_confirmed(data)
                        )
                    if not ok:
                        await ws.send(json.dumps({"t": "err", "m": detail})); return
                    await ws.send(json.dumps({"t": "input-ok", "s": name})); return
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
        # Auto-rebind loop: if the local attach server ever fails to bind or its task exits (which used to
        # leave `mux` locally dead until a full muxd restart), keep retrying so it self-heals.
        while True:
            try:
                async with _ws.serve(handler, "127.0.0.1", LOCAL_PORT, ping_interval=20,
                                     ping_timeout=10, close_timeout=2, max_queue=32):
                    log(f"local attach server listening on 127.0.0.1:{LOCAL_PORT}")
                    await asyncio.Future()
            except Exception as e:
                log(f"local attach server failed on :{LOCAL_PORT}: {e}; retrying in 3s")
                await asyncio.sleep(3)
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
                    stale_frames = 0
                    while True:
                        try:
                            outq.get_nowait()
                            stale_frames += 1
                        except asyncio.QueueEmpty:
                            break
                    if stale_frames or outq.dropped:
                        log(
                            f"relay backlog reset on reconnect: stale={stale_frames} "
                            f"dropped={outq.dropped}; session rings remain available for scrollback"
                        )
                        outq.dropped = 0
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
                            try: await asyncio.get_running_loop().run_in_executor(None, write_live_tabs)   # off-loop file write
                            except Exception: pass
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
                            t = m.get("t")
                            name = strict_mux_name(m.get("s", ""))
                            if t == "create":
                                violation = remote_create_violation(m)
                                if violation:
                                    log(f"[relay] rejected create protocol frame: {violation}")
                                    await ws.close(code=1008, reason="create frame violated protocol")
                                    break
                                request_id = str(m.get("rid", "") or "")
                                session, err, created = await coordinate_session_request(
                                    m, True, leave_unarmed_dormant=True
                                )
                                if err:
                                    log(f"[{name}] create REFUSED: {err}")
                                    await ws.send(json.dumps({
                                        "t": "createResult",
                                        "rid": request_id,
                                        "s": name,
                                        "ok": False,
                                        "created": False,
                                        "detail": "muxd refused the create request",
                                    }))
                                else:
                                    await ws.send(json.dumps({
                                        "t": "createResult",
                                        "rid": request_id,
                                        "s": name,
                                        "ok": True,
                                        "created": bool(created),
                                        "detail": "",
                                        "session": session_payload(name, session),
                                    }))
                                continue
                            elif t == "heal" and name in sessions:
                                if bool(m.get("on")) and getattr(sessions[name], "identity_pending", False):
                                    log(f"[{name}] refused auto-resume while fresh identity is pending")
                                else:
                                    session = sessions[name]
                                    previous = session.heal
                                    session.heal = bool(m.get("on"))
                                    try:
                                        manifest_save()
                                    except Exception as e:
                                        session.heal = previous
                                        log(f"[{name}] heal persistence failed: {e}")
                                        await ws.send(json.dumps({
                                            "t": "sessions",
                                            "list": sess_list(),
                                            "notice": "auto-resume policy was not persisted",
                                        }))
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
                                to = strict_mux_name(m.get("to", ""))
                                if to and to not in sessions:
                                    s = sessions[name]
                                    candidate = dict(sessions)
                                    candidate.pop(name)
                                    candidate[to] = s
                                    try:
                                        manifest_save(candidate)
                                    except Exception as e:
                                        log(f"[{name}] rename persistence failed: {e}")
                                        await ws.send(json.dumps({
                                            "t": "sessions",
                                            "list": sess_list(),
                                            "notice": "session rename was not persisted",
                                        }))
                                        continue
                                    sessions.pop(name)
                                    s.name = to
                                    sessions[to] = s
                                    await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))
                            elif t == "tail" and name in sessions:
                                await ws.send(json.dumps({"t": "tailr", "rid": m.get("rid", ""),
                                                          "text": sessions[name].tail_text(nbytes=200000, lines=int(m.get("lines") or 40))}))
                            elif t == "kill" and name in sessions:
                                ok, detail = await remove_session(name, by_user=True)
                                if not ok:
                                    log(f"[{name}] remote stop failed: {detail}")
                                    await ws.send(json.dumps({"t": "sessions", "list": sess_list(), "notice": "session stop failed"}))
                                    continue
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
    ok, detail = acquire_single_instance()
    if not ok:
        _log_now("FATAL: refusing to start duplicate muxd: " + detail)
        sys.exit(0)
    if not TOKEN or not RELAYS:
        log("FATAL: muxd.env needs MUX_HOST_TOKEN and RELAY_LAN/RELAY_PUBLIC"); sys.exit(1)
    while True:                      # top-level crash guard: muxd must never die quietly
        try: asyncio.run(main())
        except KeyboardInterrupt: sys.exit(0)
        except Exception:
            log("CRASH:\n" + traceback.format_exc()); time.sleep(3)
