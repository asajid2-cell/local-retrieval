#!/usr/bin/env bash
# Off-box backup of the multiplex relay's durable state.
#
# The relay keeps its whole recoverable identity in a handful of JSON files under
# STATE_DIR (relay/server.js: MUX_STATE_DIR || __dirname), each with a .bak sibling
# written by durable-state.js. If the VPS dies, those files die with it. This script
# tars exactly those files and sends the archive over sftp to the PC, using the
# existing 'win' route (the alias the VPS already uses to reach the PC host).
#
#   backup-state.sh                                  # tar + sftp to win:~/mux-relay-backups
#   backup-state.sh --verify                         # ...and prove a restore roundtrip first
#   backup-state.sh --dry-run-to /tmp/out            # build the archive locally, never touch the network
#   backup-state.sh --dry-run-to /tmp/out --verify   # both
#
# TRUST BOUNDARY — read before widening the allowlist below.
# This archive deliberately carries ONLY relay-tier state. It never carries muxd
# identity keys, client private keys, local-control credentials, principal
# registries, or session ACL state; those live outside the relay's state dir and
# are additionally refused by name here. What it DOES carry inherits the relay's
# exposure profile: plaintext transcript references, archive indexes, signed
# intent envelopes, upload metadata, and timing. The backup destination is
# therefore as sensitive as the relay itself and must be treated that way.
# See relay/tests/fixtures/backup-state/EXPOSURE.md.

set -euo pipefail

SELF="$(basename -- "$0")"
SCRIPT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

# --- the allowlist -----------------------------------------------------------
# Exactly the relay's durable state. Each entry is backed up together with its
# "<name>.bak" sibling, because durableJsonLoad() falls back to the sibling when
# the primary is torn — a backup without the .bak files is only half a backup.
STATE_FILES=(
  projects.json
  app-commands.json
  pins.json
  uploads-meta.json
  rename-intents.json
)

# --- the refusal list --------------------------------------------------------
# Second, independent assertion. The allowlist above already excludes everything
# else; this exists so that a future edit widening the allowlist cannot quietly
# start shipping secrets off-box. None of the allowlisted names match these.
DENY_SUBSTRINGS=(
  identity principal acl private secret cred token
  password passphrase authorized_keys known_hosts
)
DENY_SUFFIXES=(.key .pem .jwk .p12 .pfx .asc .gpg)

STATE_DIR_ARG=""
DRY_RUN_TO=""
VERIFY=0
SSH_HOST="win"
# ssh resolves its per-user config - and the default IdentityFile/UserKnownHostsFile that hang off
# it - from the account's PASSWD home, not from $HOME. svc-multiplex's passwd home is
# /home/svc-multiplex and does not exist, so ssh read no user config at all: the `Host win` alias
# never applied and the run died on "Could not resolve hostname win". mux-relay-backup.service also
# sets ProtectHome=yes, so the route can never move under /home. Name the file explicitly.
SSH_CONFIG="/var/lib/multiplex/.ssh/config"
REMOTE_DIR="mux-relay-backups"
ARCHIVE_NAME=""

die() { printf '%s: %s\n' "$SELF" "$*" >&2; exit 1; }
note() { printf '%s: %s\n' "$SELF" "$*"; }

usage() {
  sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'
  cat <<'EOF'

Options:
  --state-dir <dir>     Relay state directory. Resolved against the CURRENT working
                        directory. Default: $MUX_STATE_DIR, else <repo>/relay.
  --dry-run-to <dir>    Write the archive into <dir> and skip the network entirely. The name is
                        fixed (mux-relay-state-dryrun.tgz) so repeat runs overwrite in
                        place rather than piling up; nothing else in <dir> is touched.
  --verify              Extract the freshly built archive into a temp dir, check every
                        file against its recorded sha256, byte-compare it with the live
                        state file, and re-run the refusal sweep. Fails the run on any
                        mismatch. On a real run this happens BEFORE the archive is sent.
  --ssh-host <host>     sftp destination alias (default: win).
  --remote-dir <dir>    Remote directory, relative to the remote home (default: mux-relay-backups).
  --archive-name <name> Override the archive filename.
  -h, --help            This text.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --state-dir)    [ $# -ge 2 ] || die "--state-dir needs a value";    STATE_DIR_ARG="$2"; shift 2 ;;
    --dry-run-to)   [ $# -ge 2 ] || die "--dry-run-to needs a value";   DRY_RUN_TO="$2";    shift 2 ;;
    --ssh-host)     [ $# -ge 2 ] || die "--ssh-host needs a value";     SSH_HOST="$2";      shift 2 ;;
    --remote-dir)   [ $# -ge 2 ] || die "--remote-dir needs a value";   REMOTE_DIR="$2";    shift 2 ;;
    --archive-name) [ $# -ge 2 ] || die "--archive-name needs a value"; ARCHIVE_NAME="$2";  shift 2 ;;
    --verify)       VERIFY=1; shift ;;
    -h|--help)      usage; exit 0 ;;
    *)              die "unknown argument: $1 (try --help)" ;;
  esac
done

if command -v sha256sum >/dev/null 2>&1; then
  SHA=(sha256sum)
elif command -v shasum >/dev/null 2>&1; then
  SHA=(shasum -a 256)
else
  die "need sha256sum or shasum on PATH to build a checkable manifest"
fi

# Resolve the state dir against the invocation cwd. Do NOT cd first: callers pass
# paths relative to where they stand (the verifier runs from relay/).
if [ -n "$STATE_DIR_ARG" ]; then
  RAW_STATE_DIR="$STATE_DIR_ARG"
elif [ -n "${MUX_STATE_DIR:-}" ]; then
  RAW_STATE_DIR="$MUX_STATE_DIR"
else
  RAW_STATE_DIR="$REPO_ROOT/relay"
fi
[ -d "$RAW_STATE_DIR" ] || die "state dir does not exist: $RAW_STATE_DIR"
STATE_DIR="$(cd -- "$RAW_STATE_DIR" && pwd)"

# deny_reason <basename> -> prints why the file is refused, exit 0; exit 1 if allowed.
deny_reason() {
  local lower="$1" s
  lower="$(printf '%s' "$lower" | tr '[:upper:]' '[:lower:]')"
  for s in "${DENY_SUBSTRINGS[@]}"; do
    case "$lower" in *"$s"*) printf 'name contains %q' "$s"; return 0 ;; esac
  done
  for s in "${DENY_SUFFIXES[@]}"; do
    case "$lower" in *"$s") printf 'secret-bearing extension %q' "$s"; return 0 ;; esac
  done
  return 1
}

# refuse_secrets <dir> — sweep a whole tree and abort on anything that looks like a key.
refuse_secrets() {
  local root="$1" file base reason
  while IFS= read -r file; do
    base="$(basename -- "$file")"
    if reason="$(deny_reason "$base")"; then
      die "refusing to include $base ($reason) — relay backups never carry keys, credentials, principal registries or ACL state"
    fi
  done < <(find "$root" -type f)
}

STAGE=""
cleanup() { [ -n "$STAGE" ] && rm -rf -- "$STAGE" || true; }
trap cleanup EXIT

STAGE="$(mktemp -d "${TMPDIR:-/tmp}/mux-backup-XXXXXX")"
PAYLOAD="$STAGE/relay-state"
mkdir -p -- "$PAYLOAD"

MEMBERS=()
for name in "${STATE_FILES[@]}"; do
  for candidate in "$name" "$name.bak"; do
    [ -f "$STATE_DIR/$candidate" ] || continue
    if reason="$(deny_reason "$candidate")"; then
      die "allowlist entry $candidate is refused by the trust boundary ($reason) — fix the allowlist"
    fi
    cp -p -- "$STATE_DIR/$candidate" "$PAYLOAD/$candidate"
    MEMBERS+=("$candidate")
  done
done

if [ "${#MEMBERS[@]}" -eq 0 ]; then
  die "no durable state found under $STATE_DIR — refusing to ship an empty backup"
fi

refuse_secrets "$PAYLOAD"

cat > "$PAYLOAD/EXPOSURE.txt" <<EOF
multiplex relay durable state — off-box backup

Contains ONLY relay-tier durable state: project/tab registry, queued app commands,
pins, upload metadata and rename intents, plus the .bak siblings durable-state.js
maintains for torn-write recovery.

Explicitly NOT contained: muxd identity keys, client private keys, local-control
credentials, principal registries, session ACL state, uploaded blobs.

Exposure: what IS here inherits the relay's exposure profile — plaintext transcript
references, archive indexes, signed intent envelopes, metadata and timing. Treat this
archive and wherever it lands as exactly as sensitive as the relay host itself.

Restore: extract, then place the files back into the relay's MUX_STATE_DIR with the
relay stopped. Verify with: $SELF --verify --dry-run-to <dir> --state-dir <restored dir>
EOF

( cd -- "$PAYLOAD" && "${SHA[@]}" "${MEMBERS[@]}" > MANIFEST.sha256 )

if [ -n "$DRY_RUN_TO" ]; then
  DEFAULT_NAME="mux-relay-state-dryrun.tgz"
else
  DEFAULT_NAME="mux-relay-state-$(date -u +%Y%m%dT%H%M%SZ).tgz"
fi
[ -n "$ARCHIVE_NAME" ] || ARCHIVE_NAME="$DEFAULT_NAME"

ARCHIVE="$STAGE/$ARCHIVE_NAME"
# cd first and name the archive relatively: tar reads an -f argument containing a
# colon as a remote host:path spec, which a drive-lettered TMPDIR would trip over.
( cd -- "$STAGE" && tar -czf "$ARCHIVE_NAME" relay-state )

if [ "$VERIFY" -eq 1 ]; then
  RESTORE="$STAGE/restore"
  mkdir -p -- "$RESTORE"
  ( cd -- "$RESTORE" && tar -xzf "../$ARCHIVE_NAME" )
  [ -d "$RESTORE/relay-state" ] || die "verify: archive has no relay-state/ root"
  refuse_secrets "$RESTORE"
  ( cd -- "$RESTORE/relay-state" && "${SHA[@]}" -c MANIFEST.sha256 >/dev/null ) \
    || die "verify: checksum mismatch inside the archive"
  for member in "${MEMBERS[@]}"; do
    cmp -s "$RESTORE/relay-state/$member" "$STATE_DIR/$member" \
      || die "verify: restored $member differs from the live state file"
  done
  note "verified restore roundtrip: ${#MEMBERS[@]} file(s) byte-identical to $STATE_DIR"
fi

if [ -n "$DRY_RUN_TO" ]; then
  mkdir -p -- "$DRY_RUN_TO"
  cp -- "$ARCHIVE" "$DRY_RUN_TO/$ARCHIVE_NAME"
  note "dry run — wrote $DRY_RUN_TO/$ARCHIVE_NAME (${#MEMBERS[@]} file(s), nothing sent)"
else
  # SFTP, NOT `ssh <cmd>` + scp.
  #
  # The destination is a Windows OpenSSH server with a non-POSIX login shell — measured 2026-09-16,
  # OpenSSH's registry DefaultShell hands the connection PowerShell 5.1, since sshd_config sets none —
  # so a remote command is parsed by that shell, not by sh. The first revision ran
  # `ssh win "mkdir -p 'mux-relay-backups'"`, which such a shell reads as "make a directory literally
  # named 'mux-relay-backups'" (single quotes are not quoting characters to it) with `-p` as a second
  # operand — the destination it created was not the one scp was told to write. SFTP speaks the
  # protocol directly and never invokes the remote shell, so the route stops depending on which shell
  # answers.
  #
  # `-mkdir` (leading dash) means "create it, and do not fail if it is already there" — the same
  # idempotence `mkdir -p` was there for. StrictHostKeyChecking=yes + BatchMode=yes keep an hourly
  # unattended run from ever blocking on a trust prompt: an unknown host fails loudly instead.
  printf -- '-mkdir %s\nput %s %s/%s\n' \
    "$REMOTE_DIR" "$ARCHIVE" "$REMOTE_DIR" "$ARCHIVE_NAME" \
    | sftp -q -b - -F "$SSH_CONFIG" -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=yes "$SSH_HOST" \
    || die "cannot reach $SSH_HOST — the 'win' sftp route to the PC is down, or its host key is not in known_hosts"
  note "sent $ARCHIVE_NAME to $SSH_HOST:$REMOTE_DIR/ (${#MEMBERS[@]} file(s))"
fi
