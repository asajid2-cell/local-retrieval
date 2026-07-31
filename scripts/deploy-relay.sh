#!/usr/bin/env bash
# Deploy relay/ from the monorepo to the live VPS relay.
#
# ⚠ REFUSES BY DESIGN. This script used to tar relay/ into ~/multiplex-app — but that target is DEAD:
# since the loopback-security work the live relay is /opt/multiplex-app (served by multiplex-app.service,
# root-owned). The old path silently no-ops, so a "successful" deploy changed nothing.
#
# It is NOT re-pointed at /opt to auto-run, because the two copies have DIVERGED in both directions:
#   - canonical (this repo's relay/, ~2350 lines) is AHEAD on features and now also carries every
#     security fix (loopback-bridge allowlist + wsOriginOk),
#   - the live VPS runs an OLDER fork (~1441 lines) that lacks canonical's features.
# Pushing canonical over the VPS blind would drop live behaviour that hasn't been reconciled/tested.
# That reconciliation (forward-port the VPS's remaining live-only bits, or validate canonical on the
# VPS first) is a deliberate migration — not a tar-and-restart.
#
# When you HAVE reconciled and want to deploy to /opt on purpose, do it explicitly with a backup:
#   MUX_DEPLOY_CONFIRM=1 scripts/deploy-relay.sh
set -euo pipefail
VPS=harmonizer@192.168.1.142
LIVE=/opt/multiplex-app

if [ "${MUX_DEPLOY_CONFIRM:-0}" != "1" ]; then
  cat >&2 <<EOF
deploy-relay.sh refused (safe default).
  live relay : $LIVE  (multiplex-app.service)
  dead target: ~/multiplex-app  (the old path — deploying there changes nothing)
  status     : canonical is the feature+security superset; the VPS runs an older fork. They have not
               been reconciled, so a blind deploy would regress the live relay.
To deploy canonical to $LIVE anyway (after reconciling + testing), re-run with MUX_DEPLOY_CONFIRM=1.
EOF
  exit 2
fi

cd "$(dirname "$0")/../relay"
STAMP=$(date +%Y%m%d-%H%M%S)
echo "backing up $LIVE/server.js -> server.js.pre-deploy-$STAMP on the VPS ..."
ssh "$VPS" "sudo cp $LIVE/server.js $LIVE/server.js.pre-deploy-$STAMP"
echo "deploying relay/ code to $LIVE (sudo) ..."
tar czf - --exclude node_modules --exclude '*.log' --exclude '.deploy-backup*' \
          --exclude 'durable-state.json' --exclude 'app-commands.json' --exclude uploads . | \
  ssh "$VPS" "sudo tar xzf - -C $LIVE && echo deployed"
# SIGTERM-FIRST, then wait. server.js now handles SIGTERM by DRAINING: it refuses new upgrades, flushes
# pins, and closes every viewer with 1012 'restarting', which index.html maps to a 500ms reconnect plus a
# toast. `systemctl restart` alone does signal SIGTERM, but it does not wait for the drain to finish
# before bringing the unit back, so a viewer can still see an abnormal 1006 and walk its exponential
# backoff — the deploy looks like a ~10s outage. Signal, wait for the process to go (well inside its own
# 5s backstop), then start clean. r.1.4.3 shipped this logic against the wrong service name and the dead
# ~/multiplex-app target; it belongs here, on /opt, behind the confirm gate.
ssh "$VPS" "
  sudo systemctl kill -s SIGTERM multiplex-app 2>/dev/null || true
  for i in \$(seq 1 40); do
    sudo systemctl is-active --quiet multiplex-app || break
    sleep 0.2
  done
  sudo systemctl restart multiplex-app && echo restarted
"
echo "health check:"
ssh "$VPS" "curl -fsS http://127.0.0.1:\$(sudo grep -oE 'PORT[ =:]+[0-9]+' $LIVE/server.js | grep -oE '[0-9]+' | head -1)/api/health || echo 'health check FAILED — check the service'"
