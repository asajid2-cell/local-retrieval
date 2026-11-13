#!/usr/bin/env bash
# Install the relay and page that repair a viewer after a PC host flap, and give the phone a bottom-bar
# toggle plus a durable conversation. Backs up what was there, proves the staged bytes are the new build,
# installs, restarts the container and polls health. The page and threads.js are installed in the same pass
# so the served page and the served segmenter can never be a build apart.
set -euo pipefail
SRV=/tmp/server.js.new
SM=/tmp/screen-model.js.new
SRC=/tmp/index.html.new
SRCJS=/tmp/threads.js.new
DSTDIR=/home/harmonizer/multiplex-app
STAMP='2026-11-13-attach-repair-1'
[ -s "$SRV" ] || { echo "staged server.js missing"; exit 3; }
[ -s "$SM" ] || { echo "staged screen-model.js missing"; exit 3; }
[ -s "$SRC" ] || { echo "staged page missing"; exit 3; }
[ -s "$SRCJS" ] || { echo "staged threads.js missing"; exit 3; }
# The exact build, by marker, before anything is installed. A stale /tmp file that looks right is how a
# local edit once "deployed" itself twice; the hash is printed so the run can be checked against the source.
grep -q "A viewer already ON THE BANNER is owed the same paint" "$SRV" || { echo "staged server.js lacks the banner repair"; exit 4; }
grep -q "A pending grid request belonged to the OLD link" "$SRV" || { echo "staged server.js lacks the boundary gridReq clear"; exit 4; }
grep -q "MUX_SNAPSHOT_MAX_BYTES" "$SM" || { echo "staged screen-model.js lacks the snapshot byte budget"; exit 4; }
grep -q "$STAMP" "$SRC" || { echo "staged page is not the attach-repair build"; exit 4; }
grep -q "function accumulateThreads" "$SRC" || { echo "staged page lacks the conversation accumulation"; exit 4; }
grep -q "id=\"navtoggle\"" "$SRC" || { echo "staged page lacks the nav toggle"; exit 4; }
grep -q "MuxThreads" "$SRCJS" || { echo "staged threads.js is not the segmenter"; exit 4; }
echo "staged.server.sha256 $(sha256sum "$SRV" | cut -d' ' -f1)"
echo "staged.screenmodel.sha256 $(sha256sum "$SM" | cut -d' ' -f1)"
echo "staged.page.sha256 $(sha256sum "$SRC" | cut -d' ' -f1)"
TS="$(date +%Y%m%d-%H%M%S)"
cp -a "$DSTDIR/server.js" "/home/harmonizer/server.js.bak-$TS"
cp -a "$DSTDIR/screen-model.js" "/home/harmonizer/screen-model.js.bak-$TS"
cp -a "$DSTDIR/public/index.html" "/home/harmonizer/index.html.bak-$TS"
[ -f "$DSTDIR/public/threads.js" ] && cp -a "$DSTDIR/public/threads.js" "/home/harmonizer/threads.js.bak-$TS"
install -o harmonizer -g harmonizer -m 644 "$SRV" "$DSTDIR/server.js"
install -o harmonizer -g harmonizer -m 644 "$SM" "$DSTDIR/screen-model.js"
install -o harmonizer -g harmonizer -m 644 "$SRC" "$DSTDIR/public/index.html"
install -o harmonizer -g harmonizer -m 644 "$SRCJS" "$DSTDIR/public/threads.js"
echo "server.sha256 $(sha256sum "$DSTDIR/server.js" | cut -d' ' -f1)"
echo "page.sha256 $(sha256sum "$DSTDIR/public/index.html" | cut -d' ' -f1)"
docker restart multiplex-app >/dev/null
for i in $(seq 1 30); do
  code=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/api/health || true)
  if [ "$code" = "200" ]; then echo "health=200 after ${i}s"; break; fi
  sleep 1
done
[ "${code:-}" = "200" ] || { echo "health never returned 200"; exit 1; }
# What is actually SERVED, not what is on disk: the page must carry the new build stamp and the segmenter
# must be reachable at the URL the page loads it from.
echo "served $(curl -s http://127.0.0.1:7682/ | grep -o "window.__muxBuild='[^']*'" | head -1)"
echo "threads.js $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/threads.js)"
echo "screenModel $(curl -s http://127.0.0.1:7682/api/health | sed -n 's/.*"screenModel":"\([^"]*\)".*/\1/p')"
