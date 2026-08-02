# wp-relay Fold

## Status

Relay deliverables 1, 2, and 4 are complete. Deliverable 3 is blocked by a deterministic full-suite
regression in the concurrent discovery lane. No deploy was run.

Canonical required no `relay/server.js` compatibility change. I added the two charter fixtures and
repaired the deploy script's post-restart health probe so it reads the real service port from
`/etc/multiplex-app.env` instead of scraping a numeric literal that no longer exists in `server.js`.

## Evidence

Focused tests rerun green:

- `canonical fully bridges the production protocol-4 16-cap muxd`
  - exact caps: `ls, info, create, createAck, bind, input, open, attach, kill, rename, heal, tail,
    scrollback, resize, owner, relaunch`
  - hello accepted
  - create/createResult round trip
  - viewer scrollback replay and live output
  - viewer typing reaches the host as exactly unsigned `{t:"i",s,d}`
  - kill/killed round trip and viewer cleanup
- `canonical boots from the live fork raw state file shapes`
  - legacy raw `projects.json` is allowlist-normalized
  - old unleased `app-commands.json` rows migrate to leased commands; invalid rows drop
  - legacy `pins.json` loads
  - legacy `uploads-meta.json` plus kept bytes loads while `pcPath` is dropped
- `the post-restart health check reads the deployed port from the service environment`

The full command reached green once before the concurrent picker migration landed:

```bash
cd relay
npm test
```

That snapshot was 385 total, 384 passed, 1 skipped, 0 failed: the charter's 381 baseline passes plus
three new passing tests.

Current-tree result after `relay/public/picker.js` and its fixture changed:

- 385 total
- 383 passed
- 1 skipped
- 1 failed
- failure: `RENDER: only offered rows are drawn, typing narrows them, and a dead query still says something`
  at `relay/tests/resume-picker-dom.test.js:228`
- actual status: `PC archive connected - resume may queue until the desktop app is open`
- stale assertion: status must include `offline`

The failing file rerun alone reproduces 4 passed, 1 failed. It is outside this lane's write scope.
My changed tests rerun together remain 7 passed, 0 failed.

Additional checks rerun green:

```bash
bash -n scripts/deploy-relay.sh
git diff --check
```

The deploy refusal gate was also exercised with `MUX_DEPLOY_CONFIRM=0` and exited 2 without touching
the VPS.

## Ambiguities

- The discovery lane must decide whether the new connected-PC status should intentionally avoid the
  word `offline`, then update the DOM assertion to the chosen contract. The implementation and fixture
  already agree on the new PC discovery response shape.
- The rollback built into the current migration is server-only. The dossier explicitly warns the apex
  not to touch `/var/lib/multiplex` and to inspect static-file differences before any broader rollback.
- `npm ci` is not needed: live and canonical production dependencies are already exact matches.
  Canonical only adds test-time xterm dev dependencies.

## Apex Ask

First reconcile `resume-picker-dom.test.js:228` with the discovery lane's new connected-PC status and
rerun the full relay suite to 384 passed, 0 failed. Then run the gated deploy from
`wp-relay-deploy.md`, capture the printed backup timestamp, and execute the production E2E checklist
through held lease, PC acknowledgement, and terminal reconnect.
