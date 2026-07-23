#!/usr/bin/env bash
# Deploy relay/ from the monorepo to the live VPS (~/multiplex-app) and restart the relay.
# Code only — excludes node_modules/state/backups. Run from the monorepo root.
#
# The restart is SIGTERM-first on purpose. server.js handles SIGTERM by draining: it refuses new
# upgrades, flushes pins, and closes every viewer with 1012 'restarting' — which index.html maps to a
# 500ms reconnect plus a toast. Anything that skips SIGTERM (a SIGKILL, or pm2's default SIGINT) skips
# the drain, so every browser sees an abnormal 1006 close and walks its exponential backoff: a deploy
# looks like an outage for ~10s. So we signal, wait for the relay to exit on its own (well inside its
# own 5s backstop), and only then bring it back up.
set -e
cd "$(dirname "$0")/../relay"
tar czf - --exclude node_modules --exclude '*.log' --exclude '.deploy-backup*' . | \
  ssh harmonizer@192.168.1.142 '
set -e
cd ~/multiplex-app
tar xzf -
echo deployed

# [n]ode: keeps this pattern from matching the shell that is running this very script
relay_pid=$(pgrep -f "[n]ode .*server\.js" | head -1 || true)
drain_wait() {                        # up to 8s for the drain to close sockets and exit
  [ -n "$relay_pid" ] || return 0
  for _ in $(seq 1 40); do
    kill -0 "$relay_pid" 2>/dev/null || { echo "drained"; return 0; }
    sleep 0.2
  done
  echo "warning: relay did not exit 8s after SIGTERM; the supervisor will force it down"
}

if pm2 describe multiplex >/dev/null 2>&1; then
  pm2 sendSignal SIGTERM multiplex >/dev/null || true   # NOT pm2 restart: that sends SIGINT, skipping the drain
  drain_wait
  pm2 restart multiplex --update-env                    # start if pm2 autorestart has not already done it
elif command -v systemctl >/dev/null 2>&1 && sudo systemctl is-active --quiet multiplex; then
  sudo systemctl kill -s SIGTERM multiplex              # signal and wait ourselves, then restart cleanly
  drain_wait
  sudo systemctl restart multiplex
else
  echo "restart the relay manually"
fi
'
