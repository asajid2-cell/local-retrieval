#!/usr/bin/env bash
# Install the relay that stops paying for history twice on attach: when the attach itself starts the
# tee's feed, it waits for the resync snapshot instead of also requesting scrollback. Backs up what was
# there, verifies the marker and the bytes on disk, restarts the app and polls health.
set -euo pipefail
SRC=${SRC:-/tmp/server.js.new}
DST=/home/harmonizer/multiplex-app/server.js
MARK='State V: this attach started the feed'
[ -s "$SRC" ] || { echo "staged server.js missing"; exit 3; }
grep -q "$MARK" "$SRC" || { echo "staged server.js is not the watch-flip build"; exit 4; }
# Echo the staged hash BEFORE installing: two runs of this script with an unchanged /tmp file install
# identical bytes and report success, which is how a local edit that never got staged looked "deployed".
echo "staged.sha256 $(sha256sum "$SRC" | cut -d' ' -f1)"
cp -a "$DST" "/home/harmonizer/server.js.bak-$(date +%Y%m%d-%H%M%S)"
install -o harmonizer -g harmonizer -m 644 "$SRC" "$DST"
echo "sha256 $(sha256sum "$DST" | cut -d' ' -f1)"
echo "bytes $(wc -c < "$DST")"
grep -c "State V: this attach started the feed" "$DST" | sed 's/^/watchflip-lines=/'
grep -c "const watchFlip = syncWatch" "$DST" | sed 's/^/watchflip-call-sites=/'
# The ack is what makes the wait SAFE rather than a gamble: without it the relay waits for a snapshot the
# tee never sends (the deployed banner bug, 2026-11-11). Verify it landed in the file that got installed.
grep -c "m.t === 'watchAck'" "$DST" | sed 's/^/watchack-handler-lines=/'
grep -c "teeAcksWatch" "$DST" | sed 's/^/per-session-gate-lines=/'
grep -q "teeAcksWatch" "$DST" || { echo "installed server.js has no per-session watchAck gate"; exit 6; }
grep -q "st.watchWaiters" "$DST" || { echo "installed server.js has no watchWaiters - not the ack build"; exit 5; }
docker restart multiplex-app >/dev/null
for i in $(seq 1 30); do
  code=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/api/health || true)
  if [ "$code" = "200" ]; then echo "health=200 after ${i}s"; exit 0; fi
  sleep 1
done
echo "health never returned 200"
exit 1
