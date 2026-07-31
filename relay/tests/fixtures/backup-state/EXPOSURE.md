# Backup fixture — what a relay backup carries, and what it costs

This directory is a hermetic stand-in for a live relay `MUX_STATE_DIR`
(`relay/server.js`: `MUX_STATE_DIR || __dirname`). `scripts/backup-state.sh` is
pointed at it by `relay/tests/backup-state.test.js` and by the leaf verifier, so
neither needs a running relay or a reachable VPS.

## What is in here on purpose

Backed up (the script's allowlist, plus each file's `.bak` sibling — `durable-state.js`
falls back to the sibling when the primary is torn, so a backup without them is only
half a backup):

- `projects.json`, `projects.json.bak`
- `app-commands.json`
- `pins.json`, `pins.json.bak`
- `uploads-meta.json`
- `rename-intents.json`

Decoys that must **never** appear in an archive. They are the trust amendment made
executable: if a future edit widens the allowlist, the test fails loudly instead of
quietly shipping keys to a second machine.

- `muxd-identity.json` — muxd identity keys
- `client-private.key` — client private keys
- `local-control.cred` — local-control credentials
- `principals.json` — principal registry
- `session-acl.json` — session ACL state

All five decoys contain obvious `FIXTURE-NOT-A-REAL-*` placeholder text.

## The exposure this backup creates

Excluding secrets is not the same as the archive being harmless. Everything the
backup *does* carry inherits the relay's own exposure profile, and copying it off-box
extends that profile to a second machine and to whatever moves it there:

- **Plaintext transcript references** — project/tab records name paths, hosts and
  sessions, and point at scrollback the relay serves in the clear.
- **Archive indexes** — `uploads-meta.json` is a full index of what was uploaded,
  under what name, at what size. The blobs themselves are excluded; the index alone
  still tells you what exists.
- **Signed intent envelopes** — queued `app-commands.json` entries and
  `rename-intents.json` records are replayable statements of what the owner asked
  for, complete with their targets.
- **Metadata and timing** — every record is timestamped. The archive is a timeline of
  when the owner worked, on what, and from where.

So: **the backup destination is exactly as sensitive as the relay host.** It is not a
lower-trust tier just because the keys were stripped. Restoring is a
relay-stopped, owner-supervised operation, and the destination directory on the PC
deserves the same handling as the relay's own state dir.

Excluded state is excluded permanently, not deferred. Identity keys, credentials,
principal registries and session ACLs are re-established on the host that owns them —
they are never restored from an off-box archive, because an archive is exactly the
artifact an attacker would rather steal than forge.
