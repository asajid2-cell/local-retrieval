#!/usr/bin/env bash
# Install the grid-ownership-2 viewer page (click forwarding on adopted consoles + the ladder/cell-drift
# fixes) into the live multiplex-app tree, keeping a timestamped backup of what was there.
set -euo pipefail
SRC=/tmp/index.html.new
DST=/home/harmonizer/multiplex-app/public/index.html
[ -s "$SRC" ] || { echo "staged page missing"; exit 3; }
cp -a "$DST" "/home/harmonizer/index.html.bak-$(date +%Y%m%d-%H%M%S)"
install -o harmonizer -g harmonizer -m 644 "$SRC" "$DST"
echo "sha256 $(sha256sum "$DST" | cut -d' ' -f1)"
echo "bytes $(wc -c < "$DST")"
grep -o "window.__muxBuild='[^']*'" "$DST" | head -1
echo "health $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/api/health || true)"
echo "served $(curl -s http://127.0.0.1:7682/ | grep -o "window.__muxBuild='[^']*'" | head -1)"
