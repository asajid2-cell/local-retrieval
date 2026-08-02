#!/usr/bin/env bash
# Deploy relay/ from the monorepo to the live VPS relay.
#
# REFUSES BY DESIGN. This script used to tar relay/ into ~/multiplex-app, but that target is dead:
# since the loopback-security work the live relay is /opt/multiplex-app (served by
# multiplex-app.service, root-owned). The old path silently no-ops, so a "successful" deploy changed
# nothing.
#
# It is not re-pointed at /opt to auto-run, because the two copies diverged in both directions:
#   - canonical is ahead on features and carries every security fix,
#   - the live VPS runs an older fork.
#
# When reconciliation and validation are complete, deploy explicitly with a backup:
#   MUX_DEPLOY_CONFIRM=1 scripts/deploy-relay.sh
set -euo pipefail
VPS=harmonizer@192.168.1.142
LIVE=/opt/multiplex-app

if [ "${MUX_DEPLOY_CONFIRM:-0}" != "1" ]; then
  cat >&2 <<EOF
deploy-relay.sh refused (safe default).
  live relay : $LIVE  (multiplex-app.service)
  dead target: ~/multiplex-app  (the old path; deploying there changes nothing)
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

# Signal first and wait for the drain to complete before restarting. This lets server.js refuse new
# upgrades, flush pins, and close viewers with 1012 "restarting" instead of an abnormal 1006.
ssh "$VPS" "
  sudo systemctl kill -s SIGTERM multiplex-app 2>/dev/null || true
  for i in \$(seq 1 40); do
    sudo systemctl is-active --quiet multiplex-app || break
    sleep 0.2
  done
  sudo systemctl restart multiplex-app && echo restarted
"

echo "health check:"
ssh "$VPS" "
  PORT=\$(sudo awk -F= '\$1 == \"PORT\" { print \$2; exit }' /etc/multiplex-app.env)
  test -n \"\$PORT\"
  curl -fsS \"http://127.0.0.1:\$PORT/api/health\"
"
