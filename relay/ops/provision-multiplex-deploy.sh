#!/usr/bin/env bash
# One-time root/admin provisioning. Routine releases use harmonizer-sub afterward.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run as root" >&2; exit 1; }

HERE="$(cd -- "$(dirname -- "$0")" && pwd)"
install -d -o root -g root -m 0755 /usr/local/lib/multiplex-deploy
install -o root -g root -m 0755 "$HERE/deploy-multiplex" /usr/local/bin/deploy-multiplex
install -o root -g root -m 0644 "$HERE/multiplex-app.service" /etc/systemd/system/multiplex-app.service
install -o root -g root -m 0644 "$HERE/mux-relay-backup.service" /etc/systemd/system/mux-relay-backup.service
install -o root -g root -m 0644 "$HERE/mux-relay-backup.timer" /etc/systemd/system/mux-relay-backup.timer
install -d -o svc-multiplex -g svc-multiplex -m 0700 /var/lib/multiplex
[ -f /etc/multiplex-app.env ] || { echo "/etc/multiplex-app.env is missing" >&2; exit 1; }
chown root:root /etc/multiplex-app.env
chmod 0600 /etc/multiplex-app.env

rm -f /etc/systemd/system/multiplex-app.service.d/10-docker-group.conf
rm -f /etc/systemd/system/multiplex-app.service.d/harden.conf
rmdir /etc/systemd/system/multiplex-app.service.d 2>/dev/null || true
gpasswd -d svc-multiplex docker >/dev/null 2>&1 || true

cat > /etc/sudoers.d/sub-deploy-multiplex <<'EOF'
sub ALL=(root) NOPASSWD: /usr/local/bin/deploy-multiplex *
EOF
chmod 0440 /etc/sudoers.d/sub-deploy-multiplex
visudo -cf /etc/sudoers.d/sub-deploy-multiplex
systemctl daemon-reload

if systemctl show multiplex-app.service -p SupplementaryGroups --value | grep -qw docker; then
    echo "docker supplementary access still present; refusing completion" >&2
    exit 1
fi
if systemctl is-active --quiet multiplex-app.service; then
    systemctl restart multiplex-app.service
fi
/usr/local/bin/deploy-multiplex --preflight
echo "Multiplex deploy wrapper provisioned; live service identity refreshed and preflight passed."
