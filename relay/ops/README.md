# Multiplex production operations

Production code lives in immutable directories under `/opt/multiplex-releases`. The
`/opt/multiplex-app` symlink selects one release. Durable state stays outside the
release under `/var/lib/multiplex`, owned by `svc-multiplex`.

`multiplex-app.service` runs as `svc-multiplex`, explicitly resets supplementary
groups, and has no Docker socket access. Nginx and `hl-auth` remain separate existing
security layers; a Multiplex deploy does not rewrite their keys or routes.

## One-time provisioning

Changing root-owned policy requires an authorized admin/root session. Stage this
directory on the VPS, inspect it, then run:

```sh
sudo bash relay/ops/provision-multiplex-deploy.sh
```

The provisioner:

- installs the fixed `/usr/local/bin/deploy-multiplex` wrapper;
- installs the consolidated non-root service and backup units;
- removes the stale Docker supplementary-group drop-in;
- locks `/etc/multiplex-app.env` to `root:root` mode `0600`;
- reloads systemd, restarts the existing relay once so its live process drops Docker
  access immediately, and verifies the effective unit, runtime UID/GID/groups, nginx,
  and loopback `hl-auth` boundary.

It writes **no sudoers rule**, and deliberately provides no lower-privileged deploy
account. The wrapper is not a privilege boundary: it validates an archive but cannot
authenticate its provenance, because the caller supplies both the commit and its
sha256. Anyone allowed to run it is therefore allowed to install any code as the
relay, and the service's systemd `EnvironmentFile` hands that code `MUX_HOST_TOKEN`
and `HL_INTERNAL_KEY`. That is a system change, which is tier 3 by definition, so
provisioning and releases both run as admin. The wrapper still earns its place as a
safety mechanism — content-addressed releases, archive validation, drain, atomic
swap, health gate with automatic rollback — but not as a way to let a read-only
account deploy.

Verify before the first release:

```sh
ls -l /usr/local/bin/deploy-multiplex        # root-owned, mode 0755
systemctl show multiplex-app.service -p User -p Group -p SupplementaryGroups
systemctl cat multiplex-app.service
pid=$(systemctl show multiplex-app.service -p MainPID --value)
grep -E '^(Uid|Gid|Groups):' "/proc/$pid/status"
```

## Routine release

The worktree must be clean because the archive is built from `HEAD`, stamped with the
full commit hash, and hashed before upload:

```sh
bash scripts/deploy-relay.sh
```

The client first runs the wrapper's read-only preflight, so missing provisioning or
security drift is reported before an archive is built or uploaded.

The client uploads only to `harmonizer-admin`. The root-owned wrapper accepts exactly a
40-hex commit and 64-hex SHA-256, quarantines and validates the archive, refuses links
or path traversal, installs dependencies as `svc-multiplex`, makes code root-owned,
drains with SIGTERM, atomically switches the release symlink, checks `/api/health`,
and restores the previous release on failure.

## Backup

The deploy wrapper installs `mux-backup-state.sh`. It enables
`mux-relay-backup.timer` only after a strict, non-interactive `win` SSH probe succeeds
as `svc-multiplex`; otherwise it leaves the timer disabled instead of generating
hourly failures. Once enabled, the backup unit repeats that probe as an
`ExecCondition`, so a temporary PC outage skips a run cleanly without disabling the
schedule. The timer reads only `/var/lib/multiplex`, verifies a restore roundtrip,
and sends the archive over that route. A timer activation problem is logged as a
backup warning after a healthy application release; it does not roll the app back.

```sh
systemctl list-timers mux-relay-backup.timer
journalctl -u mux-relay-backup.service -n 50
sudo -u svc-multiplex /usr/local/bin/mux-backup-state.sh \
  --state-dir /var/lib/multiplex --verify
ssh win 'ls -lt mux-relay-backups | head'
```

The archive includes only relay-tier durable state: projects, queued app commands,
pins, upload metadata, rename intents, and `.bak` siblings. It refuses identity keys,
private keys, credentials, principal registries, and ACL state. The backup destination
still inherits the relay's exposure profile and must be protected accordingly.

## Restore

1. Stop `multiplex-app.service`.
2. Extract `mux-relay-state-<stamp>.tgz`.
3. Copy the JSON files and `.bak` siblings from `relay-state/` into
   `/var/lib/multiplex`.
4. Set ownership to `svc-multiplex:svc-multiplex`, mode `0600`, and restart the unit.

## Local verification

```sh
cd relay
bash ../scripts/backup-state.sh --dry-run-to /tmp/mux-backup-test \
  --state-dir tests/fixtures/backup-state --verify
npm test
```
