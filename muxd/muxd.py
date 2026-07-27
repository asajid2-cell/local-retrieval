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
import asyncio, base64, collections, ctypes, gc, glob, hashlib, json, os, queue, re, socket, ssl, subprocess, sys, tempfile, threading, time, traceback
import concurrent.futures
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

def _durable_checkpoint(fault, stage, path):
    if fault is not None:
        fault(stage, path)
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
        and (not requested.get("file") or requested.get("file") == os.path.basename(path))
    ):
        remaining = int(requested.get("after", 0) or 0)
        if remaining > 0:
            requested["after"] = remaining - 1
            try:
                with open(fault_file, "w", encoding="utf-8") as stream:
                    json.dump(requested, stream)
            except OSError:
                pass
            return
        try:
            os.remove(fault_file)
        except OSError:
            pass
        delay_ms = max(0, int(requested.get("delayMs", 0) or 0))
        if delay_ms:
            time.sleep(delay_ms / 1000)
        if requested.get("fail", True):
            raise OSError(f"injected persistence failure at {stage} for {os.path.basename(path)}")
        return

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
    replaced = False
    try:
        _durable_checkpoint(fault, "before_write", path)
        with open(tmp, "xb", buffering=0) as stream:
            stream.write(payload)
            _durable_checkpoint(fault, "before_file_fsync", path)
            os.fsync(stream.fileno())
        _durable_checkpoint(fault, "before_replace", path)
        _replace_write_through(tmp, path)
        replaced = True
        _durable_checkpoint(fault, "before_directory_fsync", path)
        _fsync_directory(directory)
        _durable_checkpoint(fault, "before_readback", path)
        with open(path, "rb") as stream:
            committed = stream.read()
        if committed != payload:
            raise OSError("committed state did not read back identically: " + path)
    except Exception as exc:
        exc.target = path
        exc.replaced = replaced
        exc.committed = False
        exc.mismatch = False
        exc.unknown = False
        if replaced:
            try:
                with open(path, "rb") as stream:
                    actual = stream.read()
                exc.committed = actual == payload
                exc.mismatch = not exc.committed
            except OSError as verification_error:
                exc.unknown = True
                exc.verification_error = verification_error
        raise exc
    finally:
        try:
            os.remove(tmp)
        except FileNotFoundError:
            pass

def _compare_durable_bytes(path, expected):
    try:
        with open(path, "rb") as stream:
            return "match" if stream.read() == expected else "mismatch"
    except OSError:
        return "unknown"

def durable_json_write(path, value, fault=None, backup_fault=None, retry_fault=None):
    payload = json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    if os.path.exists(path):
        with open(path, "rb") as stream:
            previous = stream.read()
        try:
            _durable_commit_bytes(path + ".bak", previous, fault=backup_fault)
        except Exception as exc:
            exc.primary_committed = False
            exc.committed = False
            raise
    try:
        _durable_commit_bytes(path, payload, fault=fault)
    except Exception as exc:
        if getattr(exc, "committed", False):
            try:
                _durable_commit_bytes(path, payload, fault=retry_fault)
                return
            except Exception as retry_error:
                retry_error.first_error = exc
                comparison = _compare_durable_bytes(path, payload)
                retry_error.committed = comparison == "match"
                retry_error.mismatch = comparison == "mismatch"
                retry_error.unknown = comparison == "unknown"
                exc = retry_error
        if getattr(exc, "unknown", False):
            try:
                with open(path, "rb") as stream:
                    actual = stream.read()
                if actual == payload:
                    _durable_commit_bytes(path, payload)
                    return
                exc.unknown = False
                exc.mismatch = True
            except OSError as verification_error:
                exc.verification_error = verification_error
        backup = path + ".bak"
        if getattr(exc, "replaced", False) and getattr(exc, "mismatch", False) and os.path.exists(backup):
            try:
                with open(backup, "rb") as stream:
                    _durable_commit_bytes(path, stream.read())
                exc.recovered = True
                exc.committed = False
            except Exception as recovery_error:
                exc.recovery_error = recovery_error
        raise exc

def _decode_persisted_json(path, payload):
    try:
        return json.loads(payload.decode("utf-8"))
    except Exception as exc:
        raise OSError("invalid persisted JSON: " + path) from exc

def _validated_persisted_json(path, payload, validate):
    value = _decode_persisted_json(path, payload)
    if validate is not None and not validate(value):
        raise OSError("invalid persisted state shape: " + path)
    return value

def durable_json_load(path, fallback, validate=None):
    if os.path.exists(path):
        try:
            with open(path, "rb") as stream:
                return _validated_persisted_json(path, stream.read(), validate)
        except OSError as primary_error:
            backup = path + ".bak"
            if not os.path.exists(backup):
                raise primary_error
            with open(backup, "rb") as stream:
                backup_payload = stream.read()
            restored = _validated_persisted_json(backup, backup_payload, validate)
            _durable_commit_bytes(path, backup_payload)
            return restored
    backup = path + ".bak"
    if os.path.exists(backup):
        with open(backup, "rb") as stream:
            backup_payload = stream.read()
        restored = _validated_persisted_json(backup, backup_payload, validate)
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

def _process_start_token(pid):
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return ""
    if pid <= 0 or os.name != "nt":
        return ""
    try:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        open_process = kernel32.OpenProcess
        open_process.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        open_process.restype = wintypes.HANDLE
        get_process_times = kernel32.GetProcessTimes
        get_process_times.argtypes = [
            wintypes.HANDLE,
            ctypes.POINTER(wintypes.FILETIME),
            ctypes.POINTER(wintypes.FILETIME),
            ctypes.POINTER(wintypes.FILETIME),
            ctypes.POINTER(wintypes.FILETIME),
        ]
        get_process_times.restype = wintypes.BOOL
        close_handle = kernel32.CloseHandle
        close_handle.argtypes = [wintypes.HANDLE]
        close_handle.restype = wintypes.BOOL
        handle = open_process(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return ""
        try:
            created = wintypes.FILETIME()
            exited = wintypes.FILETIME()
            kernel = wintypes.FILETIME()
            user = wintypes.FILETIME()
            if not get_process_times(
                handle,
                ctypes.byref(created),
                ctypes.byref(exited),
                ctypes.byref(kernel),
                ctypes.byref(user),
            ):
                return ""
            value = (int(created.dwHighDateTime) << 32) | int(created.dwLowDateTime)
            return f"{value:016x}"
        finally:
            close_handle(handle)
    except Exception:
        return ""

def _same_process_instance(pid, expected_start_token):
    expected = str(expected_start_token or "")
    return bool(expected) and _pid_alive(pid) and _process_start_token(pid) == expected

def _terminate_process_instance(pid, expected_start_token, timeout=3):
    """Terminate exactly one Windows process instance, fenced by its creation time."""
    if os.name != "nt":
        return False, "process-instance termination is Windows-specific"
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return True, "no process recorded"
    expected = str(expected_start_token or "")
    if pid <= 0 or not expected:
        return True, "no process instance recorded"
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    open_process = kernel32.OpenProcess
    open_process.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    open_process.restype = wintypes.HANDLE
    get_process_times = kernel32.GetProcessTimes
    get_process_times.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
    ]
    get_process_times.restype = wintypes.BOOL
    terminate_process = kernel32.TerminateProcess
    terminate_process.argtypes = [wintypes.HANDLE, wintypes.UINT]
    terminate_process.restype = wintypes.BOOL
    wait_for_single = kernel32.WaitForSingleObject
    wait_for_single.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    wait_for_single.restype = wintypes.DWORD
    close_handle = kernel32.CloseHandle
    close_handle.argtypes = [wintypes.HANDLE]
    close_handle.restype = wintypes.BOOL
    rights = 0x0001 | 0x1000 | 0x00100000  # TERMINATE | QUERY_LIMITED_INFORMATION | SYNCHRONIZE
    handle = open_process(rights, False, pid)
    if not handle:
        error = ctypes.get_last_error()
        if error == 87:  # ERROR_INVALID_PARAMETER: PID no longer exists
            return True, "process already exited"
        return False, f"could not open process {pid} for fenced termination (winerror={error})"
    try:
        created = wintypes.FILETIME()
        exited = wintypes.FILETIME()
        kernel = wintypes.FILETIME()
        user = wintypes.FILETIME()
        if not get_process_times(
            handle,
            ctypes.byref(created),
            ctypes.byref(exited),
            ctypes.byref(kernel),
            ctypes.byref(user),
        ):
            return False, f"could not verify process {pid} creation time (winerror={ctypes.get_last_error()})"
        actual_value = (int(created.dwHighDateTime) << 32) | int(created.dwLowDateTime)
        if f"{actual_value:016x}" != expected:
            return True, "PID now refers to a different process instance"
        initial_wait = int(wait_for_single(handle, 0))
        if initial_wait == 0:  # WAIT_OBJECT_0
            return True, "process already exited"
        if initial_wait == 0xFFFFFFFF:  # WAIT_FAILED
            return False, f"could not query process {pid} state (winerror={ctypes.get_last_error()})"
        if initial_wait != 258:  # WAIT_TIMEOUT
            return False, f"unexpected wait result for process {pid} (result={initial_wait})"
        if not terminate_process(handle, 1):
            if wait_for_single(handle, 0) == 0:
                return True, "process exited"
            return False, f"could not terminate process {pid} (winerror={ctypes.get_last_error()})"
        wait_ms = max(100, min(0xFFFFFFFE, int(max(0.1, timeout) * 1000)))
        wait_result = int(wait_for_single(handle, wait_ms))
        if wait_result == 0:
            return True, "process exited"
        if wait_result == 258:  # WAIT_TIMEOUT
            return False, f"process {pid} is still alive after termination"
        return False, f"could not wait for process {pid} termination (result={wait_result})"
    finally:
        close_handle(handle)

class _PROCESSENTRY32W(ctypes.Structure):
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
        ("szExeFile", wintypes.WCHAR * 260),
    ]

_CONPTY_SPAWN_LOCK = threading.Lock()
_CONPTY_HOST_EXECUTABLES = (
    "conhost.exe",
    "openconsole.exe",
    "winpty-agent.exe",
)
_ORPHANED_CONPTY_LOCK = threading.Lock()
_ORPHANED_CONPTY_HOSTS = {}
_PENDING_CONPTY_BASELINES = {}

def _direct_child_pids(parent_pid, executable_name=""):
    if os.name != "nt":
        return set()
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    create_snapshot = kernel32.CreateToolhelp32Snapshot
    create_snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    create_snapshot.restype = wintypes.HANDLE
    close_handle = kernel32.CloseHandle
    close_handle.argtypes = [wintypes.HANDLE]
    close_handle.restype = wintypes.BOOL
    snapshot = create_snapshot(0x00000002, 0)  # TH32CS_SNAPPROCESS
    invalid_handle = ctypes.c_void_p(-1).value
    if snapshot == invalid_handle:
        raise ctypes.WinError(ctypes.get_last_error())
    entry = _PROCESSENTRY32W()
    entry.dwSize = ctypes.sizeof(entry)
    wanted = str(executable_name or "").lower()
    found = set()
    try:
        first = kernel32.Process32FirstW
        first.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
        first.restype = wintypes.BOOL
        next_entry = kernel32.Process32NextW
        next_entry.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
        next_entry.restype = wintypes.BOOL
        ctypes.set_last_error(0)
        if not first(snapshot, ctypes.byref(entry)):
            error = ctypes.get_last_error()
            if error == 18:  # ERROR_NO_MORE_FILES
                return set()
            raise ctypes.WinError(error)
        while True:
            if int(entry.th32ParentProcessID) == int(parent_pid):
                name = str(entry.szExeFile or "").lower()
                if not wanted or name == wanted:
                    found.add(int(entry.th32ProcessID))
            ctypes.set_last_error(0)
            if not next_entry(snapshot, ctypes.byref(entry)):
                error = ctypes.get_last_error()
                if error not in (0, 18):  # ERROR_NO_MORE_FILES
                    raise ctypes.WinError(error)
                break
        return found
    finally:
        close_handle(snapshot)

def _conpty_host_pids(parent_pid):
    found = set()
    for executable_name in _CONPTY_HOST_EXECUTABLES:
        found.update(_direct_child_pids(parent_pid, executable_name))
    return found

def _conpty_host_process_records(parent_pid):
    records = {}
    for pid in _conpty_host_pids(parent_pid):
        start_token = _process_start_token(pid)
        if not start_token:
            raise OSError(f"could not capture creation token for ConPTY host process {pid}")
        records[pid] = start_token
    return records

def _new_conpty_host_processes(previous_records, timeout=1.0):
    deadline = time.monotonic() + max(0.0, timeout)
    owned = []
    last_error = None
    while True:
        try:
            current = _conpty_host_process_records(os.getpid())
            owned = [
                (pid, start_token)
                for pid, start_token in sorted(current.items())
                if (previous_records or {}).get(pid) != start_token
            ]
            last_error = None
        except OSError as error:
            last_error = error
            owned = []
        if owned or time.monotonic() >= deadline:
            break
        time.sleep(0.01)
    if last_error is not None:
        raise last_error
    return owned

def _terminate_pid_tree(pid, timeout=12):
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return True, "no process recorded"
    if pid <= 0 or not _pid_alive(pid):
        return True, "process already exited"
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
    while _pid_alive(pid) and time.monotonic() < deadline:
        time.sleep(0.05)
    if _pid_alive(pid):
        return False, f"process {pid} is still alive after termination"
    return True, "process exited"

def _release_pty_resources(pty, deadline=None):
    """Close pywinpty transport and release the native pseudoconsole handle."""
    if pty is None:
        return
    cleanup_lock = getattr(pty, "_muxd_cleanup_lock", None)
    if cleanup_lock is None:
        cleanup_lock = threading.Lock()
        try:
            pty._muxd_cleanup_lock = cleanup_lock
        except Exception:
            pass
    with cleanup_lock:
        if getattr(pty, "_muxd_cleanup_done", False):
            return
        native = getattr(pty, "pty", None)
        try:
            if native is not None:
                native.cancel_io()
        except Exception:
            pass
        for attribute in ("fileobj", "_server"):
            resource = getattr(pty, attribute, None)
            if resource is None:
                continue
            try:
                resource.close()
            except Exception:
                pass
        reader = getattr(pty, "_thread", None)
        if reader is not None and reader is not threading.current_thread():
            try:
                join_timeout = 2
                if deadline is not None:
                    join_timeout = max(0.0, min(join_timeout, deadline - time.monotonic()))
                reader.join(timeout=join_timeout)
            except Exception:
                pass
            if reader.is_alive():
                log("[conpty] pywinpty reader did not exit during resource cleanup")
        try:
            pty.fd = -1
            pty.closed = True
            pty._thread = None
            pty.pty = None
            pty._muxd_cleanup_done = True
        except Exception:
            pass
        native = None
        gc.collect()

def _terminate_conhost_records(owned, timeout=3):
    retained = []
    failures = []
    deadline = time.monotonic() + max(0.1, timeout)
    for pid, start_token in owned:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            retained.append((pid, start_token))
            failures.append("ConPTY host cleanup deadline expired")
            continue
        stopped, detail = _terminate_process_instance(
            pid,
            start_token,
            timeout=remaining,
        )
        if not stopped:
            retained.append((pid, start_token))
            failures.append(detail)
    return retained, failures

def _retain_orphaned_conpty_hosts(owned, reason):
    records = list(owned or ())
    if not records:
        return
    with _ORPHANED_CONPTY_LOCK:
        for pid, start_token in records:
            _ORPHANED_CONPTY_HOSTS[(int(pid), str(start_token))] = str(reason or "")
    log(f"[conpty] retained {len(records)} orphan host record(s) for supervised cleanup: {reason}")

def _retain_pending_conpty_baseline(records, reason):
    baseline = tuple(sorted((int(pid), str(token)) for pid, token in (records or {}).items()))
    with _ORPHANED_CONPTY_LOCK:
        _PENDING_CONPTY_BASELINES[baseline] = str(reason or "")
    log(f"[conpty] quarantined new PTY spawns pending orphan discovery: {reason}")

def _reap_orphaned_conpty_hosts(timeout=5):
    with _CONPTY_SPAWN_LOCK:
        with _ORPHANED_CONPTY_LOCK:
            pending_baselines = list(_PENDING_CONPTY_BASELINES.items())
        for baseline_key, reason in pending_baselines:
            try:
                discovered = _new_conpty_host_processes(
                    dict(baseline_key),
                    timeout=min(1.0, max(0.1, timeout)),
                )
            except OSError as error:
                log(f"[conpty] orphan discovery still pending: {error}")
                continue
            with _ORPHANED_CONPTY_LOCK:
                _PENDING_CONPTY_BASELINES.pop(baseline_key, None)
            _retain_orphaned_conpty_hosts(discovered, reason)
        with _ORPHANED_CONPTY_LOCK:
            records = list(_ORPHANED_CONPTY_HOSTS)
    retained, failures = _terminate_conhost_records(records, timeout=timeout)
    retained_set = set(retained)
    with _ORPHANED_CONPTY_LOCK:
        for record in records:
            if record not in retained_set:
                _ORPHANED_CONPTY_HOSTS.pop(record, None)
    if failures:
        log(f"[conpty] supervised orphan cleanup still pending: {'; '.join(failures)}")
    return len(retained)

def _release_pty_conhosts(pty, session_name="?", timeout=3):
    lock = getattr(pty, "_muxd_conhost_lock", None)
    if lock is None:
        return True
    with lock:
        owned = list(getattr(pty, "_muxd_conhost_processes", ()) or ())
        retained, failures = _terminate_conhost_records(owned, timeout=timeout)
        pty._muxd_conhost_processes = retained
    if failures:
        log(f"[{session_name}] ConPTY host cleanup failed: {'; '.join(failures)}")
        return False
    return True

def _abort_uncustodied_pty(pty, conpty_hosts_before, session_name):
    try:
        pty.terminate(force=True)
    except Exception:
        pass
    _terminate_pid_tree(int(getattr(pty, "pid", 0) or 0), timeout=3)
    _release_pty_resources(pty)
    try:
        late_hosts = _new_conpty_host_processes(conpty_hosts_before, timeout=0.2)
        retained, failures = _terminate_conhost_records(late_hosts, timeout=3)
    except OSError as error:
        _retain_pending_conpty_baseline(
            conpty_hosts_before,
            f"aborted spawn for {session_name}",
        )
        log(f"[{session_name}] could not enumerate ConPTY hosts after aborted spawn: {error}")
        return
    if retained:
        _retain_orphaned_conpty_hosts(retained, f"aborted spawn for {session_name}")
        log(f"[{session_name}] uncustodied ConPTY hosts survived abort: {'; '.join(failures)}")

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
                           capture_output=True, text=True, timeout=15,
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
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
PROTOCOL = 4
CAPS = ["ls", "info", "create", "createAck", "bind", "input", "open", "attach", "kill", "rename", "heal", "tail", "scrollback", "resize", "owner", "relaunch", "agentTruth"]
STARTED = time.time()
AGENT_WORKING_FRESH = float(ENV.get("AGENT_WORKING_FRESH", "25"))
AGENT_STARTING_GRACE = float(ENV.get("AGENT_STARTING_GRACE", "45"))
# --- process-truth probe budget -------------------------------------------------
# A session payload is built on the event loop, several times a second, for every
# session. Asking the OS "is the agent really alive" is a syscall storm, so the
# answer is cached and refreshed OFF the loop by a tiny dedicated executor. Nothing
# on the request path ever waits for a probe: a missing or stale answer degrades to
# the heuristic ladder instead of blocking.
AGENT_TRUTH_TTL = max(10.0, float(ENV.get("AGENT_TRUTH_TTL", "10")))          # refresh no faster than this
AGENT_TRUTH_MAX_AGE = max(AGENT_TRUTH_TTL * 3, float(ENV.get("AGENT_TRUTH_MAX_AGE", "30")))  # older => heuristic
AGENT_TRUTH_PROBE_TIMEOUT = min(3.0, max(0.5, float(ENV.get("AGENT_TRUTH_PROBE_TIMEOUT", "3"))))
AGENT_TRUTH_MAX_INFLIGHT = 2        # hard ceiling on concurrent probes; a wedged probe cannot pile up
AGENT_TRUTH_MAX_PIDS = 400          # bounded subtree walk
AGENT_TRUTH_MAX_DEPTH = 12
ANSI_RE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]")
STALE_CSI_RE = re.compile(r"\[[0-?]*[ -/]*[@-~]")
CTRL_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
PRIVATE_MODE_RE = re.compile(br"\x1b\[\?([0-9;]+)([hl])")
# Alt-screen/bracketed-paste PLUS the mouse-tracking family: a viewer that attaches after the
# app's ?1000h/?1006h scrolled out of the ring must still learn that the app owns the wheel,
# or its wheel input falls back to arrow keys / dies entirely (the "can't scroll a TUI" bug).
# DECCKM (?1) rides along so viewers pick the right arrow encoding (SS3 vs CSI) for that fallback
# and for real cursor keys; readline treats both forms as arrows, so a dead TUI can't wedge input.
REPLAY_PRIVATE_MODES = frozenset((1, 47, 1047, 1049, 2004, 9, 1000, 1002, 1003, 1005, 1006, 1007, 1015))


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


def _pid_descends_from(pid, ancestor_pid, timeout=15):
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
            timeout=max(0.5, float(timeout)),
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        if result.returncode != 0:
            return None
        value = result.stdout.strip().lower()
        return True if value == "true" else False if value == "false" else None
    except Exception:
        return None


_AGENT_TRUTH_LOCK = threading.Lock()      # guards the cache dict ONLY - never held across a probe
_AGENT_TRUTH_CACHE = {}                   # (pid, start_token) -> entry
_AGENT_TRUTH_INFLIGHT = 0
_AGENT_TRUTH_EXECUTOR = concurrent.futures.ThreadPoolExecutor(
    max_workers=AGENT_TRUTH_MAX_INFLIGHT, thread_name_prefix="agent-truth"
)
AGENT_TRUTH_UNKNOWN = {"procAlive": False, "cpuActiveRecent": False, "exe": "", "checkedUtc": ""}


def _process_rows():
    """(pid, parentPid, exe) for every process, from one Toolhelp32 snapshot.

    Same machinery `_direct_child_pids` uses, walked once instead of once per parent:
    a descendant probe that re-snapshots per level costs O(depth) snapshots for nothing.
    """
    if os.name != "nt":
        return []
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    create_snapshot = kernel32.CreateToolhelp32Snapshot
    create_snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    create_snapshot.restype = wintypes.HANDLE
    close_handle = kernel32.CloseHandle
    close_handle.argtypes = [wintypes.HANDLE]
    close_handle.restype = wintypes.BOOL
    snapshot = create_snapshot(0x00000002, 0)  # TH32CS_SNAPPROCESS
    if snapshot == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    entry = _PROCESSENTRY32W()
    entry.dwSize = ctypes.sizeof(entry)
    rows = []
    try:
        first = kernel32.Process32FirstW
        first.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
        first.restype = wintypes.BOOL
        next_entry = kernel32.Process32NextW
        next_entry.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
        next_entry.restype = wintypes.BOOL
        ctypes.set_last_error(0)
        if not first(snapshot, ctypes.byref(entry)):
            error = ctypes.get_last_error()
            if error == 18:  # ERROR_NO_MORE_FILES
                return []
            raise ctypes.WinError(error)
        while True:
            rows.append((int(entry.th32ProcessID), int(entry.th32ParentProcessID), str(entry.szExeFile or "")))
            ctypes.set_last_error(0)
            if not next_entry(snapshot, ctypes.byref(entry)):
                error = ctypes.get_last_error()
                if error not in (0, 18):
                    raise ctypes.WinError(error)
                break
        return rows
    finally:
        close_handle(snapshot)


def _process_cpu_100ns(pid):
    """Kernel+user CPU consumed by one pid, in 100ns ticks. 0 when unreadable."""
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return 0
    if pid <= 0 or os.name != "nt":
        return 0
    try:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        open_process = kernel32.OpenProcess
        open_process.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        open_process.restype = wintypes.HANDLE
        get_process_times = kernel32.GetProcessTimes
        get_process_times.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        get_process_times.restype = wintypes.BOOL
        handle = open_process(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return 0
        try:
            created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
            if not get_process_times(handle, ctypes.byref(created), ctypes.byref(exited),
                                     ctypes.byref(kernel), ctypes.byref(user)):
                return 0
            total = 0
            for part in (kernel, user):
                total += (int(part.dwHighDateTime) << 32) | int(part.dwLowDateTime)
            return total
        finally:
            kernel32.CloseHandle(handle)
    except Exception:
        return 0


def _agent_truth_subtree(root_pid, rows):
    """Bounded descendant walk. Returns (pids, exe, agentPid) where exe/agentPid name the
    deepest non-ConPTY-host descendant - the process a human would call "the agent"."""
    children = {}
    names = {}
    for pid, parent, exe in rows:
        names[pid] = exe
        children.setdefault(parent, []).append(pid)
    if root_pid not in names:
        return [], "", 0
    pids = [root_pid]
    best = (-1, root_pid, names.get(root_pid, ""))
    seen = {root_pid}
    frontier = [(root_pid, 0)]
    while frontier and len(pids) < AGENT_TRUTH_MAX_PIDS:
        pid, depth = frontier.pop(0)
        if depth >= AGENT_TRUTH_MAX_DEPTH:
            continue
        for child in sorted(children.get(pid, [])):
            if child in seen or child == pid or len(pids) >= AGENT_TRUTH_MAX_PIDS:
                continue
            seen.add(child)
            pids.append(child)
            frontier.append((child, depth + 1))
            exe = names.get(child, "")
            if exe.lower() in _CONPTY_HOST_EXECUTABLES:
                continue
            if depth + 1 > best[0]:
                best = (depth + 1, child, exe)
    return pids, best[2], best[1]


def _agent_truth_probe(pid, start_token, previous=None):
    """Off-loop, bounded. Answers whether the session's process tree is REALLY alive."""
    checked = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    if start_token and not _same_process_instance(pid, start_token):
        # The pid we recorded is gone (or was recycled onto somebody else's process).
        return {"procAlive": False, "cpuActiveRecent": False, "exe": "", "checkedUtc": checked}, 0, 0
    try:
        rows = _process_rows()
    except Exception as error:
        log(f"agent-truth snapshot failed for pid {pid}: {error}")
        rows = None
    if rows is None:
        # Snapshot unavailable: fall back to the per-pid ancestry walk, capped at the
        # probe budget, to re-confirm the descendant we saw last time.
        agent_pid = int((previous or {}).get("agentPid", 0) or 0)
        alive = _pid_alive(pid)
        if not alive and agent_pid > 0 and _pid_alive(agent_pid):
            alive = _pid_descends_from(agent_pid, pid, timeout=AGENT_TRUTH_PROBE_TIMEOUT) is True
        return ({"procAlive": bool(alive), "cpuActiveRecent": False,
                 "exe": str((previous or {}).get("exe", "") or "") if alive else "",
                 "checkedUtc": checked}, 0, 0)
    pids, exe, agent_pid = _agent_truth_subtree(int(pid), rows)
    alive = bool(pids) or _pid_alive(pid)
    if not alive:
        return {"procAlive": False, "cpuActiveRecent": False, "exe": "", "checkedUtc": checked}, 0, 0
    cpu = sum(_process_cpu_100ns(p) for p in pids)
    prev_cpu = int((previous or {}).get("cpu", 0) or 0)
    had_prev = prev_cpu > 0
    return ({"procAlive": True, "cpuActiveRecent": bool(had_prev and cpu > prev_cpu),
             "exe": exe, "checkedUtc": checked}, cpu, agent_pid)


def _agent_truth_refresh(key, pid, start_token, previous):
    global _AGENT_TRUTH_INFLIGHT
    truth, cpu, agent_pid = AGENT_TRUTH_UNKNOWN, 0, 0
    ok = False
    try:
        # Run the probe in a fresh daemon thread so it can be abandoned if
        # it hangs.  _agent_truth_probe touches kernel32 / psutil syscalls
        # that can wedge (hung process handle, frozen snapshot) -- the
        # bounded .join below is the ONLY enforcement of
        # AGENT_TRUTH_PROBE_TIMEOUT.
        result = {}
        def _run_probe():
            try:
                t, c, a = _agent_truth_probe(pid, start_token, previous)
                result["truth"], result["cpu"], result["agent_pid"] = t, c, a
                result["ok"] = True
            except Exception as exc:
                result["error"] = exc
        probe_thread = threading.Thread(target=_run_probe, daemon=True)
        probe_thread.start()
        probe_thread.join(timeout=AGENT_TRUTH_PROBE_TIMEOUT)
        if probe_thread.is_alive():
            log(f"agent-truth probe timed out after {AGENT_TRUTH_PROBE_TIMEOUT}s for pid {pid}")
            # Thread abandoned as daemon; inflight counter recovers in finally.
        elif result.get("error"):
            raise result["error"]
        else:
            truth, cpu, agent_pid = result["truth"], result["cpu"], result["agent_pid"]
            ok = True
    except Exception as error:
        log(f"agent-truth probe failed for pid {pid}: {error}")
    finally:
        with _AGENT_TRUTH_LOCK:
            _AGENT_TRUTH_INFLIGHT = max(0, _AGENT_TRUTH_INFLIGHT - 1)
            entry = _AGENT_TRUTH_CACHE.get(key) or {}
            entry["inflight"] = False
            if ok:
                entry["truth"] = truth
                entry["cpu"] = cpu
                entry["agentPid"] = agent_pid
                entry["exe"] = truth.get("exe", "")
                entry["at"] = time.monotonic()
            _AGENT_TRUTH_CACHE[key] = entry


def _agent_truth_schedule(key, pid, start_token, entry):
    global _AGENT_TRUTH_INFLIGHT
    if _AGENT_TRUTH_INFLIGHT >= AGENT_TRUTH_MAX_INFLIGHT:
        return          # a wedged probe degrades this session to 'heuristic'; it never queues threads
    _AGENT_TRUTH_INFLIGHT += 1
    entry["inflight"] = True
    _AGENT_TRUTH_CACHE[key] = entry
    previous = dict(entry)
    try:
        _AGENT_TRUTH_EXECUTOR.submit(_agent_truth_refresh, key, pid, start_token, previous)
    except Exception as error:
        _AGENT_TRUTH_INFLIGHT = max(0, _AGENT_TRUTH_INFLIGHT - 1)
        entry["inflight"] = False
        log(f"agent-truth probe could not be scheduled for pid {pid}: {error}")


def session_agent_truth(sess, now=None):
    """(agentTruth, agentStateSource) for a session payload. NEVER blocks and is
    never called with the state lock held for anything but a dict read: a stale or
    failed probe reports 'heuristic' and lets `session_agent_status` stand."""
    if now is None:
        now = time.monotonic()
    try:
        pid = int(getattr(sess, "child_pid", 0) or 0)
    except (TypeError, ValueError):
        pid = 0
    start_token = str(getattr(sess, "child_start_token", "") or "")
    if pid <= 0:
        return dict(AGENT_TRUTH_UNKNOWN), "heuristic"
    key = (pid, start_token)
    with _AGENT_TRUTH_LOCK:
        entry = _AGENT_TRUTH_CACHE.get(key)
        if entry is None:
            entry = {"truth": None, "at": 0.0, "cpu": 0, "agentPid": 0, "exe": "", "inflight": False}
        age = now - float(entry.get("at", 0.0) or 0.0) if entry.get("at") else float("inf")
        if age >= AGENT_TRUTH_TTL and not entry.get("inflight"):
            _agent_truth_schedule(key, pid, start_token, entry)
        truth = entry.get("truth")
    if not truth or age > AGENT_TRUTH_MAX_AGE:
        # Never probed, or the last answer is too old to be evidence of anything now.
        return (dict(truth) if truth else dict(AGENT_TRUTH_UNKNOWN)), "heuristic"
    return dict(truth), "process"


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

LOCAL_VIEWER_QUEUE_MAX = 64          # ~768ms of link/loop-lag tolerance (was 4 = ~48ms -> spurious detaches)
LOCAL_VIEWER_SLOW = object()

def fanout_local_output(session, data):
    for local_queue in list(session.local):
        try:
            local_queue.put_nowait(data)
        except asyncio.QueueFull:
            # A brief lag must NOT detach a local terminal (that was the "[muxctl] detached" bug). Drop the
            # OLDEST chunk to make room, keep the viewer, and nudge a full repaint so the momentary gap heals.
            try:
                local_queue.get_nowait()
            except asyncio.QueueEmpty:
                pass
            try:
                local_queue.put_nowait(data)
            except asyncio.QueueFull:
                pass
            try:
                redraw_nudge(session)
            except Exception:
                pass

def attach_replay_payload(session, sb_limit):
    """First frame for a new viewer. With scrollback disabled (sb<=0) the MODE PREFIX must still
    go out: it is what tells the client the app owns the wheel (alt screen / mouse tracking).
    Gating the whole frame on sb>0 silently resurrected the 'local terminal cannot scroll' bug
    under MUXCTL_SCROLLBACK=0."""
    if sb_limit > 0:
        return session.scrollback(sb_limit)
    return session.replay_state.prefix()

def redraw_nudge(session):
    """Force a full repaint from a full-screen TUI by wiggling the PTY size (SIGWINCH) — alt-screen only,
    rate-limited. The running TUI is the authoritative screen model, so this GUARANTEES a newly-attached or
    resynced viewer sees a complete screen instead of a mid-stream diff fragment (the black-with-a-sliver
    class). Input-free; the net size is unchanged."""
    try:
        modes = getattr(getattr(session, "replay_state", None), "private_modes", None)
        if not modes or not (modes & {47, 1047, 1049}):
            return
    except Exception:
        return
    now = time.time()
    if now - getattr(session, "_last_nudge", 0.0) < 1.0:
        return
    pty = getattr(session, "pty", None)
    if pty is None or not hasattr(pty, "setwinsize"):
        return
    session._last_nudge = now
    rows = max(2, int(getattr(session, "rows", 40)))
    cols = max(2, int(getattr(session, "cols", 140)))
    try:
        pty.setwinsize(max(1, rows - 1), cols)
        pty.setwinsize(rows, cols)
    except Exception:
        pass

class AsyncRLock:
    def __init__(self):
        self._lock = asyncio.Lock()
        self._owner = None
        self._depth = 0

    async def __aenter__(self):
        task = asyncio.current_task()
        if self._owner is task:
            self._depth += 1
            return self
        await self._lock.acquire()
        self._owner = task
        self._depth = 1
        return self

    async def __aexit__(self, exc_type, exc, tb):
        task = asyncio.current_task()
        if self._owner is not task:
            raise RuntimeError("async state lock released by a non-owner task")
        self._depth -= 1
        if self._depth == 0:
            self._owner = None
            self._lock.release()

    def owned_by_current_task(self):
        return self._owner is asyncio.current_task()

async def run_state_transaction(lock, operation):
    if lock.owned_by_current_task():
        return await operation()

    async def execute():
        async with lock:
            return await operation()

    transaction = asyncio.create_task(execute())
    try:
        return await asyncio.shield(transaction)
    except asyncio.CancelledError:
        while not transaction.done():
            try:
                await asyncio.shield(transaction)
            except asyncio.CancelledError:
                continue
        if not transaction.cancelled():
            try:
                transaction.result()
            except Exception as error:
                log(f"state transaction failed after caller cancellation: {error}")
        raise

async def supervise_background(name, factory, restart_delay=1.0):
    while True:
        try:
            await factory()
            log(f"[supervisor] {name} returned unexpectedly; restarting")
        except asyncio.CancelledError:
            raise
        except Exception as error:
            log(f"[supervisor] {name} failed: {type(error).__name__}: {error}; restarting")
        await asyncio.sleep(max(0.01, restart_delay))

def start_supervised_background(registry, name, factory, restart_delay=1.0):
    task = asyncio.create_task(
        supervise_background(name, factory, restart_delay=restart_delay),
        name=f"muxd-{name}",
    )
    registry.add(task)

    def completed(done):
        registry.discard(done)
        if done.cancelled():
            return
        try:
            error = done.exception()
        except asyncio.CancelledError:
            return
        if error is not None:
            log(f"[supervisor] {name} supervisor exited: {type(error).__name__}: {error}")

    task.add_done_callback(completed)
    return task

def watch_snapshot():
    with WATCH_LOCK:
        return dict(WATCH)

def clean_terminal_text(s):
    # xterm/codex emits CSI sequences with intermediate bytes, e.g. ESC[0 q.
    # Older sanitizing stripped ESC but left "[0 q", so remove those leftovers too.
    s = ANSI_RE.sub("", str(s or ""))
    s = STALE_CSI_RE.sub("", s)
    return CTRL_RE.sub("", s)


class TerminalReplayState:
    def __init__(self):
        self._lock = threading.RLock()
        self._scan_tail = b""
        self.private_modes = set()
        self.cursor_visible = None

    def ingest(self, data):
        with self._lock:
            scan = self._scan_tail + bytes(data or b"")
            for match in PRIVATE_MODE_RE.finditer(scan):
                enabled = match.group(2) == b"h"
                for raw_mode in match.group(1).split(b";"):
                    try:
                        mode = int(raw_mode)
                    except ValueError:
                        continue
                    if mode == 25:
                        self.cursor_visible = enabled
                    elif mode in REPLAY_PRIVATE_MODES:
                        if enabled:
                            self.private_modes.add(mode)
                        else:
                            self.private_modes.discard(mode)
            self._scan_tail = scan[-64:]

    def prefix(self):
        with self._lock:
            cursor_visible = self.cursor_visible
            private_modes = tuple(sorted(self.private_modes))
        out = []
        if cursor_visible is not None:
            out.append(b"\x1b[?25" + (b"h" if cursor_visible else b"l"))
        for mode in private_modes:
            out.append(f"\x1b[?{mode}h".encode("ascii"))
        return b"".join(out)


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
        self.lifecycle = "active" if spawn_now else "dormant"
        self.child_pid = 0
        self.child_start_token = ""
        self.stop_disposition = ""
        self.generation_id = "g" + os.urandom(16).hex()
        self.operation_key = ""
        self.operation_fingerprint = ""
        self.operation_created = False
        self.heal = bool(heal)          # opt-in: ONLY healed (user-armed) sessions auto-start at boot / auto-respawn
        self.cols, self.rows = max(20, cols or 140), max(8, rows or 40)
        self.created = time.time(); self.last_out = time.time()
        self.ring = collections.deque(); self.ring_len = 0
        self._ring_lock = threading.RLock()
        self.replay_state = TerminalReplayState()
        self.dead = False; self.user_killed = False
        self.deaths = []
        self.loop, self.outq = loop, outq
        self.pending = bytearray(); self.plock = threading.Lock()   # output coalescing (flushed by the pump)
        self.local = set()                                          # local (muxctl) viewer queues — fanned the same output
        self.local_sizes = {}
        self.remote_size_active = False
        self.wq = queue.Queue()                                     # input write queue → serialized, chunked writes
        self._writer_lock = threading.Lock()
        self._writer_thread = None
        self._writer_pty = None
        self.pty = None
        if spawn_now: self.spawn()
        else: self.dead = True          # placeholder tab: NOTHING runs until the user attaches (revive) or arms it

    def spawn(self):
        self.lifecycle = "starting"
        cmdline = "powershell.exe -NoLogo"
        direct_cmd = False
        if self.cmd:
            encoded = base64.b64encode(self.cmd.encode("utf-16le")).decode("ascii")
            cmdline = subprocess.list2cmdline(["powershell.exe", "-NoLogo", "-NoExit", "-EncodedCommand", encoded])
            direct_cmd = True
        with _CONPTY_SPAWN_LOCK:
            with _ORPHANED_CONPTY_LOCK:
                if _PENDING_CONPTY_BASELINES:
                    raise RuntimeError(
                        "ConPTY custody is quarantined pending orphan-host discovery"
                    )
            conpty_hosts_before = _conpty_host_process_records(os.getpid())
            try:
                try:
                    pty = PtyProcess.spawn(cmdline, dimensions=(self.rows, self.cols), cwd=self.cwd)
                except Exception:
                    if not self.cmd:
                        raise
                    # If a saved command hits a Windows command-line edge case, keep the session usable and
                    # fall back to typing the command into an already-started shell.
                    pty = PtyProcess.spawn("powershell.exe -NoLogo", dimensions=(self.rows, self.cols), cwd=self.cwd)
                    direct_cmd = False
            except Exception:
                try:
                    orphaned = _new_conpty_host_processes(conpty_hosts_before, timeout=0.2)
                    retained, failures = _terminate_conhost_records(orphaned, timeout=3)
                    if retained:
                        _retain_orphaned_conpty_hosts(retained, f"failed spawn for {self.name}")
                        log(f"[{self.name}] failed spawn leaked ConPTY hosts: {'; '.join(failures)}")
                except OSError as error:
                    _retain_pending_conpty_baseline(
                        conpty_hosts_before,
                        f"failed spawn for {self.name}",
                    )
                    log(f"[{self.name}] could not enumerate ConPTY hosts after failed spawn: {error}")
                raise
            try:
                owned_conhosts = _new_conpty_host_processes(conpty_hosts_before)
            except OSError as error:
                _abort_uncustodied_pty(pty, conpty_hosts_before, self.name)
                raise RuntimeError("could not establish ConPTY host process custody") from error
            if os.name == "nt" and not owned_conhosts:
                _abort_uncustodied_pty(pty, conpty_hosts_before, self.name)
                raise RuntimeError("could not establish ConPTY host process custody")
            pty._muxd_conhost_lock = threading.Lock()
            pty._muxd_conhost_processes = owned_conhosts
        self.pty = pty
        self.dead = False
        self.child_pid = int(getattr(self.pty, "pid", 0) or 0)
        self.child_start_token = _process_start_token(self.child_pid)
        self.lifecycle = "active"
        self._ensure_writer()
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
                    writer_stopped = self.stop_input_writer()
                    if not writer_stopped:
                        log(f"[{self.name}] input writer did not stop after PTY EOF")
                    self.loop.call_soon_threadsafe(self.outq.put_nowait, ("dead", self.name, ""))
                    log(f"[{self.name}] pty EOF (shell exited or killed)")
                _release_pty_resources(pty)
                conhosts_released = _release_pty_conhosts(pty, self.name)
                if conhosts_released and pty is self.pty:
                    self.pty = None
                return
            if not data: continue
            if pty is not self.pty: return          # superseded by a respawn
            b = data.encode("utf-8", "replace")
            self.replay_state.ingest(b)
            self._append_ring(b)
            with self.plock:                           # coalesced; the pump flushes on a ~12ms timer
                self.pending += b                       # backpressure: if a flood outruns a slow link, keep the
                if len(self.pending) > 512_000:         # last ~512KB unsent (the ring still holds history for reattach)
                    del self.pending[:len(self.pending) - 512_000]

    def drain(self):
        if not self.pending: return None
        with self.plock:
            chunk = bytes(self.pending); self.pending = bytearray()
        return chunk

    def _append_ring(self, data):
        with self._ring_lock:
            if len(data) >= RING_CAP:
                retained = data[-RING_CAP:]
                self.ring.clear()
                self.ring.append(retained)
                self.ring_len = len(retained)
                self.last_out = time.time()
                return
            self.ring.append(data)
            self.ring_len += len(data)
            self.last_out = time.time()
            while self.ring_len > RING_CAP:
                old = self.ring.popleft()
                self.ring_len -= len(old)

    def _ring_snapshot(self):
        with self._ring_lock:
            return tuple(self.ring)

    def _ensure_writer_locked(self):
        if (
            self._writer_thread is not None
            and self._writer_thread.is_alive()
            and self._writer_pty is self.pty
        ):
            return self._writer_thread
        work_queue = queue.Queue()
        pty = self.pty
        thread = threading.Thread(
            target=self._writer,
            args=(work_queue, pty),
            name=f"{self.name}-writer",
            daemon=True,
        )
        self.wq = work_queue
        self._writer_thread = thread
        self._writer_pty = pty
        thread.start()
        return thread

    def _ensure_writer(self):
        with self._writer_lock:
            return self._ensure_writer_locked()

    def _enqueue_writer_item(self, item):
        with self._writer_lock:
            if not self.alive() or self.pty is None:
                return False
            self._ensure_writer_locked()
            self.wq.put_nowait(item)
            return True

    def stop_input_writer(self, timeout=2):
        lock = getattr(self, "_writer_lock", None)
        if lock is None:
            return True
        with lock:
            thread = self._writer_thread
            work_queue = self.wq
            self._writer_thread = None
            self._writer_pty = None
            if thread is not None:
                try:
                    work_queue.put_nowait(None)
                except Exception:
                    pass
        if thread is None:
            return True
        if thread is not threading.current_thread():
            thread.join(timeout=max(0, timeout))
        return not thread.is_alive()

    def _writer(self, work_queue, pty):
        # serialize input; slice large pastes into <=1KB writes so a big paste can't stall/garble ConPTY input.
        while True:
            item = work_queue.get()
            if item is None: return
            if isinstance(item, tuple):
                s, completed, outcome = item
            else:
                s, completed, outcome = item, None, None
            ok, detail = False, "PTY is not live"
            try:
                if pty is not None and pty is self.pty and not self.dead:
                    if len(s) <= 1024:
                        pty.write(s)
                        ok, detail = True, ""
                    else:
                        ok, detail = True, ""
                        for i in range(0, len(s), 1024):
                            if self.dead or pty is not self.pty:
                                ok, detail = False, "PTY exited during input"
                                break
                            pty.write(s[i:i+1024]); time.sleep(0.004)
            except Exception as e:
                detail = "PTY input write failed"
                log(f"[{self.name}] write failed: {e}")
            finally:
                if completed is not None:
                    outcome.append((ok, detail))
                    completed.set()

    def write(self, data: bytes):
        try:
            self._enqueue_writer_item(data.decode("utf-8", "replace"))
        except Exception as e: log(f"[{self.name}] enqueue failed: {e}")

    def write_confirmed(self, data: bytes, timeout=8):
        completed = threading.Event()
        outcome = []
        try:
            accepted = self._enqueue_writer_item(
                (data.decode("utf-8", "replace"), completed, outcome)
            )
        except Exception as e:
            log(f"[{self.name}] confirmed enqueue failed: {e}")
            return False, "PTY input queue failed"
        if not accepted:
            return False, "PTY is not live"
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
        for b in reversed(self._ring_snapshot()):
            out.append(b); n += len(b)
            if n >= limit: break
        return self.replay_state.prefix() + b"".join(reversed(out))

    def tail_text(self, nbytes=1600, lines=0):
        raw = bytearray()
        for b in reversed(self._ring_snapshot()):
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
                try:
                    pty.terminate(force=True)
                except Exception:
                    pass
                try:
                    if not pty.isalive():
                        _release_pty_resources(pty)
                except Exception:
                    pass
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
        self._ring_lock = threading.RLock()
        self.replay_state = TerminalReplayState()
        self.dead = False; self.user_killed = False
        self.deaths = []
        self.loop, self.outq = loop, outq
        self.pending = bytearray(); self.plock = threading.Lock()
        self.local = set()
        self.local_sizes = {}
        self.remote_size_active = False
        self.owner_ws = owner_ws
        self.owner = True
        self.expected_owner = True
        self.owner_key = str(owner_key or "")
        self.identity_pending = False
        self.lifecycle = "active"
        self.child_pid = 0
        self.child_start_token = ""
        self.stop_disposition = ""
        self.generation_id = "g" + os.urandom(16).hex()
        self.operation_key = ""
        self.operation_fingerprint = ""
        self.operation_created = False
        self.owner_exit_confirmed = False
        self.input_waiters = {}
        self.input_seq = 0

    def ingest(self, data: bytes):
        if not data: return
        self.replay_state.ingest(data)
        self._append_ring(data)
        with self.plock:
            self.pending += data
            if len(self.pending) > 512_000:
                del self.pending[:len(self.pending) - 512_000]

    def drain(self):
        if not self.pending: return None
        with self.plock:
            chunk = bytes(self.pending); self.pending = bytearray()
        return chunk

    def _append_ring(self, data):
        with self._ring_lock:
            if len(data) >= RING_CAP:
                retained = data[-RING_CAP:]
                self.ring.clear()
                self.ring.append(retained)
                self.ring_len = len(retained)
                self.last_out = time.time()
                return
            self.ring.append(data)
            self.ring_len += len(data)
            self.last_out = time.time()
            while self.ring_len > RING_CAP:
                old = self.ring.popleft()
                self.ring_len -= len(old)

    def _ring_snapshot(self):
        with self._ring_lock:
            return tuple(self.ring)

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
        for b in reversed(self._ring_snapshot()):
            out.append(b); n += len(b)
            if n >= limit: break
        return self.replay_state.prefix() + b"".join(reversed(out))

    def tail_text(self, nbytes=1600, lines=0):
        raw = bytearray()
        for b in reversed(self._ring_snapshot()):
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

sessions = {}          # name -> Session
intent_records = {}    # scope:type:intentId -> durable request/outcome tombstone
INTENT_TERMINAL_RETENTION_SECONDS = 180 * 24 * 60 * 60
INTENT_TERMINAL_LIMIT = 20000

def live_session_names():
    names = []
    for name, sess in list(sessions.items()):
        try:
            if sess.alive():
                names.append(name)
        except Exception:
            pass
    return names


def restore_local_session_size(session):
    sizes = list(getattr(session, "local_sizes", {}).values())
    if not sizes:
        return
    cols, rows = max(sizes, key=lambda value: (int(value[0]), int(value[1])))
    session.resize(cols, rows)


def update_local_session_size(session, viewer, cols, rows):
    size = (int(cols), int(rows))
    session.local_sizes[viewer] = size
    if not session.remote_size_active:
        session.resize(*size)


def apply_remote_session_size(session, message):
    if message.get("active") is False:
        session.remote_size_active = False
        restore_local_session_size(session)
        return
    session.remote_size_active = True
    session.resize(message.get("cols", 140), message.get("rows", 40))


def clear_remote_size_ownership():
    for session in list(sessions.values()):
        if not getattr(session, "remote_size_active", False):
            continue
        session.remote_size_active = False
        restore_local_session_size(session)


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

def session_records_payload(source=None):
    source = sessions if source is None else source
    return {
        n: {"cmd": s.cmd, "cwd": s.cwd, "cols": s.cols, "rows": s.rows, "heal": s.heal,
            "sessionId": getattr(s, "session_id", "") or "",
            "aliases": list(getattr(s, "aliases", []) or []),
            "ids": list(getattr(s, "ids", []) or []),
            "owner": bool(getattr(s, "owner", False) or getattr(s, "expected_owner", False)),
            "ownerKey": str(getattr(s, "owner_key", "") or ""),
            "identityPending": bool(getattr(s, "identity_pending", False)),
            "lifecycle": str(getattr(s, "lifecycle", "active") or "active"),
            "childPid": int(getattr(s, "child_pid", 0) or 0),
            "childStartToken": str(getattr(s, "child_start_token", "") or ""),
            "stopDisposition": str(getattr(s, "stop_disposition", "") or ""),
            "userKilled": bool(getattr(s, "user_killed", False)),
            "generationId": str(getattr(s, "generation_id", "") or ""),
            "operationKey": str(getattr(s, "operation_key", "") or ""),
            "operationFingerprint": str(getattr(s, "operation_fingerprint", "") or ""),
            "operationCreated": bool(getattr(s, "operation_created", False)),
            "deaths": [float(value) for value in list(getattr(s, "deaths", []) or [])[-16:]]}
        for n, s in source.items()
    }

def manifest_payload(source=None):
    return {
        "version": 2,
        "sessions": session_records_payload(source),
        "intents": compact_intent_records(intent_records),
    }

def compact_intent_records(records, now=None):
    now = time.time() if now is None else float(now)
    live = {
        key: record
        for key, record in records.items()
        if record.get("status") not in ("completed", "failed")
    }
    terminal = sorted(
        (
            (key, record)
            for key, record in records.items()
            if record.get("status") in ("completed", "failed")
            and now - float(record.get("updatedAt", record.get("createdAt", 0)) or 0)
            <= INTENT_TERMINAL_RETENTION_SECONDS
        ),
        key=lambda item: float(item[1].get("updatedAt", item[1].get("createdAt", 0)) or 0),
        reverse=True,
    )[:INTENT_TERMINAL_LIMIT]
    return {**live, **dict(terminal)}

def valid_session_records(value):
    if not isinstance(value, dict):
        return False
    required = ("cmd", "cwd", "cols", "rows", "heal")
    lifecycles = {"active", "dormant", "starting", "stopping", "failed"}
    stop_dispositions = {"", "remove", "replace"}
    return all(
        strict_mux_name(name)
        and isinstance(record, dict)
        and all(field in record for field in required)
        and str(record.get("lifecycle", "active") or "active") in lifecycles
        and isinstance(record.get("childPid", 0), int)
        and isinstance(record.get("childStartToken", ""), str)
        and str(record.get("stopDisposition", "") or "") in stop_dispositions
        and isinstance(record.get("userKilled", False), bool)
        and isinstance(record.get("generationId", ""), str)
        and isinstance(record.get("operationKey", ""), str)
        and isinstance(record.get("operationFingerprint", ""), str)
        and isinstance(record.get("operationCreated", False), bool)
        for name, record in value.items()
    )

def valid_intent_records(value):
    statuses = {"accepted", "dispatching", "completed", "failed", "uncertain"}
    return isinstance(value, dict) and all(
        isinstance(key, str)
        and re.fullmatch(r"[A-Za-z0-9._:-]{1,320}", key)
        and isinstance(record, dict)
        and str(record.get("status", "")) in statuses
        and re.fullmatch(r"[a-f0-9]{64}", str(record.get("fingerprint", "")))
        and isinstance(record.get("kind", ""), str)
        and isinstance(record.get("session", ""), str)
        and isinstance(record.get("result", {}), dict)
        for key, record in value.items()
    )

def valid_manifest(value):
    if (
        isinstance(value, dict)
        and value.get("version") == 2
        and "sessions" in value
        and "intents" in value
    ):
        return valid_session_records(value.get("sessions")) and valid_intent_records(value.get("intents"))
    return valid_session_records(value)

def manifest_load():
    loaded = durable_json_load(MANIFEST, {}, validate=valid_manifest)
    if isinstance(loaded, dict) and loaded.get("version") == 2:
        intent_records.clear()
        intent_records.update(loaded.get("intents") or {})
        return loaded.get("sessions") or {}
    intent_records.clear()
    return loaded

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
    "lifecycle",
    "child_pid",
    "child_start_token",
    "stop_disposition",
    "generation_id",
    "operation_key",
    "operation_fingerprint",
    "operation_created",
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
_RELAY_TLS_CONTEXT = None
_RELAY_TLS_CONTEXT_LOCK = threading.Lock()

def _load_relay_tls_context():
    global _RELAY_TLS_CONTEXT
    with _RELAY_TLS_CONTEXT_LOCK:
        if _RELAY_TLS_CONTEXT is None:
            _RELAY_TLS_CONTEXT = ssl.create_default_context()
        return _RELAY_TLS_CONTEXT

async def relay_tls_context(url):
    if not str(url or "").lower().startswith("wss://"):
        return None
    return await asyncio.get_running_loop().run_in_executor(None, _load_relay_tls_context)

def remote_create_violation(frame):
    if not isinstance(frame, dict):
        return "create frame must be an object"
    extra = set(frame) - REMOTE_CREATE_FIELDS
    if extra:
        return "create frame contains forbidden fields: " + ",".join(sorted(map(str, extra)))
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

def intent_id(value):
    raw = str(value or "").strip()
    return raw if re.fullmatch(r"[A-Za-z0-9._-]{1,128}", raw) else ""

def intent_fingerprint(frame):
    payload = {
        str(key): value
        for key, value in (frame or {}).items()
        if key not in ("intentId", "rid") and not str(key).startswith("_")
    }
    encoded = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest()

def intent_key(scope, kind, request_id):
    return f"{scope}:{kind}:{request_id}"

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
    # Process truth is advisory and additive: it reports what the OS says about the
    # session's own process tree. It never overrides the heuristic ladder above - when
    # the probe is stale or failed, agentStateSource says 'heuristic' and agentState
    # stands on its own.
    try:
        agent_truth, agent_source = session_agent_truth(sess)
    except Exception:
        agent_truth, agent_source = dict(AGENT_TRUTH_UNKNOWN), "heuristic"
    return {"name": name, "alive": alive, "created": int(sess.created * 1000),
            "lastOut": int(sess.last_out * 1000), "cols": sess.cols, "rows": sess.rows,
            "tail": tail, "heal": sess.heal, "localViewers": len(sess.local),
            "localFirst": owner or len(sess.local) > 0, "owner": owner,
            "hasCommand": has_cmd, "shellOnly": alive and not has_cmd,
            "ready": alive, "kind": kind,
            "sessionId": getattr(sess, "session_id", "") or "",
            "aliases": list(getattr(sess, "aliases", []) or []),
            "identityPending": bool(getattr(sess, "identity_pending", False)),
            "lifecycle": str(getattr(sess, "lifecycle", "active") or "active"),
            "childPid": int(getattr(sess, "child_pid", 0) or 0),
            "agentState": agent["agentState"], "agentLabel": agent["agentLabel"],
            "agentDetail": agent["agentDetail"], "agentConfidence": agent["agentConfidence"],
            "needsAttention": agent["needsAttention"], "lastOutAgeMs": agent.get("lastOutAgeMs", 0),
            "agentTruth": agent_truth, "agentStateSource": agent_source}

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
        writer_stopped = await asyncio.get_running_loop().run_in_executor(None, s.stop_input_writer)
        if not writer_stopped:
            log(f"[{getattr(s, 'name', '?')}] input writer did not stop after session exit")
        if release_claim_on_success:
            release_session_claim(s)
        return True, "already stopped"
    s.dead = True

    def terminate_and_verify():
        deadline = time.monotonic() + max(0.1, timeout)
        terminate_error = None
        try:
            pty.terminate(force=True)
        except Exception as e:
            terminate_error = e
        remaining = deadline - time.monotonic()
        if remaining <= 0 and _pid_alive(pid):
            return False, "process termination deadline expired", True
        stopped, detail = _terminate_pid_tree(pid, timeout=max(0.1, remaining))
        if not stopped:
            return False, detail, True
        if terminate_error is not None and pid <= 0:
            return False, f"PTY termination failed: {terminate_error}", True
        _release_pty_resources(pty, deadline=deadline)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return False, "ConPTY host cleanup deadline expired", False
        if not _release_pty_conhosts(
            pty,
            getattr(s, "name", "?"),
            timeout=remaining,
        ):
            return False, "ConPTY host process is still alive after termination", False
        return True, "process exited", False

    result = await asyncio.get_running_loop().run_in_executor(
        None,
        terminate_and_verify,
    )
    if result[0]:
        if s.pty is pty:
            s.pty = None
        writer_stopped = await asyncio.get_running_loop().run_in_executor(None, s.stop_input_writer)
        if not writer_stopped:
            log(f"[{getattr(s, 'name', '?')}] input writer did not stop after process exit")
        if release_claim_on_success:
            release_session_claim(s)
    elif result[2] and s.pty is pty:
        s.dead = False
    return result[0], result[1]


async def main():
    start_watchdog_thread()
    threading.Thread(target=_guardian_keepalive, name="guardian-keepalive", daemon=True).start()
    loop = asyncio.get_running_loop()
    outq = RelayOutQueue()
    launch_locks = {}
    intent_locks = {}
    create_intent_locks = {}
    state_lock = AsyncRLock()
    background_tasks = set()

    def state_mutation(func):
        async def serialized(*args, **kwargs):
            return await run_state_transaction(
                state_lock,
                lambda: func(*args, **kwargs),
            )
        return serialized

    async def manifest_save_async(source=None):
        async with state_lock:
            payload = manifest_payload(source)
            await asyncio.get_running_loop().run_in_executor(
                None,
                lambda: durable_json_write(MANIFEST, payload),
            )
            intent_records.clear()
            intent_records.update(payload["intents"])

    def launch_lock(name):
        lock = launch_locks.get(name)
        if lock is None:
            lock = asyncio.Lock()
            launch_locks[name] = lock
        return lock

    def operation_lock(key):
        lock = intent_locks.get(key)
        if lock is None:
            lock = asyncio.Lock()
            intent_locks[key] = lock
        return lock

    def create_intent_lock(name):
        lock = create_intent_locks.get(name)
        if lock is None:
            lock = asyncio.Lock()
            create_intent_locks[name] = lock
        return lock

    @state_mutation
    async def persist_intent(key, record):
        missing = object()
        prior = intent_records.get(key, missing)
        intent_records[key] = record
        try:
            await manifest_save_async(sessions)
        except Exception as error:
            if not getattr(error, "committed", False):
                if prior is missing:
                    intent_records.pop(key, None)
                else:
                    intent_records[key] = prior
            raise

    def stable_session_result(session):
        payload = session_payload(session.name, session)
        return {
            key: payload.get(key)
            for key in (
                "name", "alive", "created", "lastOut", "cols", "rows", "heal", "owner",
                "localFirst", "localViewers", "hasCommand", "shellOnly", "ready", "kind",
                "sessionId", "aliases", "identityPending",
            )
        } | {"generationId": str(getattr(session, "generation_id", "") or "")}

    def create_semantic_result(session, detail, created):
        if detail:
            return {
                "ok": False,
                "created": False,
                "detail": str(detail),
                "retryable": False,
                "session": {},
            }
        return {
            "ok": True,
            "created": bool(created),
            "detail": "",
            "retryable": False,
            "session": stable_session_result(session),
        }

    def create_error(detail, retryable=False):
        return {
            "ok": False,
            "created": False,
            "detail": str(detail),
            "retryable": bool(retryable),
            "session": {},
        }

    @state_mutation
    async def coordinate_create_intent(first, scope, spawn_if_missing, leave_unarmed_dormant=False):
        name = strict_mux_name(first.get("s", ""))
        if not name:
            return create_error("session name required")
        async with create_intent_lock(name):
            return await coordinate_create_intent_locked(
                first,
                scope,
                spawn_if_missing,
                leave_unarmed_dormant=leave_unarmed_dormant,
            )

    async def coordinate_create_intent_locked(first, scope, spawn_if_missing, leave_unarmed_dormant=False):
        supplied = first.get("intentId") if "intentId" in first else first.get("rid")
        request_id = intent_id(supplied)
        if supplied and not request_id:
            return create_error("invalid intent id")
        if not request_id:
            session, detail, created = await coordinate_session_request(
                first,
                spawn_if_missing,
                leave_unarmed_dormant=leave_unarmed_dormant,
            )
            return create_semantic_result(session, detail, created)

        key = intent_key(scope, "create", request_id)
        fingerprint = intent_fingerprint(first)
        name = strict_mux_name(first.get("s", ""))
        async with operation_lock(key):
            record = intent_records.get(key)
            if record is not None:
                if record.get("fingerprint") != fingerprint:
                    return create_error("intent id is already bound to a different payload")
                if record.get("status") in ("completed", "failed"):
                    return dict(record.get("result") or {})
                applied = sessions.get(name)
                if (
                    applied is not None
                    and getattr(applied, "operation_key", "") == key
                    and getattr(applied, "operation_fingerprint", "") == fingerprint
                    and getattr(applied, "lifecycle", "") == "active"
                    and applied.alive()
                ):
                    result = create_semantic_result(
                        applied,
                        "",
                        bool(getattr(applied, "operation_created", False)),
                    )
                    await persist_intent(key, {**record, "status": "completed", "result": result, "updatedAt": time.time()})
                    return result
            else:
                unresolved = [
                    (other_key, other)
                    for other_key, other in intent_records.items()
                    if other_key != key
                    and other.get("kind") == "create"
                    and other.get("session") == name
                    and other.get("status") == "accepted"
                ]
                for other_key, other in unresolved:
                    applied = sessions.get(name)
                    if (
                        applied is not None
                        and getattr(applied, "operation_key", "") == other_key
                        and getattr(applied, "operation_fingerprint", "") == other.get("fingerprint")
                        and getattr(applied, "lifecycle", "") == "active"
                        and applied.alive()
                    ):
                        old_result = create_semantic_result(
                            applied,
                            "",
                            bool(getattr(applied, "operation_created", False)),
                        )
                        old_status = "completed"
                    else:
                        old_result = create_error(
                            "accepted create intent was abandoned before its outcome became durable"
                        )
                        old_status = "failed"
                    try:
                        await persist_intent(other_key, {
                            **other,
                            "status": old_status,
                            "result": old_result,
                            "updatedAt": time.time(),
                        })
                    except Exception as error:
                        return create_error(
                            "could not reconcile an abandoned create intent: " + str(error),
                            retryable=True,
                        )
                record = {
                    "kind": "create",
                    "session": name,
                    "fingerprint": fingerprint,
                    "status": "accepted",
                    "result": {},
                    "createdAt": time.time(),
                    "updatedAt": time.time(),
                }
                try:
                    await persist_intent(key, record)
                except Exception as error:
                    return create_error(
                        "could not durably accept create intent: " + str(error),
                        retryable=True,
                    )

            session, detail, created = await coordinate_session_request(
                first,
                spawn_if_missing,
                leave_unarmed_dormant=leave_unarmed_dormant,
                operation_key=key,
                operation_fingerprint=fingerprint,
            )
            applied = sessions.get(name)
            if (
                detail
                and applied is not None
                and getattr(applied, "operation_key", "") == key
                and getattr(applied, "operation_fingerprint", "") == fingerprint
                and getattr(applied, "lifecycle", "") == "active"
                and applied.alive()
            ):
                session = applied
                detail = ""
                created = bool(getattr(applied, "operation_created", False))
            result = create_semantic_result(session, detail, created)
            terminal = {
                **record,
                "status": "completed" if result["ok"] else "failed",
                "result": result,
                "updatedAt": time.time(),
            }
            try:
                await persist_intent(key, terminal)
            except Exception as error:
                return create_error(
                    "create outcome persistence failed: " + str(error),
                    retryable=True,
                )
            return result

    async def execute_input_intent(first, session, data, scope="local"):
        supplied = first.get("intentId")
        request_id = intent_id(supplied)
        if supplied and not request_id:
            return {"t": "err", "m": "invalid intent id"}
        if not request_id:
            if session is None or not session.alive():
                return {"t": "err", "m": "session is not live: " + strict_mux_name(first.get("s", ""))}
            if isinstance(session, OwnerSession):
                ok, detail = await session.write_confirmed(data)
            else:
                ok, detail = await asyncio.get_running_loop().run_in_executor(
                    None, lambda: session.write_confirmed(data)
                )
            return {"t": "input-ok", "s": session.name} if ok else {"t": "err", "m": detail}

        return await execute_durable_input_intent(
            first,
            session,
            data,
            scope,
            request_id,
        )

    @state_mutation
    async def execute_durable_input_intent(first, session, data, scope, request_id):
        key = intent_key(scope, "input", request_id)
        fingerprint = intent_fingerprint(first)
        async with operation_lock(key):
            record = intent_records.get(key)
            if record is not None:
                if record.get("fingerprint") != fingerprint:
                    return {"t": "err", "m": "intent id is already bound to a different payload"}
                if record.get("status") in ("completed", "failed"):
                    return dict(record.get("result") or {})
                return {
                    "t": "err",
                    "m": "input outcome is uncertain; the PTY write was not replayed",
                }

            if session is None or not session.alive():
                return {"t": "err", "m": "session is not live: " + strict_mux_name(first.get("s", ""))}
            record = {
                "kind": "input",
                "session": session.name,
                "fingerprint": fingerprint,
                "status": "dispatching",
                "result": {},
                "createdAt": time.time(),
                "updatedAt": time.time(),
            }
            try:
                await persist_intent(key, record)
            except Exception as error:
                return {"t": "err", "m": "could not durably reserve input intent: " + str(error)}

            if isinstance(session, OwnerSession):
                ok, detail = await session.write_confirmed(data)
            else:
                ok, detail = await asyncio.get_running_loop().run_in_executor(
                    None, lambda: session.write_confirmed(data)
                )
            result = {"t": "input-ok", "s": session.name} if ok else {"t": "err", "m": detail}
            terminal = {
                **record,
                "status": "completed" if ok else "failed",
                "result": result,
                "updatedAt": time.time(),
            }
            try:
                await persist_intent(key, terminal)
            except Exception as error:
                return {
                    "t": "err",
                    "m": "input was dispatched but its outcome is uncertain and will not be replayed: " + str(error),
                }
            return result

    @state_mutation
    async def remove_session(name, by_user):
        async with launch_lock(name):
            current = sessions.get(name)
            if current is None:
                return False, "no such session: " + name
            snapshot = persisted_session_snapshot(current)
            current.lifecycle = "stopping"
            current.stop_disposition = "remove" if by_user else "replace"
            pty = getattr(current, "pty", None)
            current.child_pid = int(getattr(pty, "pid", 0) or getattr(current, "child_pid", 0) or 0)
            current.child_start_token = (
                _process_start_token(current.child_pid)
                or getattr(current, "child_start_token", "")
            )
            try:
                await manifest_save_async(sessions)
            except Exception as e:
                if not getattr(e, "committed", False):
                    restore_persisted_session(current, snapshot)
                    return False, "could not durably record session stop: " + str(e)
            ok, detail = await terminate_session_off_loop(current, by_user=by_user)
            if not ok:
                restore_persisted_session(current, snapshot)
                try:
                    await manifest_save_async(sessions)
                except Exception as restore_error:
                    return False, detail + "; active-state rollback failed: " + str(restore_error)
                return False, detail
            candidate = dict(sessions)
            candidate.pop(name, None)
            try:
                await manifest_save_async(candidate)
            except Exception as e:
                if getattr(e, "committed", False) and sessions.get(name) is current:
                    del sessions[name]
                return False, "process exited but durable tombstone cleanup is unconfirmed: " + str(e)
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

    @state_mutation
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
                previous_snapshot = persisted_session_snapshot(prev)
                prev.lifecycle = "stopping"
                prev.stop_disposition = "replace"
                prev.child_pid = int(getattr(prev, "child_pid", 0) or first.get("childPid", 0) or 0)
                prev.child_start_token = (
                    _process_start_token(prev.child_pid)
                    or getattr(prev, "child_start_token", "")
                )
                try:
                    await manifest_save_async(sessions)
                    tombstoned = True
                except Exception as e:
                    if getattr(e, "committed", False):
                        tombstoned = True
                    else:
                        restore_persisted_session(prev, previous_snapshot)
                        if claim is not existing_claim and claim is not None:
                            claim.release()
                        return None, "could not durably reserve owner replacement: " + str(e)
                ok, detail = await terminate_session_off_loop(
                    prev,
                    by_user=False,
                    release_claim_on_success=not preserve_existing_claim,
                )
                if not ok:
                    restore_persisted_session(prev, previous_snapshot)
                    try:
                        await manifest_save_async(sessions)
                    except Exception as restore_error:
                        detail += "; active-state rollback failed: " + str(restore_error)
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
            owner.child_pid = int(first.get("childPid", 0) or 0)
            owner.child_start_token = _process_start_token(owner.child_pid)
            candidate = dict(sessions)
            candidate[name] = owner
            try:
                await manifest_save_async(candidate)
            except Exception as e:
                if getattr(e, "committed", False):
                    sessions[name] = owner
                    if existing_claim is not None and existing_claim is not claim:
                        existing_claim.release()
                    return None, "visible owner registration committed but durability is unconfirmed: " + str(e)
                if claim is not existing_claim and claim is not None:
                    claim.release()
                if preserve_existing_claim and prev is not None:
                    prev._launch_claim = existing_claim
                    prev.claim_paths = list(existing_claim.paths) if existing_claim is not None else []
                return None, "could not durably register visible owner: " + str(e)
            sessions[name] = owner
            return owner, ""

    @state_mutation
    async def coordinate_session_request(
        first,
        spawn_if_missing,
        leave_unarmed_dormant=False,
        operation_key="",
        operation_fingerprint="",
    ):
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
                prev.operation_key = operation_key
                prev.operation_fingerprint = operation_fingerprint
                prev.operation_created = False
                if claim is not None:
                    prev._launch_claim = claim
                    prev.claim_paths = list(claim.paths)
                if requested_cwd:
                    prev.cwd = requested_cwd
                if first.get("cols"):
                    prev.cols = cols
                    prev.rows = rows
                try:
                    await manifest_save_async()
                except Exception as e:
                    if getattr(e, "committed", False):
                        if old_claim is not None and old_claim is not claim:
                            old_claim.release()
                        return None, "session update committed but durability is unconfirmed: " + str(e), False
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
            replaced_prev = prev
            if candidate_ids and claim is None:
                return None, claim_detail or "could not reserve session launch", False

            if prev:
                previous_snapshot = persisted_session_snapshot(prev)
                prev.lifecycle = "stopping"
                prev.stop_disposition = "replace"
                prev.operation_key = operation_key
                prev.operation_fingerprint = operation_fingerprint
                prev.operation_created = True
                pty = getattr(prev, "pty", None)
                prev.child_pid = int(getattr(pty, "pid", 0) or getattr(prev, "child_pid", 0) or 0)
                prev.child_start_token = (
                    _process_start_token(prev.child_pid)
                    or getattr(prev, "child_start_token", "")
                )
                try:
                    await manifest_save_async(sessions)
                except Exception as e:
                    if not getattr(e, "committed", False):
                        restore_persisted_session(prev, previous_snapshot)
                        if claim is not existing_claim and claim is not None:
                            claim.release()
                        return None, "could not durably reserve session replacement: " + str(e), False
                ok, detail = await terminate_session_off_loop(
                    prev,
                    by_user=False,
                    release_claim_on_success=not preserve_existing_claim,
                )
                if not ok:
                    restore_persisted_session(prev, previous_snapshot)
                    try:
                        await manifest_save_async(sessions)
                    except Exception as restore_error:
                        detail += "; active-state rollback failed: " + str(restore_error)
                    if claim is not existing_claim and claim is not None:
                        claim.release()
                    return None, "previous session did not exit: " + detail, False
                if preserve_existing_claim:
                    prev._launch_claim = None
                    prev.claim_paths = []
                if sessions.get(name) is prev:
                    del sessions[name]

            created = Session(
                name, cmd, cwd, cols, rows, loop, outq, heal=requested_heal, spawn_now=False,
                ids=candidate_ids, session_id=canonical_id, aliases=identity_aliases
            )
            created._launch_claim = claim
            created.claim_paths = list(claim.paths) if claim is not None else []
            created.identity_pending = requested_identity_pending
            created.deaths = prior_deaths
            created.lifecycle = "starting"
            created.operation_key = operation_key
            created.operation_fingerprint = operation_fingerprint
            created.operation_created = True
            candidate = dict(sessions)
            candidate[name] = created
            try:
                await manifest_save_async(candidate)
            except Exception as e:
                if getattr(e, "committed", False):
                    sessions[name] = created
                else:
                    if replaced_prev is not None:
                        sessions[name] = replaced_prev
                    if claim is not None:
                        claim.release()
                return None, "could not durably reserve session start: " + str(e), False

            sessions[name] = created
            try:
                await spawn_session_off_loop(created)
            except Exception as e:
                stopped, stop_detail = await terminate_session_off_loop(created, by_user=False)
                created.lifecycle = "failed" if stopped else "stopping"
                created.stop_disposition = "" if stopped else "remove"
                created.child_pid = int(getattr(created.pty, "pid", 0) or created.child_pid or 0)
                created.child_start_token = (
                    _process_start_token(created.child_pid)
                    or getattr(created, "child_start_token", "")
                )
                created.dead = stopped
                try:
                    await manifest_save_async(sessions)
                except Exception as persist_error:
                    return None, "spawn failed and failure state could not be persisted: " + str(persist_error), False
                return None, "spawn failed: " + str(e) + "; stop=" + stop_detail, False

            created.lifecycle = "active"
            created.child_pid = int(getattr(created.pty, "pid", 0) or 0)
            created.child_start_token = (
                _process_start_token(created.child_pid)
                or getattr(created, "child_start_token", "")
            )
            try:
                await manifest_save_async(sessions)
            except Exception as e:
                if getattr(e, "committed", False):
                    return None, "spawned session is active but manifest durability is unconfirmed: " + str(e), False
                stopped, stop_detail = await terminate_session_off_loop(created, by_user=False)
                created.lifecycle = "failed" if stopped else "stopping"
                created.stop_disposition = "" if stopped else "remove"
                created.child_pid = int(getattr(created.pty, "pid", 0) or created.child_pid or 0)
                created.child_start_token = (
                    _process_start_token(created.child_pid)
                    or getattr(created, "child_start_token", "")
                )
                try:
                    await manifest_save_async(sessions)
                except Exception as persist_error:
                    return None, (
                        "active manifest commit failed; stop=" + stop_detail
                        + "; failure state persistence also failed: " + str(persist_error)
                    ), False
                return None, "could not durably activate spawned session; stop=" + stop_detail, False
            return created, "", True

    @state_mutation
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
                await manifest_save_async()
            except Exception as e:
                if getattr(e, "committed", False):
                    if old_claim is not None and old_claim is not claim:
                        old_claim.release()
                    return None, "identity binding committed but durability is unconfirmed: " + str(e)
                restore_persisted_session(current, snapshot)
                if claim is not old_claim:
                    claim.release()
                return None, "could not durably bind session identity: " + str(e)
            if old_claim is not None and old_claim is not claim:
                old_claim.release()
            return current, ""

    @state_mutation
    async def set_session_heal(name, enabled):
        session = sessions.get(name)
        if session is None:
            return False, "no such session"
        if enabled and getattr(session, "identity_pending", False):
            return False, "fresh identity is pending"
        previous = session.heal
        session.heal = bool(enabled)
        try:
            await manifest_save_async()
        except Exception as error:
            if not getattr(error, "committed", False):
                session.heal = previous
            return False, str(error)
        return True, ""

    @state_mutation
    async def rename_session(name, target):
        session = sessions.get(name)
        if session is None or not target or target in sessions:
            return False, "invalid or conflicting session name"
        candidate = dict(sessions)
        candidate.pop(name)
        candidate[target] = session
        try:
            await manifest_save_async(candidate)
        except Exception as error:
            if getattr(error, "committed", False):
                sessions.pop(name, None)
                session.name = target
                sessions[target] = session
            return False, str(error)
        sessions.pop(name)
        session.name = target
        sessions[target] = session
        return True, ""

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
    start_supervised_background(background_tasks, "loop-monitor", loop_monitor)

    async def conpty_orphan_reaper_tick():
        while True:
            await asyncio.sleep(5)
            with _ORPHANED_CONPTY_LOCK:
                pending = bool(
                    _ORPHANED_CONPTY_HOSTS
                    or _PENDING_CONPTY_BASELINES
                )
            if pending:
                await asyncio.get_running_loop().run_in_executor(
                    None,
                    _reap_orphaned_conpty_hosts,
                )
    start_supervised_background(
        background_tasks,
        "conpty-orphan-reaper",
        conpty_orphan_reaper_tick,
    )

    async def reap_unresolved_processes(restored):
        pids = set()
        if _same_process_instance(restored.child_pid, restored.child_start_token):
            pids.add(restored.child_pid)
        elif restored.child_pid > 0 and _pid_alive(restored.child_pid):
            log(
                f"[boot] ignored unverified persisted pid {restored.child_pid} for {restored.name}; "
                "the process-instance token did not match"
            )
        candidate_ids = launch_candidate_ids(restored.cmd, restored.ids)
        if candidate_ids:
            live = await asyncio.get_running_loop().run_in_executor(None, try_live_session_ids)
            if not live[0]:
                return False, live[2] or "could not inspect live agent processes"
            for session_id in candidate_ids:
                pid = (live[1] or {}).get(session_id.lower())
                if pid and int(pid) not in pids:
                    return False, (
                        f"matching live agent {session_id} has pid {pid}, but muxd cannot prove "
                        "that process belongs to this interrupted lifecycle"
                    )
        for pid in pids:
            ok, detail = await asyncio.get_running_loop().run_in_executor(
                None, lambda target=pid: _terminate_pid_tree(target)
            )
            if not ok:
                return False, detail
        return True, "unresolved child processes are stopped"

    # boot policy (user-specified): agents NEVER auto-start on a fresh boot unless the session was
    # ARMED (auto-resume on). Armed -> recreate + resume now. Unarmed -> a dead placeholder tab that
    # stays dormant until an explicit create/relaunch sends a non-empty resume command.
    # liveness runs a PowerShell CIM query (blocking) — NEVER call it on the event-loop thread or muxd
    # freezes and hosted sessions drop. Always hop to a worker thread.
    boot_manifest = manifest_load()
    boot_names = []
    for name, m in boot_manifest.items():
        if not strict_mux_name(name) or name in sessions:
            continue
        heal = bool(m.get("heal"))
        mcmd = m.get("cmd", "")
        ids = m.get("ids") if isinstance(m.get("ids"), list) else []
        aliases = m.get("aliases") if isinstance(m.get("aliases"), list) else []
        try:
            restored = Session(
                name, mcmd, m.get("cwd", ""), m.get("cols", 140), m.get("rows", 40),
                loop, outq, heal=heal, spawn_now=False, ids=ids,
                session_id=m.get("sessionId", ""), aliases=aliases
            )
            restored.expected_owner = bool(m.get("owner"))
            restored.owner_key = str(m.get("ownerKey", "") or "")
            restored.identity_pending = bool(m.get("identityPending"))
            restored.lifecycle = str(m.get("lifecycle", "active") or "active")
            if restored.lifecycle not in ("active", "dormant", "starting", "stopping", "failed"):
                restored.lifecycle = "failed"
            restored.child_pid = int(m.get("childPid", 0) or 0)
            restored.child_start_token = str(m.get("childStartToken", "") or "")
            restored.stop_disposition = str(m.get("stopDisposition", "") or "")
            if restored.stop_disposition not in ("", "remove", "replace"):
                restored.stop_disposition = ""
            restored.user_killed = bool(m.get("userKilled", False))
            restored.generation_id = str(m.get("generationId", "") or restored.generation_id)
            restored.operation_key = str(m.get("operationKey", "") or "")
            restored.operation_fingerprint = str(m.get("operationFingerprint", "") or "")
            restored.operation_created = bool(m.get("operationCreated", False))
            restored.deaths = [
                float(value)
                for value in (m.get("deaths") if isinstance(m.get("deaths"), list) else [])
                if isinstance(value, (int, float))
            ][-16:]
        except Exception as error:
            raise RuntimeError(f"could not restore durable session {name}") from error
        sessions[name] = restored
        boot_names.append(name)

    # Reconcile only after every durable record is represented in memory. Any save below is therefore
    # authoritative for the whole manifest and cannot erase entries that happened to sort later.
    for name in boot_names:
        restored = sessions.get(name)
        if restored is None:
            continue
        m = boot_manifest[name]
        heal = bool(m.get("heal"))
        ids = m.get("ids") if isinstance(m.get("ids"), list) else []
        try:
            if restored.user_killed or (
                not restored.expected_owner
                and restored.lifecycle in ("active", "starting", "stopping")
            ):
                interrupted_lifecycle = restored.lifecycle
                reaped, detail = await reap_unresolved_processes(restored)
                if not reaped:
                    raise RuntimeError(
                        f"could not reconcile unresolved {restored.lifecycle} session {name}: {detail}"
                    )
                remove_record = restored.user_killed or (
                    restored.lifecycle == "stopping"
                    and restored.stop_disposition == "remove"
                )
                if remove_record:
                    sessions.pop(name, None)
                    await manifest_save_async(sessions)
                    log(f"[boot] completed durable stop intent for {name}")
                    continue
                restored.lifecycle = "failed" if restored.lifecycle == "starting" else "dormant"
                restored.child_pid = 0
                restored.child_start_token = ""
                restored.stop_disposition = ""
                restored.user_killed = False
                await manifest_save_async(sessions)
                log(f"[boot] reconciled unresolved lifecycle for {name}; left dormant")
                if interrupted_lifecycle != "active":
                    continue
            if restored.lifecycle == "failed":
                log(f"[boot] failed lifecycle for {name}; left dormant")
                continue
            if restored.expected_owner:
                log(f"[boot] waiting for visible owner reconnect: {name}")
                continue
            if restored.identity_pending:
                log(f"[boot] fresh identity was not captured for {name}; left dormant")
                continue
            if heal and not restored.user_killed:
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
        except Exception as e:
            log(f"[boot] {name} failed: {e}")

    async def self_heal_tick():
        # a session whose SHELL died (pty EOF) is useless — recreate + re-run its resume (max 3/10min).
        while True:
            await asyncio.sleep(15)
            for s in list(sessions.values()):
                if (
                    s.dead
                    and not s.user_killed
                    and getattr(s, "lifecycle", "active") in ("active", "dormant")
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
    start_supervised_background(background_tasks, "self-heal", self_heal_tick)

    async def flush_out():
        # coalesce each session's output into ONE ws frame per ~12ms tick — far fewer frames/less b64+JSON
        # overhead, smoother phone rendering, and a natural place to add backpressure.
        while True:
            await asyncio.sleep(0.012)
            for s in list(sessions.values()):
                chunk = s.drain()
                if chunk:
                    outq.put_nowait(("o", s.name, chunk))
                    fanout_local_output(s, chunk)
    start_supervised_background(background_tasks, "output-flush", flush_out)

    # ---- LOCAL attach server (muxctl): loopback-only, no token — a raw console client streams a session
    # exactly like a web viewer, so you get `tmux attach`-style parity from a Windows Terminal on the PC.
    async def local_serve():
        import websockets as _ws
        async def ensure_local_session(first, spawn_if_missing):
            if not spawn_if_missing:
                name = SAFE(first.get("s", ""))
                if not name:
                    return None, "session name required", False
                session = sessions.get(name)
                if session is None:
                    return None, "no such session: " + name, False
                return session, "", False
            return await coordinate_session_request(first, spawn_if_missing)

        async def handler(ws):
            headers = getattr(ws, "request_headers", None)
            if headers is None:
                headers = getattr(getattr(ws, "request", None), "headers", None)
            if headers is not None and headers.get("Origin"):
                log("[local] rejected browser-origin websocket")
                await ws.close(code=1008, reason="browser origins are not allowed")
                return
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
                            elif mt == "child":
                                child_pid = int(m.get("pid", 0) or 0)
                                if child_pid <= 0:
                                    continue
                                owner.child_pid = child_pid
                                owner.child_start_token = _process_start_token(child_pid)
                                await manifest_save_async(sessions)
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
                    result = await coordinate_create_intent(first, "local", True)
                    if not result.get("ok"):
                        await ws.send(json.dumps({"t": "err", "m": result.get("detail", "create failed")})); return
                    session = result.get("session") or {}
                    await ws.send(json.dumps({
                        "t": "created",
                        "s": session.get("name", strict_mux_name(first.get("s", ""))),
                        "created": bool(result.get("created")),
                        "alive": bool(session.get("alive")),
                    })); return
                if first.get("t") == "input":
                    name = SAFE(first.get("s", ""))
                    s = sessions.get(name)
                    try:
                        data = base64.b64decode(first.get("d", ""), validate=True)
                    except Exception:
                        await ws.send(json.dumps({"t": "err", "m": "invalid input payload"})); return
                    if not data:
                        await ws.send(json.dumps({"t": "err", "m": "input payload is empty"})); return
                    result = await execute_input_intent(first, s, data)
                    await ws.send(json.dumps(result)); return
                if first.get("t") == "open":
                    s, err, _created = await ensure_local_session(first, True)
                else:
                    s, err, _created = await ensure_local_session(first, False)
                if err:
                    await ws.send(json.dumps({"t": "err", "m": err})); return
                lq = asyncio.Queue(maxsize=LOCAL_VIEWER_QUEUE_MAX); s.local.add(lq)
                if first.get("cols"):
                    update_local_session_size(s, lq, first.get("cols"), first.get("rows") or 40)
                try:
                    sb_limit = int(first.get("sb") if first.get("sb") is not None else LOCAL_SB_SEND)
                    replay = await asyncio.get_running_loop().run_in_executor(
                        None, lambda: attach_replay_payload(s, sb_limit)
                    )
                    if replay:
                        await ws.send(replay)
                    # Guarantee a full frame for an alt-screen TUI attached locally.
                    await asyncio.get_running_loop().run_in_executor(None, redraw_nudge, s)
                    async def pump():
                        while True:
                            data = await lq.get()
                            if data is LOCAL_VIEWER_SLOW:
                                await ws.close(code=1013, reason="local viewer is not draining output")
                                return
                            await ws.send(data)
                    pt = asyncio.create_task(pump())
                    try:
                        async for raw in ws:
                            if isinstance(raw, (bytes, bytearray)):
                                s.write(bytes(raw)); continue
                            try: m = json.loads(raw)
                            except Exception: continue
                            if m.get("t") == "i": s.write(base64.b64decode(m.get("d", "")))
                            elif m.get("t") == "resize":
                                update_local_session_size(
                                    s, lq, m.get("cols", 140), m.get("rows", 40)
                                )
                    finally: pt.cancel()
                finally:
                    s.local.discard(lq)
                    s.local_sizes.pop(lq, None)
                    if not s.remote_size_active:
                        restore_local_session_size(s)
            except _ws.exceptions.ConnectionClosedOK:
                pass
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
    start_supervised_background(background_tasks, "local-server", local_serve)

    import websockets
    backoff = 1
    while True:
        url = None
        for cand in RELAYS:
            url = cand + ("&" if "?" in cand else "?") + "token=" + TOKEN
            try:
                tls_context = await relay_tls_context(url)
                async with websockets.connect(
                    url,
                    ssl=tls_context,
                    max_size=8_000_000,
                    ping_interval=20,
                    ping_timeout=15,
                    open_timeout=8,
                ) as ws:
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
                                result = await coordinate_create_intent(
                                    m, "relay", True, leave_unarmed_dormant=True
                                )
                                if not result.get("ok"):
                                    log(f"[{name}] create REFUSED: {result.get('detail', 'create failed')}")
                                    await ws.send(json.dumps({
                                        "t": "createResult",
                                        "rid": request_id,
                                        "s": name,
                                        "ok": False,
                                        "created": False,
                                        "detail": result.get("detail", "muxd refused the create request"),
                                        "retryable": bool(result.get("retryable")),
                                    }))
                                else:
                                    await ws.send(json.dumps({
                                        "t": "createResult",
                                        "rid": request_id,
                                        "s": name,
                                        "ok": True,
                                        "created": bool(result.get("created")),
                                        "detail": "",
                                        "retryable": False,
                                        "session": result.get("session") or {},
                                    }))
                                continue
                            elif t == "heal" and name in sessions:
                                healed, detail = await set_session_heal(name, bool(m.get("on")))
                                if not healed and detail == "fresh identity is pending":
                                    log(f"[{name}] refused auto-resume while fresh identity is pending")
                                elif not healed:
                                    log(f"[{name}] heal persistence failed: {detail}")
                                    await ws.send(json.dumps({
                                        "t": "sessions",
                                        "list": sess_list(),
                                        "notice": "auto-resume policy was not persisted",
                                    }))
                            elif t == "i" and name in sessions:
                                sessions[name].write(base64.b64decode(m.get("d", "")))
                            elif t == "resize" and name in sessions:
                                apply_remote_session_size(sessions[name], m)
                            elif t == "sb" and name in sessions:
                                session = sessions[name]
                                scrollback_limit = m.get("max", SB_SEND)
                                scrollback_request_id = str(m.get("rid", "") or "")
                                encoded = await asyncio.get_running_loop().run_in_executor(
                                    None,
                                    lambda current=session, limit=scrollback_limit: base64.b64encode(
                                        current.scrollback(limit)
                                    ).decode(),
                                )
                                await ws.send(json.dumps({
                                    "t": "sb",
                                    "s": name,
                                    "rid": scrollback_request_id,
                                    "d": encoded,
                                }))
                                # Byte replay can't rebuild a full-screen TUI on its own; nudge the app to
                                # emit an authoritative full frame right after the replay.
                                await asyncio.get_running_loop().run_in_executor(None, redraw_nudge, session)
                            elif t == "rename" and name in sessions:
                                to = strict_mux_name(m.get("to", ""))
                                if to and to not in sessions:
                                    renamed, detail = await rename_session(name, to)
                                    if not renamed:
                                        log(f"[{name}] rename persistence failed: {detail}")
                                        await ws.send(json.dumps({
                                            "t": "sessions",
                                            "list": sess_list(),
                                            "notice": "session rename durability is unconfirmed",
                                        }))
                                        continue
                                    await ws.send(json.dumps({"t": "sessions", "list": sess_list()}))
                            elif t == "tail" and name in sessions:
                                session = sessions[name]
                                lines = int(m.get("lines") or 40)
                                tail = await asyncio.get_running_loop().run_in_executor(
                                    None,
                                    lambda current=session, count=lines: current.tail_text(
                                        nbytes=200000,
                                        lines=count,
                                    ),
                                )
                                await ws.send(json.dumps({"t": "tailr", "rid": m.get("rid", ""), "text": tail}))
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
                        clear_remote_size_ownership()
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
