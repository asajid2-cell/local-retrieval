#!/usr/bin/env bash
# Package the committed relay and hand it to the root-owned, fixed-policy VPS installer.
set -euo pipefail

VPS=harmonizer-sub
REMOTE_DEPLOY=/usr/local/bin/deploy-multiplex
REMOTE_INCOMING=/home/sub/multiplex-incoming

die() { printf 'deploy-relay.sh: %s\n' "$*" >&2; exit 1; }

ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$ROOT"
[ -z "$(git status --porcelain)" ] || die "worktree must be clean; deploy an exact commit"
COMMIT="$(git rev-parse HEAD)"
[[ "$COMMIT" =~ ^[0-9a-f]{40}$ ]] || die "could not resolve a full commit hash"

ssh "$VPS" "sudo -n '$REMOTE_DEPLOY' --preflight" ||
  die "remote deploy preflight failed; provision the narrow wrapper before uploading a release"

STAGE="$(mktemp -d "${TMPDIR:-/tmp}/multiplex-release-XXXXXX")"
ARCHIVE="$STAGE/multiplex-$COMMIT.tgz"
cleanup() { rm -rf -- "$STAGE"; }
trap cleanup EXIT

git archive --format=tar HEAD relay scripts/backup-state.sh |
  tar -xf - -C "$STAGE"
printf '%s\n' "$COMMIT" > "$STAGE/RELEASE_COMMIT"
tar -czf "$ARCHIVE" -C "$STAGE" relay scripts RELEASE_COMMIT
SHA256="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
[[ "$SHA256" =~ ^[0-9a-f]{64}$ ]] || die "could not hash release archive"

ssh "$VPS" "mkdir -p '$REMOTE_INCOMING' && chmod 0700 '$REMOTE_INCOMING'"
scp -q -- "$ARCHIVE" "$VPS:$REMOTE_INCOMING/multiplex-$COMMIT.tgz"
ssh "$VPS" "sudo '$REMOTE_DEPLOY' '$COMMIT' '$SHA256'"

printf 'deployed Multiplex commit %s\n' "$COMMIT"
