# relay/ops — off-box state backup

The relay keeps everything it cannot rebuild in a handful of JSON files under
`MUX_STATE_DIR` (`relay/server.js`), each with a `.bak` sibling maintained by
`durable-state.js`. Lose the VPS and you lose all of it. `scripts/backup-state.sh`
copies exactly those files to the PC over the `win` ssh route the VPS already has.

Installing this on the VPS is an **ops step for the deploy owner** — nothing here
runs automatically, and `scripts/deploy-relay.sh` does not ship it (it deploys
`relay/` only, and the script lives at the repo root under `scripts/`).

## Install

```sh
# 1. Put the script somewhere the unit can reach it.
scp scripts/backup-state.sh harmonizer@192.168.1.142:/tmp/mux-backup-state.sh
ssh harmonizer@192.168.1.142 'sudo install -m 0755 /tmp/mux-backup-state.sh /usr/local/bin/mux-backup-state.sh'

# 2. Confirm the PC route works for the *service* user before trusting the timer.
ssh harmonizer@192.168.1.142 'ssh -o BatchMode=yes win true && echo win-route-ok'

# 3. Prove one run by hand. This touches the network and writes to the PC.
ssh harmonizer@192.168.1.142 '/usr/local/bin/mux-backup-state.sh --state-dir ~/multiplex-app --verify'

# 4. Install the units.
scp relay/ops/mux-relay-backup.service relay/ops/mux-relay-backup.timer harmonizer@192.168.1.142:/tmp/
ssh harmonizer@192.168.1.142 '
  sudo install -m 0644 /tmp/mux-relay-backup.service /tmp/mux-relay-backup.timer /etc/systemd/system/ &&
  sudo systemctl daemon-reload &&
  sudo systemctl enable --now mux-relay-backup.timer &&
  systemctl list-timers mux-relay-backup.timer'
```

If the relay lives somewhere other than `/home/harmonizer/multiplex-app`, or runs as
another user, edit `--state-dir`, `User=`, `Group=` and `ReadOnlyPaths=` in the
service before installing.

## Check on it

```sh
systemctl list-timers mux-relay-backup.timer     # when it last ran, when it runs next
journalctl -u mux-relay-backup.service -n 50     # why the last run failed
sudo systemctl start mux-relay-backup.service    # force a run now
ssh win 'ls -lt mux-relay-backups | head'        # what actually landed on the PC
```

`--verify` runs before the scp: the archive is extracted to a temp dir, every member
is checked against its recorded sha256 and byte-compared with the live state file, and
the refusal sweep runs again on the extracted tree. A bad archive fails the unit
instead of overwriting a good backup on the far side.

## Trust boundary

The archive carries relay-tier state only — project registry, queued app commands,
pins, upload metadata, rename intents, and the `.bak` siblings. It never carries muxd
identity keys, client private keys, local-control credentials, principal registries or
session ACL state; the script's allowlist excludes them and a name-based refusal sweep
aborts the run if one ever shows up anyway.

What it does carry inherits the relay's exposure profile: plaintext transcript
references, archive indexes, signed intent envelopes, metadata and timing. **The
backup destination on the PC is as sensitive as the relay host.** Full write-up:
`relay/tests/fixtures/backup-state/EXPOSURE.md`.

## Restore

1. Stop the relay (`pm2 stop multiplex` / `sudo systemctl stop multiplex`).
2. `tar -xzf mux-relay-state-<stamp>.tgz` — files land under `relay-state/`.
3. Read `relay-state/EXPOSURE.txt`, then copy the JSON files (and their `.bak`
   siblings) into `MUX_STATE_DIR`.
4. Start the relay. Identity, credentials, principals and ACLs are re-established on
   the host that owns them — they are never restored from this archive.

## Local check, no VPS needed

```sh
cd relay && bash ../scripts/backup-state.sh --dry-run-to /tmp/mux-backup-test \
  --state-dir tests/fixtures/backup-state --verify
node --test tests/backup-state.test.js
```
