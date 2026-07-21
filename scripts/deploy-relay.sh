#!/usr/bin/env bash
# Deploy relay/ from the monorepo to the live VPS (~/multiplex-app) and restart the relay.
# Code only — excludes node_modules/state/backups. Run from the monorepo root.
set -e
cd "$(dirname "$0")/../relay"
tar czf - --exclude node_modules --exclude '*.log' --exclude '.deploy-backup*' . | \
  ssh harmonizer@192.168.1.142 'cd ~/multiplex-app && tar xzf - && echo deployed && (pm2 restart multiplex 2>/dev/null || sudo systemctl restart multiplex 2>/dev/null || echo "restart the relay manually")'
