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
# The glob the remote listing uses. Kept as the literal prefix rather than an
# is_archive_name call because sftp matches it, not us.
ARCHIVE_PREFIX="mux-relay-state-"

# --- retention ---------------------------------------------------------------
# The archive name carries a UTC stamp, so every run is a NEW file: without a
# pruning pass an hourly timer leaves ~8760 near-identical copies a year on the
# far side. Retention runs over the remote directory AFTER a successful put, and
# it is deliberately coarse: keep the newest KEEP_RECENT outright (that is the
# recent-granularity window), then keep the newest archive of each older day for
# KEEP_DAILY more days. All of the logic lives on this side — the far side is a
# Windows OpenSSH server with a non-POSIX shell, so it is only ever asked to list
# and delete over SFTP, never to evaluate anything.
KEEP_RECENT=24
KEEP_DAILY=30
PRUNE=1
PRUNE_PLAN=0
# Printed as the last command of the listing batch and required before any delete is
# attempted. A complete listing that happened to be EMPTY and a listing that never ran
# both produce no entries; only this marker distinguishes them, and the difference is
# the difference between "nothing to prune" and "do not touch what you cannot see".
SENTINEL="sftp-mux-prune-sentinel"

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
  --keep-recent <n>     Retention: keep this many newest archives outright (default: 24).
  --keep-daily <n>      Retention: then keep the newest archive of each of this many older days
                        (default: 30). Together these bound the far side at roughly
                        keep-recent + keep-daily archives however often the timer fires.
  --no-prune            Send the archive and delete nothing.
  --prune-plan          Read a remote listing on stdin (the shape `ls -1` prints over sftp) and
                        print the retention decision for each entry, in the form
                        "keep <name>" / "delete <name>". Touches no network and no files, so it
                        answers "what would a real run delete?" before one runs.
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
    --keep-recent)  [ $# -ge 2 ] || die "--keep-recent needs a value";  KEEP_RECENT="$2";   shift 2 ;;
    --keep-daily)   [ $# -ge 2 ] || die "--keep-daily needs a value";   KEEP_DAILY="$2";    shift 2 ;;
    --no-prune)     PRUNE=0; shift ;;
    --prune-plan)   PRUNE_PLAN=1; shift ;;
    --verify)       VERIFY=1; shift ;;
    -h|--help)      usage; exit 0 ;;
    *)              die "unknown argument: $1 (try --help)" ;;
  esac
done

# is_archive_name <name> — the ONLY shape retention is ever allowed to delete.
# Everything below leans on this being strict: it is what keeps a stray file in the
# remote directory, or a name carrying whitespace or a newline (which would inject a
# second command into the sftp batch), out of reach of the sweep.
is_archive_name() {
  case "$1" in
    mux-relay-state-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]T[0-9][0-9][0-9][0-9][0-9][0-9]Z.tgz) return 0 ;;
    *) return 1 ;;
  esac
}

# prune_plan — reads a remote listing (one name per line, the shape `ls -1` prints
# over sftp) on stdin and prints the retention decision for each entry. Pure: no
# network, no filesystem, so the same function answers "what would a real run
# delete?" offline and drives the real deletes on the far side.
#
# Order is the whole algorithm. The stamps are fixed-width UTC, so a plain reverse
# sort is chronological, newest first. Keep the newest KEEP_RECENT outright — that
# is the recent-granularity window — then walk what is left and keep the newest
# archive of each distinct day for KEEP_DAILY days. Entries that are not our
# archives are always kept: this directory may be shared, and a sweep that deletes
# what it does not recognise is a sweep nobody can run unattended.
prune_plan() {
  local line name stamp day
  local -a ours=()
  while IFS= read -r line; do
    line="${line%$'\r'}"
    # Trim trailing blanks: an sftp listing may pad, and a trailing space would
    # otherwise ride into the batch file as part of the operand.
    while [ -n "$line" ] && [ "${line% }" != "$line" ]; do line="${line% }"; done
    [ -n "$line" ] || continue
    # Drop the command echo a batch-mode sftp prints. Measured against the real
    # route (2026-09-17): `sftp -b -` writes the prompt and command line into the
    # same stream as the listing, e.g.
    #     sftp> ls -1 mux-relay-backups
    #     mux-relay-backups/mux-relay-state-20260917T010033Z.tgz
    # Without this, every entry also carries the REMOTE_DIR prefix stripped just
    # below, no name matches the archive shape, and the sweep would classify the
    # whole directory as foreign, keep all of it, and silently never prune.
    case "$line" in "sftp>"*) continue ;; esac
    # The completion sentinel is a liveness marker, not an entry. It reached the
    # caller through this same pipe, so it reaches prune_plan too.
    [ "$line" = "$SENTINEL" ] && continue
    # sftp echoes each path relative to the directory it was given, so a bare
    # `ls -1` answers "./name". Strip that prefix: without it no entry matches the
    # archive shape and the sweep silently keeps the whole directory forever.
    # The REMOTE_DIR form is stripped too, so a listing taken with the directory
    # spelled out parses the same way.
    case "$line" in ./"$REMOTE_DIR"/*) line="${line#./}" ;; esac
    case "$line" in ./|../) continue ;; esac
    case "$line" in ./*) line="${line#./}" ;; esac
    case "$line" in "$REMOTE_DIR"/*) line="${line#"$REMOTE_DIR"/}" ;; esac
    [ -n "$line" ] || continue
    case "$line" in .|..) continue ;; esac
    if is_archive_name "$line"; then ours+=("$line"); else printf 'keep %s\n' "$line"; fi
  done

  if [ "${#ours[@]}" -eq 0 ]; then return 0; fi

  local -a sorted=()
  while IFS= read -r name; do sorted+=("$name"); done < <(printf '%s\n' "${ours[@]}" | LC_ALL=C sort -r)

  local index=0
  declare -A seen_day=()
  local daily_kept=0
  for name in "${sorted[@]}"; do
    index=$((index + 1))
    if [ "$index" -le "$KEEP_RECENT" ]; then
      printf 'keep %s\n' "$name"
      continue
    fi
    stamp="${name#mux-relay-state-}"; stamp="${stamp%.tgz}"; day="${stamp%%T*}"
    if [ -n "${seen_day[$day]:-}" ]; then
      printf 'delete %s\n' "$name"
    elif [ "$daily_kept" -lt "$KEEP_DAILY" ]; then
      seen_day[$day]=1; daily_kept=$((daily_kept + 1))
      printf 'keep %s\n' "$name"
    else
      seen_day[$day]=1
      printf 'delete %s\n' "$name"
    fi
  done
}

# Floors, so a fat-fingered flag cannot turn retention into a directory wipe.
# Validated in every mode, including --prune-plan: a mode that answers "what would
# this delete?" must not answer it for values a real run would refuse.
case "$KEEP_RECENT" in ''|*[!0-9]*) die "--keep-recent must be a non-negative integer" ;; esac
case "$KEEP_DAILY"  in ''|*[!0-9]*) die "--keep-daily must be a non-negative integer"  ;; esac
[ "$KEEP_RECENT" -ge 1 ] || die "--keep-recent must keep at least the archive just sent"

if [ "$PRUNE_PLAN" -eq 1 ]; then
  # Offline decision surface. Deliberately before any state-dir or checksum probe:
  # this mode answers a question about a LISTING, and needs neither.
  prune_plan
  exit 0
fi

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
  sftp -q -b - -F "$SSH_CONFIG" -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=yes "$SSH_HOST" \
    <<SFTP \
    || die "cannot reach $SSH_HOST — the 'win' sftp route to the PC is down, or its host key is not in known_hosts"
-mkdir $REMOTE_DIR
put $ARCHIVE $REMOTE_DIR/$ARCHIVE_NAME
SFTP
  note "sent $ARCHIVE_NAME to $SSH_HOST:$REMOTE_DIR/ (${#MEMBERS[@]} file(s))"

  # --- retention, applied AFTER a successful put -----------------------------
  # Order matters: the archive is on the far side before anything is pruned, so an
  # interrupted run leaves one extra copy rather than deleting the oldest backup to
  # make room for one that never landed. Retention failure is a WARNING, never a
  # death: the backup itself succeeded, and failing the unit here would mean a full
  # 24-hour gap in backups over a directory that only needs a human to look at it.
  if [ "$PRUNE" -eq 1 ]; then
    # The sentinel deliberately does NOT match the ls pattern: a glob that matches
    # nothing makes sftp report an error and continue, so the batch still reaches the
    # last command. It must be an equality match (a glob would swallow the sentinel
    # into its own listing). The glob itself is what keeps sftp from having to tell a
    # file from a directory - a bare `ls -1` lists directory CONTENTS, so pointing it
    # at the remote dir answers with bare names and no prefix to strip at all.
    LISTING="$(sftp -q -b - -F "$SSH_CONFIG" -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=yes "$SSH_HOST" \
      <<SFTP 2>/dev/null
cd $REMOTE_DIR
ls -1 $ARCHIVE_PREFIX*
$SENTINEL
SFTP
    )"
    # A missing sentinel means the listing did not run to completion - an unknown
    # shell, a changed prompt, a truncated stream. Guessing at a partial listing
    # would mean deciding what to DELETE from incomplete information, so refuse it
    # and warn instead. This is the check that keeps the sweep from acting on a
    # stream it does not understand; `sftp>` echoes are dropped in prune_plan.
    case "$LISTING" in
      *"$SENTINEL"*) : ;;
      *)
        note "retention: WARNING — the remote listing did not complete; nothing pruned this run"
        LISTING=""
        ;;
    esac
    if [ -z "$LISTING" ]; then
      :
    elif [ -z "$(printf '%s' "$LISTING" | tr -d ' \t\r\n')" ]; then
      note "retention: WARNING — could not list $SSH_HOST:$REMOTE_DIR/; nothing pruned this run"
    else
      PLAN="$(printf '%s\n' "$LISTING" | prune_plan)"
      DELETES="$(printf '%s\n' "$PLAN" | while IFS= read -r l; do
        case "$l" in "delete "*) printf '%s\n' "${l#delete }" ;; esac
      done)"
      KEPT="$(printf '%s\n' "$PLAN" | grep -c '^keep ')"; KEPT="${KEPT:-0}"
      if [ -z "$DELETES" ]; then
        note "retention: kept $KEPT entr(ies) in $REMOTE_DIR, nothing to prune"
      else
        DEL_COUNT="$(printf '%s\n' "$DELETES" | grep -c .)"
        {
          printf 'cd %s\n' "$REMOTE_DIR"
          printf '%s\n' "$DELETES" | while IFS= read -r name; do
            printf 'rm %s\n' "$name"
          done
          printf 'ls -1\n'
          printf 'bye\n'
        } > "$STAGE/prune.batch"
        # No sentinel here: this output is only used to report the remaining count, and
        # a marker line would inflate it. `grep -c .` drops the sftp prompt echo, which
        # has no trailing newline of its own.
        REMAINING="$(sftp -q -b "$STAGE/prune.batch" -F "$SSH_CONFIG" -o BatchMode=yes \
          -o ConnectTimeout=10 -o StrictHostKeyChecking=yes "$SSH_HOST" 2>/dev/null | grep -c . || true)"
        note "retention: kept $KEPT, pruned $DEL_COUNT of them from $REMOTE_DIR (${REMAINING:-?} entries remain)"
      fi
    fi
  fi
fi
