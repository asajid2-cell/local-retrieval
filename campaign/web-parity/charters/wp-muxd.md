# Charter — branch mind `wp-muxd` (finish the input-durable contract; do NOT roll back)

The owner's instruction: implement this properly rather than reversing course. Today the apex
rolled the LIVE `~/muxd` back to a pre-principal build so a surprise restart could not break web
typing. That rollback is a stopgap and must be retired by finishing the contract, not by keeping
the old code.

Repo: Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval (master). Persistent tandem peer; fan
out sealed junior lanes (luna@max) after freezing contracts; re-run every verifier yourself.

## The exact break (apex-verified — start from these, do not re-derive)

- `muxd/muxd.py:1076-1095`: `PROTOCOL = 4`, CAPS include `inputDurable`.
  `PRINCIPAL_ENDPOINT = host_input_intent.PrincipalEndpoint()` is EMPTY until pairing provisions a
  principal, and the comment states the intent: "refuse everything — the correct posture, not a gap."
- `muxd/muxd.py:4750`: every relay host-link `i` frame goes through `authorize_relay_input(...)` →
  `host_input_intent.verify_host_input_frame(...)`. No accepted proof ⇒ refusal, zero PTY writes.
- `relay/server.js:3478` sends ordinary typing as a PLAIN, unsigned `{t:'i', s, d}`.
- `relay/lease-conduit.js:51,73` emit `{t:'iw', s, e}` — a frame type muxd has NO handler for.

Net: the two sides implemented different halves of one design. Deployed muxd (rolled back, no
principal machinery) works; the repo's muxd refuses all web typing. Read
`muxd/host_input_intent.py` in full — it is the specification for what a valid proof is.

## What to build

1. **Provisioning/pairing**: the path that actually puts a principal into `PrincipalEndpoint`, so
   "empty ⇒ refuse everything" becomes a real, reachable state rather than a permanent brick.
   Decide and DOCUMENT where the principal material lives on this machine (keysafe/DPAPI is the
   house pattern for secrets — see `~/.keysafe`; never a plaintext file in the repo).
2. **The relay must send provable input**: reconcile `i` vs `iw` — either muxd learns `iw`, or the
   relay signs `i`. Pick ONE and delete the other path; two half-implemented frame types is the bug.
3. **A refusal must be visible, not silent.** Today a refused frame logs on the PC and the browser
   just sees nothing happen. The viewer must be told its keystroke was refused and why.
4. **Backwards compatibility is REQUIRED**: a relay that does not sign must still drive an older
   muxd, and the new muxd must not brick if the relay is older. State the negotiation (a cap is
   already advertised — use it) and test both mixed pairings.

## Verification (binding — no source-only claims)

Real tests through `relay/tests/harness.js` for the relay side, and Python tests for muxd
(`muxd/` has test conventions — follow them). Prove, at minimum: signed input reaches the PTY;
an unsigned/forged/replayed frame is refused and writes nothing; an older relay + new muxd still
types; a new relay + older muxd still types. Then: deploy is APEX-GATED. When you are ready, write
the exact deploy + rollback commands into your fold — `scripts/deploy-muxd.ps1` currently ships the
refusing build with no guard, so ALSO add a preflight to that script that refuses to deploy a muxd
whose input contract the local relay cannot satisfy.

Write-scope: `muxd/**`, `relay/lease-conduit.js`, the input path in `relay/server.js`,
`scripts/deploy-muxd.ps1`, tests for those. Fold: `campaign/web-parity/folds/wp-muxd.md`.
Pull the apex when the contract is frozen and again when ready to deploy.
