#!/usr/bin/env bash
# Install the relay whose last-viewer release backstop is committed at dbe1836, then restart the app so
# node picks it up. Backs up what was there; verifies health and that the file on disk is the staged one.
set -euo pipefail
SRC=/tmp/server.js.new
DST=/home/harmonizer/multiplex-app/server.js
[ -s "$SRC" ] || { echo "staged server.js missing"; exit 3; }
cp -a "$DST" "/home/harmonizer/server.js.bak-$(date +%Y%m%d-%H%M%S)"
install -o harmonizer -g harmonizer -m 644 "$SRC" "$DST"
echo "sha256 $(sha256sum "$DST" | cut -d' ' -f1)"
echo "bytes $(wc -c < "$DST")"
grep -c "NOBODY WATCHING MEANS NOBODY OWNS IT" "$DST" | sed 's/^/backstop-lines=/'
docker restart multiplex-app >/dev/null
for i in $(seq 1 30); do
  code=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/api/health || true)
  if [ "$code" = "200" ]; then echo "health=200 after ${i}s"; exit 0; fi
  sleep 1
done
echo "health never returned 200"
exit 1
