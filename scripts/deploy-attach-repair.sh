set -euo pipefail
mkdir -p /home/harmonizer/multiplex-app/public
for f in server.js screen-model.js; do
  install -o harmonizer -g harmonizer -m 644 /tmp/$f.new /home/harmonizer/multiplex-app/$f
done
for f in index.html threads.js; do
  install -o harmonizer -g harmonizer -m 644 /tmp/$f.new /home/harmonizer/multiplex-app/public/$f
done
echo "--- installed hashes ---"
sha256sum /home/harmonizer/multiplex-app/server.js /home/harmonizer/multiplex-app/screen-model.js /home/harmonizer/multiplex-app/public/index.html /home/harmonizer/multiplex-app/public/threads.js
echo "--- markers ---"
grep -c "A viewer already ON THE BANNER is owed the same paint" /home/harmonizer/multiplex-app/server.js
grep -c "A pending grid request belonged to the OLD link" /home/harmonizer/multiplex-app/server.js
grep -c "MUX_SNAPSHOT_MAX_BYTES" /home/harmonizer/multiplex-app/screen-model.js
grep -o "window.__muxBuild='[^']*'" /home/harmonizer/multiplex-app/public/index.html | head -1
grep -c 'id="navtoggle"' /home/harmonizer/multiplex-app/public/index.html
grep -c "function accumulateThreads" /home/harmonizer/multiplex-app/public/index.html
echo "--- restart ---"
docker restart multiplex-app >/dev/null
for i in $(seq 1 30); do
  code=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/api/health || true)
  if [ "$code" = "200" ]; then echo "health=200 after ${i}s"; break; fi
  sleep 1
done
echo "served $(curl -s http://127.0.0.1:7682/ | grep -o "window.__muxBuild='[^']*'" | head -1)"
echo "threads.js $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:7682/threads.js)"
echo "screenModel $(curl -s http://127.0.0.1:7682/api/health | sed -n 's/.*"screenModel":"\([^"]*\)".*/\1/p')"
