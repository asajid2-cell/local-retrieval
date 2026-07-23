# Protocol 4 field registry

<!-- Rows are anchored by frame + field so independently-authored sections compose on merge. -->

## `input.durable` — principal-authorized PTY input (cap `inputDurable`)

The relay host link carries `t:"i"` frames. The relay is a conduit, not an authority: such a
frame proves only that *the relay* asked for a write. muxd admits it to the PTY only when it
carries an `auth` object proving a registered principal signed for these exact bytes, on this
exact session, inside the expiry window. A frame without an accepted proof is answered with
`principal-proof-required` and performs **zero** PTY writes.

| Frame | Field | Type | Meaning |
| --- | --- | --- | --- |
| `i` | `auth.op` | string | Must be `input.durable`. |
| `i` | `auth.instanceId` | id | The muxd instance the proof was minted for. |
| `i` | `auth.principalId` | id | Who authorized the write. Becomes the intent scope `principal:<id>`. |
| `i` | `auth.keyId` | id | Which of the principal's keys signed. |
| `i` | `auth.sessionUuid` | id | muxd-generated durable session identity. **Not** `session_id` (the agent transcript/resume identity). |
| `i` | `auth.channelId` | id | Driving channel; checked against the write lease holder. |
| `i` | `auth.intentId` | id | Durable intent key, `[A-Za-z0-9._-]{1,128}`. |
| `i` | `auth.seq` | uint | Monotonic per-channel sequence, covered by the signature. |
| `i` | `auth.aclRevision` | uint | Must equal the grant's current revision; stale ⇒ `principal-acl-stale`. |
| `i` | `auth.leaseEpoch` | uint | Must equal the grant's current epoch; stale ⇒ `principal-lease-stale`. |
| `i` | `auth.issuedAtMs` | uint | Capture time. Max 2 s of future skew. |
| `i` | `auth.expiresAtMs` | uint | 5 s normal, **15 s hard maximum**. A longer window is refused, not clamped. |
| `i` | `auth.bodySha256` | hex(64) | SHA-256 of the decoded `bodyB64` bytes, hashed before anything parses them. |
| `i` | `auth.bodyB64` | base64 | The exact payload bytes. Required — the legacy `d` field is never a substitute. |
| `i` | `auth.sig` | base64 | P-256/SHA-256 signature, pinned 64-byte `r‖s`. DER is not accepted. |

Signed transcript (UTF-8, compact JSON, `separators=(",",":")`):

```
["mux-authz-v1", op, instanceId, principalId, keyId, sessionUuid, channelId,
 seq, leaseEpoch, aclRevision, issuedAtMs, expiresAtMs, intentId, bodySha256]
```

`replayPolicy` and `leaseToken` describe operation semantics and work distribution. They are
**not** authorization: a correct `replayPolicy` says what the operation means if it runs twice,
never who was allowed to run it.

### Refusal codes

| Code | Cause |
| --- | --- |
| `principal-proof-required` | No usable proof was presented at all. |
| `principal-proof-invalid` | Proof present but rejected: bad signature, body/digest mismatch, unknown or inactive key, wrong instance. |
| `principal-proof-expired` | Expired, future-dated, or a lifetime past the 15 s maximum. |
| `principal-session-mismatch` | Signed for a different `sessionUuid`. |
| `principal-not-authorized` | Principal holds no `drive` role on this session. |
| `principal-acl-stale` / `principal-lease-stale` | Cited revision/epoch no longer current. |
| `principal-lease-held` | Another channel holds the write lease. |

### Durable replay

Each executed intent persists `(principalId, keyId, intentId, bodySha256, outcome)` alongside the
fingerprint over `(principalId, keyId, sessionUuid, intentId, bodySha256)`.

| Second arrival | Result |
| --- | --- |
| Same fingerprint, settled (`completed`/`failed`) | Cached result returned; no second PTY write. |
| Different fingerprint | `intent id is already bound to a different payload`. |
| Same fingerprint, outcome never observed | Refused as uncertain and **never** auto-replayed — a client can re-key an intent, but nobody can un-type a command. |

Re-signing after a reconnect changes `issuedAtMs` only; the fingerprint is unchanged, so it is
recognized as the same operation rather than a conflicting one.
