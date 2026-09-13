#!/usr/bin/env bash
# One-time root/admin provisioning for the isolated parallel relay test endpoint.
set -euo pipefail
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

[ "$(id -u)" -eq 0 ] || { printf '%s\n' 'run as root' >&2; exit 1; }
HERE="$(cd -- "$(dirname -- "$0")" && pwd)"
SITE_LINK=/etc/nginx/sites-enabled/harmonizer
SITE=/etc/nginx/sites-available/harmonizer
SNIPPET=/etc/nginx/snippets/multiplex-test.conf
MARKER='    include /etc/nginx/snippets/cluster-app.conf;'

if ! getent group svc-multiplex-test >/dev/null; then
  groupadd --system svc-multiplex-test
fi
if ! id svc-multiplex-test >/dev/null 2>&1; then
  useradd --system --gid svc-multiplex-test --home-dir /var/lib/multiplex-test \
    --create-home --shell /usr/sbin/nologin svc-multiplex-test
fi

install -d -o root -g root -m 0755 /opt/multiplex-test-releases
install -d -o svc-multiplex-test -g svc-multiplex-test -m 0700 /var/lib/multiplex-test
install -d -o admin -g admin -m 0700 /home/admin/multiplex-test-incoming
install -o root -g root -m 0644 "$HERE/multiplex-test.service" /etc/systemd/system/multiplex-test.service

if [ ! -f /etc/multiplex-test.env ] || [ -L /etc/multiplex-test.env ]; then
  printf '%s\n' '/etc/multiplex-test.env must be created as a regular root-owned file before provisioning' >&2
  exit 1
fi
chown root:root /etc/multiplex-test.env
chmod 0600 /etc/multiplex-test.env

install -o root -g root -m 0755 "$HERE/deploy-multiplex-test" /usr/local/bin/deploy-multiplex-test
install -o root -g root -m 0755 "$HERE/multiplex-test-config-check" /usr/local/bin/multiplex-test-config-check
cat > /etc/sudoers.d/admin-deploy-multiplex-test <<'EOF'
admin ALL=(root) NOPASSWD: /usr/local/bin/deploy-multiplex-test *
EOF
chmod 0440 /etc/sudoers.d/admin-deploy-multiplex-test
visudo -cf /etc/sudoers.d/admin-deploy-multiplex-test

[ -L "$SITE_LINK" ] || { printf '%s\n' "$SITE_LINK must be the active harmonizer site symlink" >&2; exit 1; }
[ "$(readlink -f "$SITE_LINK")" = "$SITE" ] || { printf '%s\n' "$SITE_LINK points at an unexpected site" >&2; exit 1; }
[ -f "$SITE" ] && [ ! -L "$SITE" ] || { printf '%s\n' "$SITE is missing or unsafe" >&2; exit 1; }
SITE_BACKUP="$(mktemp /var/backups/harmonizer.pre-multiplex-test.XXXXXX)"
SNIPPET_BACKUP=''
cp -a -- "$SITE" "$SITE_BACKUP"
if [ -e "$SNIPPET" ]; then
  [ -f "$SNIPPET" ] && [ ! -L "$SNIPPET" ] || { printf '%s\n' "$SNIPPET is unsafe" >&2; exit 1; }
  SNIPPET_BACKUP="$(mktemp /var/backups/multiplex-test-snippet.XXXXXX)"
  cp -a -- "$SNIPPET" "$SNIPPET_BACKUP"
fi
restore_nginx() {
  install -o root -g root -m 0644 "$SITE_BACKUP" "$SITE"
  if [ -n "$SNIPPET_BACKUP" ]; then
    install -o root -g root -m 0644 "$SNIPPET_BACKUP" "$SNIPPET"
  else
    rm -f -- "$SNIPPET"
  fi
}
install -o root -g root -m 0644 "$HERE/multiplex-test.nginx.conf" "$SNIPPET"
python3 - "$SITE" "$MARKER" <<'PY'
import pathlib, sys
site = pathlib.Path(sys.argv[1])
marker = sys.argv[2]
include = "    include /etc/nginx/snippets/multiplex-test.conf;"
text = site.read_text(encoding="utf-8")
if include not in text:
    if text.count(marker) != 1:
        raise SystemExit("active harmonizer TLS block marker is not unique")
    text = text.replace(marker, marker + "\n" + include, 1)
    site.write_text(text, encoding="utf-8")
PY
if ! nginx -t; then
  restore_nginx
  nginx -t
  printf '%s\n' 'test route failed nginx validation; original configuration restored' >&2
  exit 1
fi
if ! systemctl reload nginx; then
  restore_nginx
  nginx -t
  systemctl reload nginx
  printf '%s\n' 'test route reload failed; original configuration restored' >&2
  exit 1
fi
rm -f -- "$SITE_BACKUP"
[ -z "$SNIPPET_BACKUP" ] || rm -f -- "$SNIPPET_BACKUP"

systemctl daemon-reload
systemctl disable multiplex-test.service >/dev/null 2>&1 || true
systemctl is-active --quiet multiplex-test.service && { printf '%s\n' 'multiplex-test.service must be stopped before provisioning completes' >&2; exit 1; }
printf '%s\n' 'Provisioned isolated multiplex-test deployment, route, and narrow admin sudo lane.'
printf '%s\n' 'The service remains stopped and disabled until an exact release is deployed.'
