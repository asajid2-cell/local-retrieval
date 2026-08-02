# Relay Deploy Dossier

Status: deploy procedure ready, but deployment is blocked on the current full-suite regression recorded
in `wp-relay.md`. This branch mind did not run the deploy.

## Preflight

From the repository root:

```bash
cd /z/328/CMPUT328-A2/codexworks/301/mux-local-retrieval
cd relay && npm test && cd ..
bash -n scripts/deploy-relay.sh
```

Required suite result before deploy: 385 total, 384 passed, 1 skipped, 0 failed.

Current tree does not meet that gate: `resume-picker-dom.test.js` has one deterministic discovery-lane
assertion failure. Do not deploy until the apex or discovery lane fixes it and reruns `npm test` green.

The live systemd unit resolves to:

- code: `/opt/multiplex-app/server.js`
- state: `/var/lib/multiplex`
- user/group: `svc-multiplex:svc-multiplex`
- service: `multiplex-app.service`
- port: `7682`, sourced from `/etc/multiplex-app.env`

## Deploy

Irreversible junction; apex only:

```bash
cd /z/328/CMPUT328-A2/codexworks/301/mux-local-retrieval
MUX_DEPLOY_CONFIRM=1 scripts/deploy-relay.sh
```

The script:

1. Copies live `server.js` to `/opt/multiplex-app/server.js.pre-deploy-YYYYMMDD-HHMMSS`.
2. Streams canonical `relay/` to `/opt/multiplex-app`.
3. Sends `SIGTERM`, waits for the drain, and restarts `multiplex-app.service`.
4. Reads `PORT` from `/etc/multiplex-app.env` and requires `/api/health` to answer.

Record the exact backup filename printed by the script before continuing.

## npm ci Decision

Do not run `npm ci` for this deploy.

The live July 3 `package.json` and installed modules already have the same production dependencies and
exact installed versions as canonical:

- `express` 5.2.1
- `ws` 8.21.0

Canonical changes `main`, adds the real test script, and adds only test-time dev dependencies:
`@xterm/headless` 6.0.0 and `@xterm/addon-serialize` 0.14.0. The service does not require either.
Running `npm ci` would add test tooling but is unnecessary for runtime parity.

## Rollback

Replace `<STAMP>` with the timestamp printed by the deploy:

```bash
ssh harmonizer@192.168.1.142 '
  sudo cp /opt/multiplex-app/server.js.pre-deploy-<STAMP> /opt/multiplex-app/server.js &&
  sudo systemctl restart multiplex-app &&
  curl -fsS http://127.0.0.1:7682/api/health
'
```

This rollback restores `server.js`, the migration's behavior-bearing junction. If a static asset is
also implicated, stop and compare `/opt/multiplex-app` with the pre-deploy revision before restoring
individual files; do not overwrite `/var/lib/multiplex`.

## E2E Checklist

- Before deploy, keep a browser attached to a known disposable mux session and note a unique terminal
  marker already present in scrollback.
- Run the confirmed deploy command. Verify the browser reports a restart, reconnects promptly, replays
  the prior marker, and then displays a new line emitted by the PC host.
- Type a unique marker in the reconnected terminal. Verify it reaches muxd and appears in the terminal;
  this proves the production unsigned `t:"i"` path still works.
- Verify `/api/health` reports host connected, protocol `4`, `protocolOk:true`, and the production
  16-cap list.
- With the PC bridge holding its normal long-poll lease request open, trigger a harmless web app command
  such as opening a transcript or changing a disposable tab color.
- Capture the command id. Verify the held poll returns it with `status:"leased"`, a non-empty
  `leaseToken`, and the expected `leaseOwner`.
- Verify the PC performs the requested action and acknowledges with that lease token.
- Poll `/api/app-commands/<id>` from the authenticated web session until it reaches `done`; record the
  final detail.
- Kill the disposable mux session from the web. Verify muxd receives the kill, the web terminal closes,
  and the session disappears from `/api/sessions`.
- Reopen or recreate the disposable session and attach again. Verify scrollback, live output, typing,
  and resize remain functional after the full queue/ack lifecycle.

Rollback immediately on a failed health check, host protocol mismatch, command stuck leased after the
bridge's normal timeout, missing PC acknowledgement, failed terminal reconnect, or typing that does not
reach muxd.
