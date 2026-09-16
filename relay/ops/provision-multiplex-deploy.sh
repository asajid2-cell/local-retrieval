#!/usr/bin/env bash
# One-time root/admin provisioning for the Multiplex relay.
#
# There is deliberately NO lower-privileged deploy account, and this script writes no sudoers rule.
# The wrapper validates an archive but cannot authenticate its provenance -- the caller supplies both
# the commit and the sha256 -- so "may run the wrapper" is really "may install any code as the
# relay", whose systemd EnvironmentFile then hands that code MUX_HOST_TOKEN and HL_INTERNAL_KEY.
# That is a system change, which is tier 3 by definition, so releases run as admin.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run as root" >&2; exit 1; }

HERE="$(cd -- "$(dirname -- "$0")" && pwd)"
install -d -o root -g root -m 0755 /usr/local/lib/multiplex-deploy
install -o root -g root -m 0755 "$HERE/deploy-multiplex" /usr/local/bin/deploy-multiplex
install -o root -g root -m 0644 "$HERE/multiplex-app.service" /etc/systemd/system/multiplex-app.service
install -o root -g root -m 0644 "$HERE/mux-relay-backup.service" /etc/systemd/system/mux-relay-backup.service
install -o root -g root -m 0644 "$HERE/mux-relay-backup.timer" /etc/systemd/system/mux-relay-backup.timer
# The healthcheck triple used to exist only on the box, hand-installed in July and tracked nowhere, so
# nothing could re-create it and no reviewer could see it. It is provisioned here so the monitored
# state is a property of the release rather than of one machine's history.
install -o root -g root -m 0755 "$HERE/multiplex-healthcheck" /usr/local/bin/multiplex-healthcheck
install -o root -g root -m 0644 "$HERE/multiplex-healthcheck.service" /etc/systemd/system/multiplex-healthcheck.service
install -o root -g root -m 0644 "$HERE/multiplex-healthcheck.timer" /etc/systemd/system/multiplex-healthcheck.timer
install -d -o svc-multiplex -g svc-multiplex -m 0700 /var/lib/multiplex
[ -f /etc/multiplex-app.env ] || { echo "/etc/multiplex-app.env is missing" >&2; exit 1; }
chown root:root /etc/multiplex-app.env
chmod 0600 /etc/multiplex-app.env

rm -f /etc/systemd/system/multiplex-app.service.d/10-docker-group.conf
rm -f /etc/systemd/system/multiplex-app.service.d/harden.conf
rmdir /etc/systemd/system/multiplex-app.service.d 2>/dev/null || true
gpasswd -d svc-multiplex docker >/dev/null 2>&1 || true

# Releases run as admin. Refuse to complete if a lower-privileged deploy rule is present, so the
# retired tier-2 path cannot quietly reappear on a host provisioned by an older revision.
if [ -e /etc/sudoers.d/sub-deploy-multiplex ]; then
    echo "/etc/sudoers.d/sub-deploy-multiplex grants a lower-privileged deploy; remove it first" >&2
    exit 1
fi
systemctl daemon-reload

if systemctl show multiplex-app.service -p SupplementaryGroups --value | grep -qw docker; then
    echo "docker supplementary access still present; refusing completion" >&2
    exit 1
fi
if systemctl is-active --quiet multiplex-app.service; then
    systemctl restart multiplex-app.service
fi
# Enable and start the healthcheck; a provisioned-but-disabled timer is indistinguishable from the
# hand-installed state this replaces. `enable --now` is idempotent, so re-running provisioning is safe.
systemctl enable --now multiplex-healthcheck.timer
/usr/local/bin/deploy-multiplex --preflight
echo "Multiplex deploy wrapper provisioned; live service identity refreshed and preflight passed."
echo "Healthcheck installed and multiplex-healthcheck.timer is enabled."
echo "Human paging still requires MUX_ALERT_NTFY_URL in /etc/multiplex-app.env; without it both the"
echo "relay's ops-alert lane and this healthcheck are journal-only (see relay/ops/README.md)."
