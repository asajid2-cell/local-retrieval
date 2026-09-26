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
- installs the healthcheck triple and enables `multiplex-healthcheck.timer`;
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

### The command-bridge credential

`MUX_COMMAND_BRIDGE_TOKEN` is the only thing that authorizes the PC to consume the
command queue, and it has **two halves that must be minted together**. It gates three
things, all of which fail closed when it is unset:

- `POST /api/app-commands/lease` and `/api/app-commands/:id/ack` — the PC's only command
  channel. Without it the web can enqueue but nothing ever drains the queue, so every
  Start-chat / Resume / metadata action waits out its full 30s deadline and reports
  "PC bridge did not confirm mux start".
- `POST /api/principal-auth` — transcript read authorization.
- `POST /api/transcripts/:sessionId` — transcript page push.

The loopback address is **not** the trust boundary here: the relay and every app share
host networking, so `127.0.0.1` alone would let any co-resident process drain the queue.
The token is what distinguishes the PC's poller from a co-resident impostor.

Two artifacts, and neither is generated automatically:

```sh
# 1) the relay's half — server env, root:root 0600, like every other secret in the file
tok=$(openssl rand -hex 32)
printf 'MUX_COMMAND_BRIDGE_TOKEN=%s\n' "$tok" | sudo tee -a /etc/multiplex-app.env
sudo chown root:root /etc/multiplex-app.env && sudo chmod 0600 /etc/multiplex-app.env

# 2) the PC consumer's half — a header FILE the PC's ssh session can read
#    /home/harmonizer/.config/mux/command-bridge.header
sudo install -d -o harmonizer -g harmonizer -m 0700 /home/harmonizer/.config/mux
printf 'X-Mux-Command-Bridge: %s\n' "$tok" \
  | sudo tee /home/harmonizer/.config/mux/command-bridge.header >/dev/null
sudo chown harmonizer:harmonizer /home/harmonizer/.config/mux/command-bridge.header
sudo chmod 400 /home/harmonizer/.config/mux/command-bridge.header
unset tok
```

The header file's form is **not** free: the PC passes it to `curl --header "@$h"`, which
parses one `Name: value` pair per line (this is libcurl's netrc-style header file, not a
netrc file). A netrc body (`machine … login … password …`) is read as a *header* and
rejected with 403. The PC also refuses to use the file at all — exiting 77 before it ever
calls curl — unless it is a regular file, non-empty, readable, owned by the ssh user, and
exactly `?r??------` (0600/0400/0500). That is why the file is `400 harmonizer:harmonizer`
and not root-owned: `harmonizer` is the account the PC's ssh route authenticates as.

Verify without printing the value:

```sh
# configured at all? (names only)
sudo grep -c '^MUX_COMMAND_BRIDGE_TOKEN=' /etc/multiplex-app.env
sudo stat -c '%A %U:%G %s' /home/harmonizer/.config/mux/command-bridge.header
# the real gate: no credential must be 403, never 503
curl -s -o /dev/null -w '%{http_code}\n' -X POST \
  http://127.0.0.1:7682/api/app-commands/lease \
  -H 'Content-Type: application/json' -d '{"owner":"probe","limit":1,"waitMs":0}'
# the real consumer path, as the ssh user, using the file
sudo -u harmonizer bash -c 'h="$HOME/.config/mux/command-bridge.header"; \
  curl -s -o /dev/null -w "%{http_code}\n" -X POST \
  http://127.0.0.1:7682/api/app-commands/lease \
  -H "Content-Type: application/json" --header "@$h" \
  -d "{\"owner\":\"probe\",\"limit\":1,\"waitMs\":0}"'
```

`503 {"error":"command bridge credential not configured"}` means the *server* half is
missing. `403 {"error":"local command bridge credential required"}` from the consumer path
means the server half is fine and the *file* half is missing, mis-shaped, or mis-owned —
check the exit-77 preconditions above before suspecting the token. A `403` from the
unauthenticated probe is the healthy answer.

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

## Monitoring and alerting

Two independent watchers cover different failures, and only one of them can survive the
relay dying:

- `multiplex-healthcheck.timer` runs `/usr/local/bin/multiplex-healthcheck` every minute.
  It reads `/api/health` over loopback as `harmonizer` and exits non-zero only for a
  genuine fault (host link down, **host connected but stalled**, protocol mismatch, missing
  host capabilities, PC unreachable, **PC archive mount dead behind a live tunnel**,
  persistence not writing, legacy tmux sessions, a pending
  rename intent, an outstanding upload warning, a store recovered but not rewritten, an
  unsupported node).
  A stale desktop push is **not** a fault: `ok` is `!degraded`, and `degraded` includes
  the projects bridge, which is false whenever no desktop app is running. Failing on that
  made the unit fail ~614 times in four days for a relay that was serving correctly, and a
  monitor that cries wolf every minute trains an operator to ignore it. Those states are
  printed as `WARN` lines on every run at exit 0 instead.

  **The stalled-host check is the one that catches a link that is up but dead.** muxd pushes
  a `t:"sessions"` frame every 5s, so `/api/health.host.frameAgeMs` measures the heartbeat
  and `host.frameStale` is the relay's own staleness verdict (`MUX_HOST_FRAME_STALE_MS`,
  default 15000). `host.connected` is TCP state and stays `true` through a stalled event
  loop — which is exactly how muxd once stopped moving for three days while `/api/health`
  reported a healthy host. A down link reports `frameAgeMs: null` and `frameStale: false`
  (there is no frame to be late); the check never fires for it. The OK line prints
  `frameAge=…` (or `n/a`) so the heartbeat is readable on healthy runs too.

  **The archive-mount check is the same lesson one layer out.** `pc.reachable` is a TCP
  connect to the PC's sshd, and it stays green when the PC's archive server has died: the
  reverse tunnel (`ssh -N -R 8765`) never notices its upstream is gone, so the VPS port
  stays bound and drops every forwarded connection. `/multiplex/pc` is then a 502 to every
  browser while the relay reports a healthy PC — which is how a dead archive server went
  unreported for ~15h, with the site reading "PC archive unavailable" until someone opened
  the desktop app. So the relay also probes the mount the browser actually uses
  (`pc.archive`, loopback `:8765/healthz`, `MUX_PC_ARCHIVE_URL` to override) and requires a
  real answer carrying `service: codex-local-retrieval`, because a TCP connect would succeed
  against the bound-but-dead port and a bare 200 could come from anything else on the box.
  A failed probe is a `degraded` term, a healthcheck `FAIL`, and a named reason in the alert
  body; it is silent when the PC link itself is down, since that already says why.
- The relay's in-process ops-alert lane (`relay/health-alerts.js`) pushes on degraded
  **edges** with sustain windows and 30-minute dedupe. It starts whether or not a topic is
  configured; an unset topic selects the journal sink rather than switching the lane off.
  The lane cannot report the relay's own death, which is why the timer exists. Alongside the
  aggregate it carries a `host-stalled` condition (5min dwell, same source field), so a
  silent muxd pages by name instead of only colouring the degraded aggregate.

```sh
systemctl list-timers multiplex-healthcheck.timer
journalctl -u multiplex-healthcheck -n 20          # OK / WARN / FAIL lines
journalctl -u multiplex-app -n 20 | grep ops-alert # alert edges, journal sink
```

### Turning on real paging

Both watchers need the same one secret, and neither reaches a human without it:

```sh
# as root, in /etc/multiplex-app.env — the topic IS the credential, never commit it
MUX_ALERT_NTFY_URL=https://ntfy.sh/<unguessable-topic>
```

then `systemctl restart multiplex-app.service`. The relay logs one line at startup naming
the sink it chose (`ops alerts: ntfy` or `ops alerts: journal only`), so the current
posture is never a guess. With no topic both watchers are journal-only by design: alerts
still fire, dedupe, and print — they are simply read with `journalctl` instead of received
as a push.

`MUX_ALERT_NTFY_URL` is not the attention lane's key. That lane is constructed bare and
falls back to `MUX_NTFY_URL`; setting one does nothing for the other.

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
```

The archive includes only relay-tier durable state: projects, queued app commands,
pins, upload metadata, rename intents, and `.bak` siblings. It refuses identity keys,
private keys, credentials, principal registries, and ACL state. The backup destination
still inherits the relay's exposure profile and must be protected accordingly.

### Retention

The archive name carries a UTC stamp, so every run writes a NEW file. Left alone, an
hourly timer would leave ~8760 near-identical copies a year on the PC. Each successful
send therefore prunes the remote directory: the newest `--keep-recent` archives
(default 24) are kept outright, then the newest archive of each older day is kept for
`--keep-daily` more days (default 30). The copy count tracks the retention window, not
the cadence — hourly and daily schedules land at the same ceiling.

Ordering is deliberate. The sweep runs **after** a successful `put`, so an interrupted
run leaves one extra copy rather than deleting the oldest backup to make room for one
that never landed. A failed listing or prune is a **warning**, not a unit failure: the
backup itself succeeded, and failing here would cost a 24-hour gap over a directory
that only needs a human to look at it. Every retention line in the journal is prefixed
`retention:`.

The decision logic runs entirely on the VPS. The far side is a Windows OpenSSH server
with a non-POSIX shell, so it is only ever asked to list and delete over SFTP, never to
evaluate anything. Only names matching the exact archive shape are candidates, so a
shared directory keeps its foreign files.

```sh
# what WOULD a run delete? offline, against a listing — touches no network, no files
ssh win 'ls -1 mux-relay-backups' | awk '{print $NF}' \
  | sudo -u svc-multiplex /usr/local/bin/mux-backup-state.sh --prune-plan
# send now and skip retention entirely
sudo -u svc-multiplex /usr/local/bin/mux-backup-state.sh --state-dir /var/lib/multiplex --no-prune
```

`--prune-plan` validates `--keep-recent`/`--keep-daily` exactly as a real run does, so
a plan shown for values a real run would refuse is never printed. `--keep-recent 0` is
rejected: a sweep must always be able to keep the archive it just sent.

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

The healthcheck's exit code is a paging contract, so it is tested against synthetic health
blobs rather than trusted by inspection — including the closed-desktop-app state that must
stay a warning:

```sh
node --test --test-concurrency=1 tests/healthcheck-severity.test.js
```
