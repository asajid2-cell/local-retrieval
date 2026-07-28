# muxd ↔ relay protocol field & capability registry

Every WebSocket frame type, field, and capability string on the muxd (PC host) ↔ relay link is
registered here. This file is the single place to look up what is already taken and the single
place to declare something new.

Enforced by `relay/tests/protocol-registry.test.js`, which reads `muxd/muxd.py` and
`relay/server.js` and fails if either side declares a capability or protocol number this document
does not carry.

- **Protocol number:** `4`

Both sides pin that number as a literal: `PROTOCOL = 4` in `muxd/muxd.py`, and
`REQUIRED_HOST_PROTOCOL = 4` in `relay/server.js`.

## The registry rule

**Add fields and capabilities. Never bump the protocol number.**

The relay does not negotiate a version range — it tests for *exact equality* and hangs up on a
mismatch. `announcedHostProtocol()` (`relay/server.js:213`) returns `null` unless
`announced.protocol === REQUIRED_HOST_PROTOCOL`, and the caller closes the socket with code `1008`
("host hello violated protocol"). `hostProtocolOk()` (`relay/server.js:265`) repeats the same
equality test on every request that needs the host.

So bumping `PROTOCOL` to `5` in muxd does not degrade the pairing — it **bricks** it. The relay
refuses the hello outright, every hosted session disappears from the web UI, and it stays broken
until *both* sides are redeployed simultaneously. Deploys are not simultaneous. Don't do it.

What to do instead:

1. **New frame type** → add a row to [Frame types](#frame-types) and a capability advertising it.
   An unknown `t` is ignored by the receiver's dispatch chain, so an old peer degrades quietly.
2. **New capability** → append the literal to `CAPS` in `muxd/muxd.py:907` and add a row to
   [Capabilities](#capabilities). Gate the feature on `hostSupportsCap('yourCap')`
   (`relay/server.js:271`), never on the protocol number.
3. **New field on an existing frame** → add a row to [Frame fields](#frame-fields). Make it
   optional with a safe default; the peer that has not shipped yet will not send it.
4. **Do not add to `REQUIRED_HOST_CAPS`** unless the capability already ships in a deployed muxd.
   That set is a hard gate — a capability listed there and missing from the host makes every
   create/kill/rename/tail path return `503` until muxd is restarted with the newer build.

Two field surfaces are *closed sets* and reject unknown keys instead of ignoring them. Extending
either one requires landing the muxd side first:

- **`create` frames**: `REMOTE_CREATE_FIELDS` (`muxd/muxd.py:2142`) is an exact allow-list. Any
  extra key trips `remote_create_violation()` and closes the socket with `1008`.
- **Session payloads**: `normalizeHostSession()` (`relay/server.js:171`) rebuilds each session from
  a fixed key list. Unregistered keys are silently dropped (not fatal), so a new session field is
  invisible to the web UI until the relay side is added here and there.

Registry entries describe **wire compatibility only**. Registering a field or capability records
that a name is taken and what shape it carries. It confers no authorization: a frame that names
`principalAuthV1` is not thereby authorized, and muxd still evaluates every proof on its own.

## Capabilities

Advertised by muxd in the `hello` frame's `caps` array, sourced from `CAPS` (`muxd/muxd.py:907`).

**Required** capabilities are members of `REQUIRED_HOST_CAPS` (`relay/server.js:164`); a host
missing any of them fails `hostProtocolOk()` and the relay treats it as protocol-mismatched.
**Optional** capabilities are feature-detected individually via `hostSupportsCap()`.

| Capability | Tier | Meaning |
| --- | --- | --- |
| `create` | required | Host accepts `create` frames and answers with `createResult`. |
| `createAck` | required | `createResult` carries `rid`/`ok`/`created`/`detail`/`retryable` and a `session` object on success — i.e. create is acknowledged, not fire-and-forget. |
| `kill` | required | Host accepts `kill` and answers `killed` + `sessions`. |
| `rename` | required | Host accepts `rename` with a `to` name. |
| `heal` | required | Host accepts `heal` to toggle a session's auto-resume policy. |
| `tail` | required | Host accepts `tail` and answers `tailr` with plain text. |
| `scrollback` | required | Host accepts `sb` requests and answers with a base64 `sb` replay. |
| `ls` | optional | Host can enumerate sessions on demand (local control port). |
| `info` | optional | Host answers an `info` probe with its protocol and caps. |
| `bind` | optional | Host can bind an existing session to a caller and return `bind-ok`. |
| `input` | optional | Host accepts keystroke input frames (`i`) for a live session. |
| `open` | optional | Host can open/adopt a session by identity. |
| `attach` | optional | Host supports viewer attach semantics. |
| `resize` | optional | Host accepts `resize` and applies remote PTY geometry. |
| `owner` | optional | Host reports/steers session ownership (`owner-ok`). |
| `relaunch` | optional | Host honours the `relaunch` flag on `create`. |
| `agentTruth` | optional | Host probes the OS about the session's own process tree and emits `agentTruth` + `agentStateSource` on every session payload. Advisory only: it never replaces `agentState`, and a stale or failed probe degrades to `agentStateSource: "heuristic"`. |
| `resync` | optional | Host emits `resync` when one session's byte-bounded egress queue overflows and its backlog was dropped at a frame boundary. A relay that lacks the handler simply ignores the frame and shows the gap; it is deliberately **not** in `REQUIRED_HOST_CAPS`, so older hosts keep connecting. |
| `inputDurable` | optional | Host admits a relay `i` frame to the PTY only when it carries a principal-signed `input.durable` proof for exactly those bytes on exactly that session, and replays an already-executed intent instead of re-running it. See [`input.durable`](#inputdurable--principal-authorized-pty-input-cap-inputdurable). |

### Reserved — terminal model (native prong)

Registered ahead of implementation so the names are not taken by anything else. Each is
**optional**: gate on `hostSupportsCap()`, never assume presence.

| Capability | Tier | Meaning |
| --- | --- | --- |
| `snapshot` | optional | Host can emit an authoritative full-screen frame on demand, instead of relying on byte replay to reconstruct a TUI. |
| `sbtext` | optional | Host can return scrollback as decoded text (selection/search) alongside the raw byte replay. |
| `input` | optional | Terminal-model input path — see the `input` row above; the terminal model reuses that literal rather than minting a second one. |

### Reserved — trust amendment

Signed-principal work. All **optional**; all additive on protocol `4`. Registering these does not
authorize anything — muxd remains the sole judge of whether a proof is valid.

| Capability | Tier | Meaning |
| --- | --- | --- |
| `principalAuthV1` | optional | Host verifies an authorized-principal proof carried on a request and rejects input that lacks one. |
| `principalAclV1` | optional | Host evaluates a per-principal access-control list before acting on a request. |
| `muxdSignedOutputV1` | optional | Host signs outbound output/session frames so a viewer can attribute them to this muxd. |
| `muxdWriteLeaseV1` | optional | Host issues and honours exclusive write leases so two principals cannot interleave input on one session. |
| `deferredInputV1` | optional | Host durably reserves an input intent and replays it once, so a retry cannot double-apply it. |

**No downgrade.** A peer that has advertised one of these capabilities on a link must not silently
drop it and continue serving the same request unsigned. Withdrawing a trust capability is a
reconnect (new `hello`, new `caps`), never a mid-link fallback — otherwise an attacker who can
strip a field also strips the proof requirement.

## Frame types

Every frame is a JSON object whose type lives in `t`. Unknown `t` values fall off the end of the
dispatch chain and are ignored.

### muxd → relay (`muxd/muxd.py:3733-3873`, consumed at `relay/server.js:1785-1909`)

| `t` | Purpose |
| --- | --- |
| `hello` | First frame on the link. Announces host label, protocol, caps, and the full session list. Must be accepted before any other frame; otherwise the relay closes with `1008`. |
| `sessions` | Full session-list refresh (heartbeat every 5s, and after any lifecycle change). |
| `o` | Terminal output bytes for one session, base64. |
| `sb` | Scrollback replay answering a relay `sb` request, correlated by `rid`. |
| `createResult` | Acknowledgement of a `create`, correlated by `rid`. |
| `tailr` | Answer to `tail`, correlated by `rid`. |
| `killed` | A session ended; relay drops it and tears down viewer state. |
| `resync` | This session's output backlog was dropped at a frame boundary (its per-session egress budget overflowed). Relay repaints that session's viewers with `CLEAR` + a scrollback replay; other sessions are untouched. |

### relay → muxd (`relay/server.js:255,313,674,705,720,2145,2200,2314`)

| `t` | Purpose |
| --- | --- |
| `create` | Create or adopt a session. Closed field set — see the registry rule. |
| `i` | Keystroke input bytes for a session, base64. |
| `resize` | Set remote PTY geometry. Sent only when the viewer's dimensions actually change. |
| `sb` | Request a scrollback replay, correlated by `rid`. |
| `tail` | Request the tail of a session as text, correlated by `rid`. |
| `rename` | Rename a session. |
| `heal` | Toggle a session's auto-resume policy. |
| `kill` | Stop a session. |

### muxd local control port (`LOCAL_PORT`, default 7699)

Loopback-only, used by `muxctl`. Not carried over the relay link, registered here so the `t`
literals stay globally unique: `info`, `ls`, `err`, `killed`, `owner-ok`, `bind-ok`, `created`,
`input-ok`, `i`, `kill`.

## Frame fields

| Frame | Field | Type | Notes |
| --- | --- | --- | --- |
| `hello` | `host` | string | Host label; the relay passes it through `opaqueIdentity()` and falls back to `pc`. |
| `hello` | `protocol` | int | Must equal `4` exactly. |
| `hello` | `caps` | string[] | See [Capabilities](#capabilities). |
| `hello` | `sessions` | object[] | Session payloads; a malformed entry closes the link. |
| `sessions` | `list` | object[] | Full replacement list — not a delta. |
| `sessions` | `notice` | string | Optional. Set when a durability step failed (heal/rename/stop not persisted). |
| `o` | `s` | string | Session name. |
| `o` | `d` | string | base64 output bytes. |
| `resync` | `s` | string | Session name. The only field; carries no payload — the replay is what restores the screen. |
| `sb` (both ways) | `s` | string | Session name. |
| `sb` (both ways) | `rid` | string | Correlation id; the relay drops a reply whose `rid` does not match the in-flight request. |
| `sb` → muxd | `max` | int | Byte cap on the replay; defaults to `SB_SEND`. |
| `sb` → relay | `d` | string | base64 replay bytes. |
| `create` | `t`,`s`,`rid` | string | Required. `rid` must match `[A-Za-z0-9._-]{1,128}`. |
| `create` | `cols`,`rows` | int | Optional. Range `20-500` / `8-200`; a bool or out-of-range value closes the link. |
| `create` | `relaunch`,`heal` | bool | Optional flags. |
| `createResult` | `rid`,`s` | string | `s` must match the requested name or the relay fails the create. |
| `createResult` | `ok`,`created`,`retryable` | bool | `retryable` maps to HTTP `503` (uncertain) vs `409` (refused). |
| `createResult` | `detail` | string | Human-readable failure reason; empty on success. |
| `createResult` | `session` | object | Session payload. Required when `ok` is true and must carry the matching `name`. |
| `i` | `s`,`d` | string | Session name; base64 input bytes. |
| `resize` | `s`,`cols`,`rows` | string,int,int | Clamped to `MAX_TERM_COLS` (1000) / `MAX_TERM_ROWS` (300). |
| `tail` | `s`,`rid`,`lines` | string,string,int | `lines` defaults to 40. |
| `tailr` | `rid`,`text` | string | Plain text, not base64. |
| `rename` | `s`,`to` | string | `to` must be a strict mux name and must not already exist. |
| `heal` | `s`,`on` | string,bool | |
| `kill` / `killed` | `s` | string | |

### Session payload fields

Produced by `session_payload()` (`muxd/muxd.py:2266`), consumed by `normalizeHostSession()`
(`relay/server.js:171`). Both lists must be updated together — the relay's is an allow-list.

`name`, `alive`, `created`, `lastOut`, `cols`, `rows`, `tail`, `heal`, `localViewers`,
`localFirst`, `owner`, `hasCommand`, `shellOnly`, `ready`, `kind`, `sessionId`, `aliases`,
`identityPending`, `agentState`, `agentLabel`, `agentDetail`, `agentConfidence`.

muxd additionally emits `lifecycle`, `childPid`, `needsAttention`, `lastOutAgeMs`, `agentTruth`,
and `agentStateSource`; the relay does not currently forward them. `kind` is one of `command` /
`shell` / `dormant`.

#### Process truth (`agentTruth` capability)

Additive on protocol 4 — a host without the `agentTruth` capability simply omits both fields, and a
consumer must treat their absence as `"heuristic"`.

| Field | Type | Notes |
| --- | --- | --- |
| `agentStateSource` | string | `"process"` when `agentTruth` is a fresh OS answer; `"heuristic"` when the probe is missing, stale, or failed. `agentState` is authoritative either way. |
| `agentTruth` | object | Always present when the capability is advertised; all-falsy (`AGENT_TRUTH_UNKNOWN`) when unknown. |
| `agentTruth.procAlive` | bool | The session's `childPid` process tree is really alive, fenced against pid reuse by the recorded creation time. |
| `agentTruth.cpuActiveRecent` | bool | Subtree kernel+user CPU rose since the previous probe. False on the first probe — absence of evidence, not evidence of idleness. |
| `agentTruth.exe` | string | Image name of the deepest non-ConPTY-host descendant — the process a human would call "the agent". `""` when unknown. |
| `agentTruth.checkedUtc` | string | ISO-8601 `Z` timestamp of the probe that produced this answer; `""` when never probed. |

Probes run on a dedicated bounded executor (`AGENT_TRUTH_MAX_INFLIGHT`), are cached for
`AGENT_TRUTH_TTL` (≥ 10 s), time out at `AGENT_TRUTH_PROBE_TIMEOUT` (≤ 3 s), and are never awaited
on the request path or under the state lock. A wedged probe therefore costs a session its
`"process"` source, never its responsiveness.

`name` is the exception to the allow-list shape: `normalizeHostSession()` validates it as a strict
mux name and rejects the whole session if it fails, but does **not** copy it into the returned
object — the caller keys the session map by it (`relay/server.js:207`). A new session field is not
like `name`; add it to the returned object literal.


### Manifest-only custody fields

Custody TTL fields (`lastAliveUtc`, `custodyExpiresUtc`) exist only in the durable session manifest.
They **must not** appear in `session_payload()` (the relay allow-list at `relay/server.js:171`)
because the relay has no need to see muxd-internal expiry timers and their presence would violate
the payload shape contract.
### Reserved — signed request/response/error shapes (trust amendment)

Optional envelope fields for the capabilities above. Absent unless the peer advertised the
matching capability. Presence of these fields is a *claim*, never a grant — the receiver validates
independently and rejects anything it cannot verify.

| Field | Carried on | Type | Notes |
| --- | --- | --- | --- |
| `principal` | any relay → muxd request | object | Principal claim: stable id plus the proof material muxd verifies. |
| `sig` | any signed frame | string | Detached signature over the canonical frame body. |
| `nonce` | any signed frame | string | Single-use; a replayed nonce is rejected, not re-executed. |
| `ts` | any signed frame | int | Millisecond timestamp for freshness windows. |
| `lease` | `i`, `resize` | string | Write-lease id from `muxdWriteLeaseV1`. Input outside a held lease is refused. |
| `intent` | `i` | string | Durable input-intent id from `deferredInputV1`; replaying the same id must apply the input at most once. |
| `authErr` | any muxd → relay reply | object | Structured refusal: `{ code, detail, retryable }`. Distinct from the human-readable `detail` string so the relay can branch on `code` without parsing prose. |

## Ownership

This file is owned by leaf `r.1.1`. Other campaign leaves — custody-janitor, agent-truth,
output-fairness, and the native terminal-model prong — **append** rows to the tables above. They do
not restructure the document, and they do not change the protocol number.

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
