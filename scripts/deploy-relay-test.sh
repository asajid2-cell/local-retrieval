#!/usr/bin/env bash
# Package the committed relay and hand it to the root-owned test installer.
# This script is deliberately separate from scripts/deploy-relay.sh and production deployment.
set -euo pipefail

VPS=harmonizer-admin
REMOTE_DEPLOY=/usr/local/bin/deploy-multiplex-test
REMOTE_INCOMING=/home/admin/multiplex-test-incoming

fail() { printf 'deploy-relay-test.sh: %s\n' "$*" >&2; exit 1; }
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$ROOT"
[ -z "$(git status --porcelain)" ] || fail 'worktree must be clean; deploy an exact commit'
COMMIT="$(git rev-parse HEAD)"
[[ "$COMMIT" =~ ^[0-9a-f]{40}$ ]] || fail 'could not resolve a full commit hash'

ssh "$VPS" "sudo -n '$REMOTE_DEPLOY' --preflight" ||
  fail 'remote test deploy preflight failed; provision/configure the test endpoint first'

STAGE="$(mktemp -d "${TMPDIR:-/tmp}/multiplex-test-release-XXXXXX")"
ARCHIVE="$STAGE/multiplex-test-$COMMIT.tgz"
cleanup() { rm -rf -- "$STAGE"; }
trap cleanup EXIT

git archive --format=tar HEAD relay scripts/backup-state.sh |
  tar -xf - -C "$STAGE"
printf '%s\n' "$COMMIT" > "$STAGE/RELEASE_COMMIT"
tar -czf "$ARCHIVE" -C "$STAGE" relay scripts RELEASE_COMMIT
SHA256="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
[[ "$SHA256" =~ ^[0-9a-f]{64}$ ]] || fail 'could not hash release archive'

scp -q -- "$ARCHIVE" "$VPS:$REMOTE_INCOMING/multiplex-test-$COMMIT.tgz"
ssh "$VPS" "sudo -n '$REMOTE_DEPLOY' '$COMMIT' '$SHA256'"
printf 'deployed multiplex-test commit %s\n' "$COMMIT"
