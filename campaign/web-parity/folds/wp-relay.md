# wp-relay Fold

## Status

Ready for apex deploy and production E2E. No deploy was run.

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

Final full command:

```bash
cd relay
npm test
```

Result: 385 total, 384 passed, 1 skipped, 0 failed. This is the charter's 381 baseline passes plus
three new passing tests.

Additional checks rerun green:

```bash
bash -n scripts/deploy-relay.sh
git diff --check
```

The deploy refusal gate was also exercised with `MUX_DEPLOY_CONFIRM=0` and exited 2 without touching
the VPS.

## Ambiguities

- One intermediate parallel full-suite run reported one failure, but its console output was truncated
  before the failing test name. An immediate complete rerun passed all 384 runnable tests. Focused
  reruns of every changed test were also green.
- The rollback built into the current migration is server-only. The dossier explicitly warns the apex
  not to touch `/var/lib/multiplex` and to inspect static-file differences before any broader rollback.
- `npm ci` is not needed: live and canonical production dependencies are already exact matches.
  Canonical only adds test-time xterm dev dependencies.

## Apex Ask

Run the gated deploy from `wp-relay-deploy.md`, capture the printed backup timestamp, and execute the
production E2E checklist through held lease, PC acknowledgement, and terminal reconnect. Pull this lane
back only if production evidence contradicts the green fixtures.
