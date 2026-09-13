"""First-class muxd profile contract.

The production profile intentionally preserves the historical defaults. Every other profile is
explicit: it must name every resource which can cause cross-instance custody (state, ports, mutex,
identity, relay credentials, guardian inputs, launch claims, and scheduled tasks).  This module is
imported by every muxd entrypoint so a process cannot accidentally use a production fallback.
"""

from __future__ import annotations

import json
import sys
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping
from urllib.parse import urlsplit


PRODUCTION = "production"
PRODUCTION_CONTROL_PORT = 7699
PRODUCTION_GUARDIAN_PORT = 7690
PRODUCTION_MUTEX = r"Local\CodexMuxdSessionHost"
PRODUCTION_TASK = "MuxdSessionHost"
PRODUCTION_RESTART_TASK = "MuxdSessionHostRestart"
PRODUCTION_WATCHDOG_TASK = "MuxdSessionHostWatchdog"


class ProfileError(RuntimeError):
    """Raised when a profile is incomplete or would overlap production."""


def _env_file(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    try:
        with path.open(encoding="utf-8") as stream:
            for line in stream:
                line = line.strip()
                if line and not line.startswith("#") and "=" in line:
                    key, value = line.split("=", 1)
                    values[key.strip()] = value.strip()
    except OSError as error:
        raise ProfileError(f"profile env file cannot be read: {path}: {error}") from error
    return values


def _absolute(value: str, label: str) -> str:
    if not value:
        raise ProfileError(f"non-production profile requires {label}")
    path = Path(value).expanduser()
    if not path.is_absolute():
        raise ProfileError(f"non-production profile requires an absolute {label}: {value}")
    return str(path.resolve())


def _same_path(left: str, right: str) -> bool:
    return os.path.normcase(os.path.abspath(left)) == os.path.normcase(os.path.abspath(right))


def _overlaps_path(left: str, right: str) -> bool:
    """Return true when either path is equal to or nested under the other."""
    try:
        left = os.path.normcase(os.path.abspath(left))
        right = os.path.normcase(os.path.abspath(right))
        common = os.path.normcase(os.path.commonpath((left, right)))
        return common == left or common == right
    except ValueError:
        return False


def _port(value: str, label: str) -> int:
    try:
        result = int(value)
    except (TypeError, ValueError) as error:
        raise ProfileError(f"{label} must be an integer TCP port") from error
    if not 1 <= result <= 65535:
        raise ProfileError(f"{label} must be between 1 and 65535")
    return result


def _required(values: Mapping[str, str], key: str) -> str:
    value = str(values.get(key, "") or "").strip()
    if not value:
        raise ProfileError(f"non-production profile requires {key}")
    return value


def _production_host(env: Mapping[str, str]) -> str:
    return str(env.get("COMPUTERNAME", "") or "pc").strip() or "pc"


def profile_matches_command(command_line: str, profile: MuxdProfile | None = None) -> bool:
    """Return true only for a command carrying this profile and its runtime root."""
    active = PROFILE if profile is None else profile
    text = str(command_line or "")
    tokens = re.findall(r'"([^"\r\n]*)"|([^\s"]+)', text)
    args = [quoted or bare for quoted, bare in tokens]
    profile_switches = [index for index, arg in enumerate(args) if arg.lower() == "--profile"]
    if len(profile_switches) != 1:
        return False
    index = profile_switches[0]
    if index + 1 >= len(args) or args[index + 1] != active.name:
        return False
    # The entrypoint must be the script argument, not a path embedded in an unrelated argument.
    script = str(Path(active.runtime_root) / "muxd.py")
    return len(args) > 1 and _same_path(args[1], script)


@dataclass(frozen=True)
class MuxdProfile:
    name: str
    runtime_root: str
    env_file: str
    state_root: str
    control_port: int
    mutex_name: str
    principal_registry: str
    principal_instance_id: str
    guardian_enabled: bool
    guardian_dir: str
    guardian_lock_port: int | None
    transcript_claude_root: str
    transcript_codex_root: str
    host_identity: str
    relay_urls: tuple[str, ...]
    token_source: str
    claim_root: str
    task_name: str
    restart_task_name: str
    watchdog_task_name: str
    default_cwd: str
    env: dict[str, str]

    @property
    def control_url(self) -> str:
        return f"ws://127.0.0.1:{self.control_port}"

    @property
    def guardian_script(self) -> str:
        return str(Path(self.runtime_root) / "transcript_guardian.py")

    def resolve_token(self) -> str:
        source = self.token_source
        if source.startswith("env:"):
            variable = source[4:].strip()
            if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", variable):
                raise ProfileError("token source has an invalid environment variable name")
            token = os.environ.get(variable, "")
        elif source.startswith("file:"):
            path = Path(source[5:])
            try:
                token = path.read_text(encoding="utf-8").strip()
            except OSError as error:
                raise ProfileError(f"token source cannot be read: {path}: {error}") from error
        elif source.startswith("env-file:"):
            token = self.env.get(source[9:].strip(), "")
        else:
            raise ProfileError("token source must be env:NAME, env-file:NAME, or file:/absolute/path")
        if not token:
            raise ProfileError(f"token source did not provide a token: {source.split(':', 1)[0]}")
        return token

    def as_json(self) -> dict[str, object]:
        return {
            "Name": self.name,
            "RuntimeRoot": self.runtime_root,
            "EnvFile": self.env_file,
            "StateRoot": self.state_root,
            "ControlPort": self.control_port,
            "MutexName": self.mutex_name,
            "PrincipalRegistry": self.principal_registry,
            "PrincipalInstanceId": self.principal_instance_id,
            "GuardianEnabled": self.guardian_enabled,
            "GuardianDir": self.guardian_dir,
            "GuardianLockPort": self.guardian_lock_port,
            "TranscriptClaudeRoot": self.transcript_claude_root,
            "TranscriptCodexRoot": self.transcript_codex_root,
            "HostIdentity": self.host_identity,
            "RelayUrls": list(self.relay_urls),
            "TokenSource": self.token_source,
            "ClaimRoot": self.claim_root,
            "TaskName": self.task_name,
            "RestartTaskName": self.restart_task_name,
            "WatchdogTaskName": self.watchdog_task_name,
            "DefaultCwd": self.default_cwd,
        }


def load_profile(source: Mapping[str, str] | None = None) -> MuxdProfile:
    process = dict(os.environ if source is None else source)
    argv = list(sys.argv)
    cli_profile = argv[argv.index("--profile") + 1] if "--profile" in argv and argv.index("--profile") + 1 < len(argv) else ""
    name = str(process.get("MUXD_PROFILE") or cli_profile or PRODUCTION).strip()
    if not re.fullmatch(r"[A-Za-z0-9._-]+", name):
        raise ProfileError("MUXD_PROFILE has invalid characters")

    home = Path.home()
    production_runtime = str(home / "muxd")
    production_env = str(Path(production_runtime) / "muxd.env")
    file_hint = process.get("MUXD_ENV_FILE", "")
    if name == PRODUCTION:
        runtime_root = production_runtime
        env_file = production_env
    else:
        # These two roots locate the profile before its env file can be consulted. They may not be
        # inherited from production or inferred from HOME.
        runtime_root = _absolute(process.get("MUXD_RUNTIME_ROOT", ""), "MUXD_RUNTIME_ROOT")
        env_file = _absolute(file_hint, "MUXD_ENV_FILE")
        if _overlaps_path(runtime_root, production_runtime):
            raise ProfileError("non-production MUXD_RUNTIME_ROOT overlaps production muxd root")
        if _overlaps_path(env_file, production_env):
            raise ProfileError("non-production MUXD_ENV_FILE overlaps production muxd.env")

    if name == PRODUCTION and not Path(env_file).is_file():
        file_values = {}
    else:
        file_values = _env_file(Path(env_file))
    values = dict(file_values)
    values.update(process)

    if name == PRODUCTION:
        state_root = production_runtime
        control_port = _port(
            process.get("MUXCTL_PORT") or values.get("LOCAL_PORT", str(PRODUCTION_CONTROL_PORT)),
            "production control port",
        )
        mutex_name = process.get("INSTANCE_MUTEX_NAME", PRODUCTION_MUTEX)
        principal_registry = str(
            values.get("PRINCIPAL_REGISTRY", str(Path(production_runtime) / "principal-registry.dpapi"))
        )
        guardian_enabled = True
        guardian_dir = str(
            process.get("GUARDIAN_DIR")
            or values.get(
                "GUARDIAN_DIR",
                str((Path(os.environ.get("LOCALAPPDATA", "")) if os.environ.get("LOCALAPPDATA") else home / "AppData" / "Local")
                    / "CodexLocalRetrieval" / "transcript-guardian"),
            )
        )
        guardian_port = PRODUCTION_GUARDIAN_PORT
        claude_root = str(home / ".claude" / "projects")
        codex_root = str(home / ".codex" / "sessions")
        host_identity = _production_host(process)
        relay_urls = tuple(
            url for url in (values.get("RELAY_LAN", ""), values.get("RELAY_PUBLIC", "")) if url
        )
        token_source = "env:MUX_HOST_TOKEN" if process.get("MUX_HOST_TOKEN") else "env-file:MUX_HOST_TOKEN"
        claim_root = str(
            values.get(
                "LAUNCH_CLAIM_ROOT",
                str((Path(os.environ.get("LOCALAPPDATA", "")) if os.environ.get("LOCALAPPDATA") else Path(os.getenv("TEMP", "/tmp")))
                    / "CodexLocalRetrieval" / "launch-claims"),
            )
        )
        task = str(process.get("MUXD_TASK", PRODUCTION_TASK))
        restart_task = PRODUCTION_RESTART_TASK
        watchdog_task = PRODUCTION_WATCHDOG_TASK
        default_cwd = str(values.get("DEFAULT_CWD", r"Z:\328\CMPUT328-A2\codexworks\301"))
        return MuxdProfile(
            name, runtime_root, env_file, state_root, control_port, mutex_name,
            principal_registry, _production_host(process), guardian_enabled, guardian_dir, guardian_port,
            claude_root, codex_root, host_identity, relay_urls, token_source,
            claim_root, task, restart_task, watchdog_task, default_cwd, file_values,
        )

    required = {
        "MUXD_STATE_ROOT": "state root",
        "MUXD_CONTROL_PORT": "control port",
        "MUXD_MUTEX_NAME": "mutex",
        "MUXD_PRINCIPAL_REGISTRY": "principal registry",
        "MUXD_PRINCIPAL_INSTANCE_ID": "principal instance identity",
        "MUXD_HOST_IDENTITY": "host identity",
        "MUXD_RELAY_URLS": "relay URLs",
        "MUXD_TOKEN_SOURCE": "token source",
        "MUXD_LAUNCH_CLAIM_ROOT": "launch-claim root",
        "MUXD_TASK_NAME": "muxd task name",
        "MUXD_RESTART_TASK_NAME": "restart task name",
        "MUXD_WATCHDOG_TASK_NAME": "watchdog task name",
        "DEFAULT_CWD": "default working directory",
    }
    for key, label in required.items():
        _required(values, key)

    state_root = _absolute(values["MUXD_STATE_ROOT"], "MUXD_STATE_ROOT")
    principal_registry = _absolute(values["MUXD_PRINCIPAL_REGISTRY"], "MUXD_PRINCIPAL_REGISTRY")
    principal_instance_id = _required(values, "MUXD_PRINCIPAL_INSTANCE_ID")
    if principal_instance_id == _production_host(process):
        raise ProfileError("non-production MUXD_PRINCIPAL_INSTANCE_ID overlaps production identity")
    claim_root = _absolute(values["MUXD_LAUNCH_CLAIM_ROOT"], "MUXD_LAUNCH_CLAIM_ROOT")
    production_principal = str(Path(production_runtime) / "principal-registry.dpapi")
    production_claim = str(
        (Path(os.environ.get("LOCALAPPDATA", "")) if os.environ.get("LOCALAPPDATA") else Path(os.getenv("TEMP", "/tmp")))
        / "CodexLocalRetrieval" / "launch-claims"
    )
    for label, path in (
        ("MUXD_STATE_ROOT", state_root),
        ("MUXD_PRINCIPAL_REGISTRY", principal_registry),
        ("MUXD_LAUNCH_CLAIM_ROOT", claim_root),
    ):
        if any(_overlaps_path(path, production) for production in (
            production_runtime,
            production_principal,
            production_claim,
        )):
            raise ProfileError(f"non-production {label} overlaps a production root")

    control_port = _port(values["MUXD_CONTROL_PORT"], "MUXD_CONTROL_PORT")
    if control_port == PRODUCTION_CONTROL_PORT:
        raise ProfileError("non-production MUXD_CONTROL_PORT overlaps production")
    mutex_name = _required(values, "MUXD_MUTEX_NAME")
    if mutex_name == PRODUCTION_MUTEX:
        raise ProfileError("non-production MUXD_MUTEX_NAME overlaps production")
    host_identity = _required(values, "MUXD_HOST_IDENTITY")
    if host_identity == _production_host(process):
        raise ProfileError("non-production MUXD_HOST_IDENTITY overlaps production host identity")
    relays = tuple(part.strip() for part in re.split(r"[,;\r\n]+", values["MUXD_RELAY_URLS"]) if part.strip())
    if not relays:
        raise ProfileError("MUXD_RELAY_URLS must contain at least one URL")
    for relay in relays:
        parsed = urlsplit(relay)
        if parsed.scheme not in {"ws", "wss"} or not parsed.hostname or parsed.username or parsed.password:
            raise ProfileError("MUXD_RELAY_URLS requires websocket URLs without credentials")
        if parsed.port == 7682 or parsed.path.rstrip("/") in {"/multiplex", "/multiplex/host", "/multiplex/host-link"}:
            raise ProfileError("non-production relay URL overlaps production")
    token_source = _required(values, "MUXD_TOKEN_SOURCE")
    if not (token_source.startswith("env:") or token_source.startswith("file:")):
        raise ProfileError("MUXD_TOKEN_SOURCE must be env:NAME or file:/absolute/path")

    guardian_disabled = str(values.get("MUXD_GUARDIAN_DISABLED", "") or "").strip().lower() in {"1", "true", "yes", "on"}
    if guardian_disabled:
        guardian_dir = ""
        guardian_port = None
        claude_root = ""
        codex_root = ""
    else:
        guardian_dir = _absolute(_required(values, "MUXD_GUARDIAN_DIR"), "MUXD_GUARDIAN_DIR")
        guardian_port = _port(_required(values, "MUXD_GUARDIAN_LOCK_PORT"), "MUXD_GUARDIAN_LOCK_PORT")
        if guardian_port == PRODUCTION_GUARDIAN_PORT:
            raise ProfileError("non-production guardian lock port overlaps production")
        claude_root = _absolute(_required(values, "MUXD_TRANSCRIPT_CLAUDE_ROOT"), "MUXD_TRANSCRIPT_CLAUDE_ROOT")
        codex_root = _absolute(_required(values, "MUXD_TRANSCRIPT_CODEX_ROOT"), "MUXD_TRANSCRIPT_CODEX_ROOT")
        production_guardian = str(
            (Path(os.environ.get("LOCALAPPDATA", "")) if os.environ.get("LOCALAPPDATA") else home / "AppData" / "Local")
            / "CodexLocalRetrieval" / "transcript-guardian"
        )
        production_claude = str(home / ".claude" / "projects")
        production_codex = str(home / ".codex" / "sessions")
        for label, path, production in (
            ("MUXD_GUARDIAN_DIR", guardian_dir, production_guardian),
            ("MUXD_TRANSCRIPT_CLAUDE_ROOT", claude_root, production_claude),
            ("MUXD_TRANSCRIPT_CODEX_ROOT", codex_root, production_codex),
        ):
            if _overlaps_path(path, production):
                raise ProfileError(f"non-production {label} overlaps a production root")

    task = _required(values, "MUXD_TASK_NAME")
    restart_task = _required(values, "MUXD_RESTART_TASK_NAME")
    watchdog_task = _required(values, "MUXD_WATCHDOG_TASK_NAME")
    default_cwd = _absolute(values["DEFAULT_CWD"], "DEFAULT_CWD")
    if _overlaps_path(default_cwd, production_runtime):
        raise ProfileError("non-production DEFAULT_CWD overlaps production muxd root")
    task_names = {value.casefold() for value in (task, restart_task, watchdog_task)}
    if len(task_names) != 3:
        raise ProfileError("non-production task names must be distinct")
    if task_names & {value.casefold() for value in (PRODUCTION_TASK, PRODUCTION_RESTART_TASK, PRODUCTION_WATCHDOG_TASK)}:
        raise ProfileError("non-production task names overlap production")

    return MuxdProfile(
        name, runtime_root, env_file, state_root, control_port, mutex_name,
        principal_registry, principal_instance_id, not guardian_disabled, guardian_dir, guardian_port,
        claude_root, codex_root, host_identity, relays, token_source,
        claim_root, task, restart_task, watchdog_task, default_cwd, file_values,
    )


PROFILE = load_profile()


if __name__ == "__main__":
    if sys.argv[1:] == ["--matching-pids"]:
        processes = json.load(sys.stdin)
        if not isinstance(processes, list):
            raise SystemExit("process input must be an array")
        print(json.dumps([p["ProcessId"] for p in processes
                          if isinstance(p, dict) and isinstance(p.get("ProcessId"), int)
                          and p["ProcessId"] > 0 and profile_matches_command(p.get("CommandLine", ""))]))
    elif sys.argv[1:] == ["--json"]:
        print(json.dumps(PROFILE.as_json(), separators=(",", ":")))
    else:
        raise SystemExit("usage: profile.py --json | --matching-pids")
