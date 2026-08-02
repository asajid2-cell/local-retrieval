# wp-muxd fold

## Result

The PC and relay halves now use one host input frame: `t:"i"`.

- `muxd` loads a DPAPI-protected principal registry from
  `C:\Users\Ahmed\muxd\principal-registry.dpapi`.
- `muxctl principal provision <principal> <key> <sessionUuid> <public-key.pem>` creates or extends
  that registry. A muxd restart is required after provisioning.
- The protected registry owns the stable muxd `instanceId`; each session owns an immutable
  persisted `sessionUuid`.
- A presented proof is always verified strictly. `audit` accepts only proofless legacy `d` input;
  `enforce` rejects it. Invalid, forged, expired, stale, or replay-conflicting proof-bearing input
  never falls back to legacy.
- The relay forwards signed input as `t:"i"`. When the connected protocol-4 host lacks
  `inputDurable`, it extracts `auth.bodyB64` and sends legacy `d` for old-host compatibility.
- Principal refusals are correlated by session and intent to the originating viewer. That viewer
  closes with code `1008` and a readable reason; other viewers remain connected.
- Deployment checks the local conduit contract before copying. Enforce additionally requires a
  provisioned protected registry. The current live runtime is copied to
  `C:\Users\Ahmed\muxd\rollback\pre-input-durable` before replacement.

## Runtime evidence

- `python -m unittest tests.test_host_input_intent tests.test_input_intent_persistence`
  from `muxd/`: 46 passed. This includes a real Windows ConPTY write from a verified P-256 input,
  DPAPI round-trip, forged-proof refusal, audit/enforce behavior, replay decisions, and session UUID
  restore.
- `node --test relay/tests/input-durable-compat.test.js relay/tests/old-host-compat.test.js relay/tests/input-lease.test.js`:
  11 passed through real relay WebSockets. This covers old/new host negotiation, the single `i`
  frame, downgrade behavior, and origin-only visible refusal.
- `python -m unittest tests.test_deploy_manifest` from `muxd/`: 11 passed, including an actual
  PowerShell subprocess proving enforce deployment exits under an empty temporary profile before
  copying.
- Python bytecode compilation, Node syntax checks, PowerShell parsing, and `git diff --check`
  passed.

The broader `python -m unittest tests.test_muxd_state` run had 88 passes and one failure:
`test_relay_output_backlog_is_bounded` expected queue size 2 and observed 1. No input-durable change
touches that output-backlog implementation; the isolated test reproduces the same failure.

## Deployment gate

Do not deploy enforce yet. The trusted browser-origin signer and its principal-key custody are
outside this charter's write scope (`relay/public/**` is excluded). The backend contract is
reachable and provisionable, but enforce would correctly reject ordinary unsigned browser typing
until that signer is landed and its public key is provisioned.

Audit deployment, apex only:

```powershell
Set-Location Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\deploy-muxd.ps1 -AuthzMode audit
```

Provision a browser principal after obtaining its P-256 public key and the target `sessionUuid`:

```powershell
python .\muxd\muxctl.py principal provision <principalId> <keyId> <sessionUuid> <public-key.pem>
Start-ScheduledTask -TaskName MuxdSessionHostRestart
```

Enforce deployment after the trusted signer is live and runtime socket verification passes:

```powershell
Set-Location Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\deploy-muxd.ps1 -AuthzMode enforce
```

Exact rollback:

```powershell
$src = 'C:\Users\Ahmed\muxd\rollback\pre-input-durable'
$dst = 'C:\Users\Ahmed\muxd'
@('muxd.py','muxctl.py','host_input_intent.py') | ForEach-Object {
  Copy-Item -LiteralPath (Join-Path $src $_) -Destination (Join-Path $dst $_) -Force
}
Start-ScheduledTask -TaskName MuxdSessionHostRestart
```

No deployment was performed.
