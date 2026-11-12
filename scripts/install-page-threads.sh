#!/usr/bin/env bash
# Install the phone-threads viewer page and its new threads.js into the live multiplex-app tree. Keeps a
# timestamped backup of both, verifies the build stamp the box actually SERVES (not just the file on disk),
# and re-checks that threads.js is reachable at the URL the page loads it from.
set -euo pipefail
SRC=/tmp/index.html.new
SRCJS=/tmp/threads.js.new
DST=/home/harmonizer/multiplex-app/public/index.html
DSTJS=/home/harmonizer/multiplex-app/public/threads.js
STAMP='2026-11-12-phone-threads-4'
[ -s "$SRC" ] || { echo "staged page missing"; exit 3; }
[ -s "$SRCJS" ] || { echo "staged threads.js missing"; exit 3; }
grep -q "$STAMP" "$SRC" || { echo "staged page is not the threads build"; exit 4; }
grep -q "MuxThreads" "$SRCJS" || { echo "staged threads.js is not the segmenter"; exit 4; }
cp -a "$DST" "/home/harmonizer/index.html.bak-$(date +%Y%m%d-%H%M%S)"
if [ -f "$DSTJS" ]; then cp -a "$DSTJS" "/home/harmonizer/threads.js.bak-$(date +%Y%m%d-%H%M%S)"; fi
install -o harmonizer -g harmonizer -m 644 "$SRC" "$DST"
install -o harmonizer -g harmonizer -m 644 "$SRCJS" "$DSTJS"
echo "page.sha256 $(sha256sum "$DST" | cut -d' ' -f1)"
echo "page.bytes $(wc -c < "$DST")"
echo "js.sha256 $(sha256sum "$DSTJS" | cut -d' ' -f1)"
echo "served $(curl -s http://127.0.0.1:7682/ | grep -o "window.__muxBuild='[^']*'" | head -1)"
echo "threads.js $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/threads.js)"
echo "health $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/api/health)"
