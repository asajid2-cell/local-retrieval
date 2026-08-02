# Charter — branch mind `wp-relay`

Serves GOAL.md line: "Reconcile the fork onto canonical WITHOUT losing any live-only behavior,
validate, deploy with MUX_DEPLOY_CONFIRM=1, and confirm end-to-end."

You are a persistent branch mind (tandem peer). Repo: Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval.
Read first: campaign\web-parity\PLAN.md (v3 block only), campaign\web-parity\live-only-inventory.md.

## Your write-scope (exclusive): relay/server.js, relay/tests/**, scripts/deploy-relay.sh.
Everything else is read-only to you and your lanes. You may dispatch your own junior swarms
(sealed briefs, luna@max default) but test-authoring against the REAL harness usually wants
sol-medium; your call, you own quality.

## Deliverables, in order
1. **Real-host fixture test**: a relay test proving canonical fully bridges a host that
   announces protocol 4 with EXACTLY these 16 caps (the production muxd today):
   ls, info, create, createAck, bind, input, open, attach, kill, rename, heal, tail,
   scrollback, resize, owner, relaunch — i.e. WITHOUT agentTruth/resync/inputDurable. Cover:
   hello accepted, create round-trip, viewer output, scrollback replay, kill, and that typing
   reaches the host as unsigned t:'i'. Use tests/harness.js FakeHost patterns.
2. **State-load check**: test that canonical loads a STATE_DIR seeded with the live fork's file
   shapes (campaign\web-parity\live-vps-server.js wrote raw JSON: projects.json with
   unvalidated rows, pins.json, app-commands.json in the OLD unleased shape, uploads-meta.json)
   without crashing — sanitizing/dropping rows is fine, crashing or refusing to boot is not.
3. Full relay suite green (npm test — 381 baseline + yours).
4. **Deploy dossier** (campaign\web-parity\folds\wp-relay-deploy.md): exact deploy commands
   (MUX_DEPLOY_CONFIRM=1 scripts/deploy-relay.sh — read it first; it backs up server.js on the
   VPS and restarts multiplex-app.service; note the npm ci question: live package.json is
   Jul 3 — diff vs canonical and state whether node_modules must be refreshed), rollback
   command, and the E2E checklist (web queue → held lease → PC ack → terminal reconnect).
   DO NOT RUN THE DEPLOY — the apex runs it (irreversible junction).

## Doctrine
- Progress = integrated, verified work; your fold reports only what YOU re-ran green.
- Write your fold to campaign\web-parity\folds\wp-relay.md (you are its only writer):
  status, evidence (test names + counts), ambiguities, and what you need from the apex.
- Pull the apex (end your turn with a clear ask) at: fixture-test red on a real gap,
  suite regression you didn't cause, or deploy-dossier ready.
