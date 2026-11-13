#!/usr/bin/env bash
# Sign in as the owner and mint a TEST session, then print only the outcome and the cookie NAME set.
# Credentials arrive as OWNER_PASS (keysafe) and MUX_TOTP (the owner's current code) - neither is echoed.
set -u
UA='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36'
J=/tmp/hljar
rm -f "$J" /tmp/hl_login.html /tmp/hl_post.html
curl -s -c "$J" -A "$UA" https://harmonizerlabs.cc/auth/login -o /tmp/hl_login.html || { echo "login page fetch failed"; exit 3; }
csrf=$(grep -o 'name="_csrf" value="[^"]*"' /tmp/hl_login.html | head -1 | sed 's/.*value="//; s/"$//')
[ -n "$csrf" ] || { echo "no csrf token on the login page"; exit 3; }
code=$(curl -s -o /tmp/hl_post.html -w '%{http_code}' -c "$J" -b "$J" -A "$UA" \
  --data-urlencode "username=owner" \
  --data-urlencode "password=${OWNER_PASS}" \
  --data-urlencode "code=${MUX_TOTP}" \
  --data-urlencode "_csrf=${csrf}" \
  --data-urlencode "test_session=1" \
  --data-urlencode "next=/multiplex/" \
  https://harmonizerlabs.cc/auth/login)
echo "post_status=${code}"
if grep -q 'hl_session' "$J" 2>/dev/null; then echo "hl_session=SET"; else echo "hl_session=ABSENT"; fi
if [ "$code" != "302" ] && [ "$code" != "303" ]; then
  grep -oE 'class="(error|notice)"[^<]*<[^>]*>[^<]{0,120}|Invalid[^<]{0,80}|expired[^<]{0,60}|incorrect[^<]{0,60}' /tmp/hl_post.html | head -5
fi
