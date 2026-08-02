## WS t==claude / t==codex
The stated route-surface fact does not match the fetched live source. `live-vps-server.js` contains no
`m.t === 'claude'`, `m.t === 'codex'`, `t === 'claude'`, or `t === 'codex'` WebSocket dispatch branch.
There is therefore no full `claude`/`codex` WS handler to quote, no live sender for those message types,
and no state mutation attributable to such a handler.

The closest matching source is command parsing, not WebSocket dispatch:

```js
function parseResumeSessionId(command) {
  const tokens = commandTokens(command);
  for (let i = 0; i < tokens.length; i++) {
    const t = path.basename(tokens[i]).toLowerCase();
    if (t === 'codex' || t === 'codex.exe') {
      const resumeAt = tokens.findIndex((x, j) => j > i && String(x).toLowerCase() === 'resume');
      if (resumeAt >= 0) {
        for (let j = resumeAt + 1; j < tokens.length; j++) {
          const v = String(tokens[j] || '');
          if (!v || v.startsWith('-')) continue;
          return v;
        }
      }
    }
    if (t === 'claude' || t === 'claude.exe') {
      for (let j = i + 1; j < tokens.length - 1; j++)
        if (String(tokens[j]).toLowerCase() === '--resume') return String(tokens[j + 1] || '');
    }
  }
  const codex = String(command || '').match(/\bcodex(?:\.exe)?\b[\s\S]*?\bresume\b(?:\s+--[^\s]+)*\s+["']?([^"'\s;]+)/i);
  if (codex) return codex[1];
  const claude = String(command || '').match(/\bclaude(?:\.exe)?\b[\s\S]*?\b--resume\s+["']?([^"'\s;]+)/i);
  return claude ? claude[1] : '';
}
```

The live host-link switch at lines 1210-1290 handles `hello`, `sessions`, `o`, `sb`, `tailr`, and
`killed`; the viewer-side parser at lines 1382-1388 handles `v`, `r`, `h`, `P`, and `s`. The only
`claude`/`codex` references elsewhere are command parsing, comments, and HTTP tool validation. No
`sendHost({ t: ... })` call uses either value.

PORT SPEC: none can be written from this source. If the sealed fact came from a different live revision,
re-fetch that revision and insert its verified handler into canonical's host `ws.on('message')` switch
after validated `hello`/`sessions` processing and before the ordinary output/control branches at
canonical lines 2742-2889. On the evidence available here, verdict DROP: adding a dead handler would
invent behavior and there is no sender to preserve.

## PUBLIC_RETURN
The live file has one usage, at line 114:

```js
const prefix = req.headers['x-forwarded-prefix'] || process.env.PUBLIC_RETURN || '/multiplex';
if ((req.headers.accept || '').includes('text/html')) return res.redirect(HL_LOGIN + '?next=' + encodeURIComponent(prefix));
```

This runs in the global owner gate when there is no session cookie and the request accepts HTML. It
builds the `next` value from the forwarded mount prefix, then `PUBLIC_RETURN`, then `/multiplex`, and
redirects to the hl-auth login URL. The live login base is configured at line 33:

```js
const HL_LOGIN = (process.env.HLAUTH_PUBLIC_BASE || 'https://harmonizerlabs.cc') + '/auth/login';
```

Canonical already has the same behavior at lines 202-203:

```js
const prefix = req.headers['x-forwarded-prefix'] || process.env.PUBLIC_RETURN || '/multiplex';
if ((req.headers.accept || '').includes('text/html')) return res.redirect(HL_LOGIN + '?next=' + encodeURIComponent(prefix));
```

Canonical also has the same `HLAUTH_PUBLIC_BASE` login-base construction at line 69. Therefore
`HLAUTH_PUBLIC_BASE`/the public login path handling already covers the live behavior.

PORT SPEC: none. Verdict DROP as a port action: retain `PUBLIC_RETURN` in deployment configuration if
the reverse-proxy mount needs it, but do not add code to canonical.

## __test endpoints
The complete live-only block is:

```js
if (TEST_MODE) {
  app.post('/__test/autoheal', (req, res) => {
    _healOn = new Set(Array.isArray(req.body && req.body.names) ? req.body.names.map(SAFE).filter(Boolean) : []);
    res.json({ ok: true, names: [..._healOn] });
  });
  app.post('/__test/autoheal-tick', (req, res) => { autoHealTick(); res.json({ ok: true }); });
  app.post('/__test/boot-recreate', (req, res) => { bootRecreate(); res.json({ ok: true }); });
}
```

`POST /__test/autoheal` replaces the in-memory armed-session set `_healOn`; it does not call
`saveHealOn()`. `POST /__test/autoheal-tick` runs the relay-owned `autoHealTick()` loop, which reads
`hostSessions`, `_sessState`, `_healOn`, and projected `muxCommand` values, then may send an `i` frame
back to muxd and mutate `_heal`. `POST /__test/boot-recreate` runs `bootRecreate()`, which reads
`_healOn`, legacy tmux names, and projected resume commands, then may send a command-bearing `create`
frame and insert an optimistic entry into `hostSessions`.

All three routes are syntactically guarded by `TEST_MODE`, but they sit behind the live global auth
middleware. Canonical has zero `__test` routes and no `autoHealTick` or `bootRecreate`; its explicit
design statement at lines 1772-1773 is:

```js
// muxd is the sole owner of persisted launch commands, boot recovery, and self-heal.
// The relay only toggles muxd policy and forwards explicit, opaque control intent.
```

Verdict: DROP all three routes. Porting them would require restoring the live relay-owned healing
engine and command-bearing recreate path, which canonical intentionally removed. Tests should drive
canonical's host protocol and `/api/sessions/:name/autoheal` contract instead.

## Behavioral drift beyond the known list
- **Persistence and state loading, live lines 11-19, 575-577, 849-853, 1044-1048, 1302-1307.** The live `atomicWrite()` and raw `JSON.parse()` loads swallow all errors; canonical uses durable state writers/loaders and exposes persistence failure. Risk: a failed write can silently leave memory and disk divergent, including armed-session and command state.
- **Auth-cache policy, live lines 34-54 versus canonical lines 70-97.** Live fixes the cache at 5,000 entries and 60 seconds, without expiry sweeping or configurable TTL; canonical uses bounded configurable values and refreshes recency. Risk: live retains stale entries longer and permits a larger memory footprint under cookie churn.
- **Auth middleware shape, live lines 108-119 and 1391-1393 versus canonical lines 151-207 and 3243-3247.** Live lacks canonical's scoped transcript-bridge exemption and `testModeLocalTrust()` path, and its WS attach always requires the owner oracle. Risk: copying the live gate would break canonical bridge/test contracts or accidentally broaden loopback trust.
- **Dispatch-key handling, live lines 79-97 versus canonical lines 122-140.** The narrow `DISPATCH_KEY` and allowlisted `DISPATCH_SESSIONS` check is materially shape-matched; there is no live-only dispatch behavior to port. Risk: none beyond accidentally replacing canonical's equivalent with an older auth gate.
- **Name and terminal-size validation, live lines 131, 405-569, and 1395-1400 versus canonical lines 219-227 and 257-264.** Live sanitizes names with `SAFE()` and accepts unbounded numeric dimensions; canonical rejects changed names and clamps dimensions. Risk: malformed names can alias valid sessions, and oversized dimensions can create resource and layout failures.
- **Host protocol acceptance, live lines 151-194 and 1212-1243 versus canonical lines 265-355 and 2733-2790.** Live requires protocol 2, accepts any protocol at or above that value, installs a new socket before validating its hello, accepts `sessions` without a validated hello, and stores raw session fields; canonical requires exact protocol 4, validates identities/capabilities, and rejects forbidden remote keys. Risk: an incompatible or malformed host can replace valid state or smuggle command/path-shaped data into relay state.
- **Command-bearing remote resume flow, live lines 159-169, 401-429, 439-479, 620-673, and 953-978.** Live derives `cmdSig`, parses Claude/Codex resume commands, and sends `cmd`/`ids` in create/recreate frames; canonical accepts opaque tool/session identity and explicitly keeps executable commands in muxd/bridge state. Risk: porting this would reintroduce command transport, RCE exposure, and double-writer ambiguity.
- **Host session identity fields, live lines 140, 353-382, 422-423, and 969-971 versus canonical lines 273-307 and 781-822.** Live relies on name plus `cmdSig` and lacks canonical's validated `sessionId`, `aliases`, `identityPending`, and process-truth fields. Risk: a renamed or reused tab can link to the wrong projected chat, and status decisions lose canonical identity/process provenance.
- **Relay-owned autoheal and timing, live lines 841-990 versus canonical lines 1204-1224 and 1772-1773.** Live persists `_healOn`, scans shell prompts, uses a 12,000 ms interval, a 9,000 ms settle gate, a five-action/10-minute cap, six-second/90-second post-resume windows, and a 25,000 ms boot delay; canonical delegates self-heal and boot recovery to muxd. Risk: two independent healers can resume one transcript twice, and the timing constants can fight muxd's policy.
- **Legacy tmux probing, live lines 222-228 and 1022-1025 versus canonical lines 456-490 and 1808-1832.** Live runs synchronous `tmux list-sessions` on every caller; canonical adds a TTL snapshot and probe counter. Risk: every polling tab can block the relay event loop and produce terminal stutter.
- **Projection trust, live lines 572-839 versus canonical lines 1250-1415 and 1585-1617.** Live persists incoming projects, chats, commands, and paths with no schema, forbidden-key, or identity validation; canonical normalizes/rejects those fields and invalidates liveness after restart. Risk: executable/local-path data and malformed identities can cross the bridge and poison ownership checks.
- **App-command queue semantics, live lines 1041-1116 versus canonical lines 1851-2269.** Live exposes unleased pending commands and accepts any loopback ack for a matching id; canonical uses intent fingerprints, leases, lease tokens, immutable terminal outcomes, and replay policy. Risk: retries or stale bridge workers can execute or acknowledge a command more than once.
- **Upload storage semantics, live lines 1118-1179 versus canonical lines 2466-2701.** Live accepts loosely sanitized metadata, stores `pcPath`, and silently ignores write/delete failures; canonical validates ids/names, reconciles storage, and commits metadata durably. Risk: metadata/file drift, unsafe names, and unrecoverable partial deletes can survive a restart.
- **Host tail/replay and backpressure, live lines 148, 211-220, 1181-1293, 1311-1360, and 1431-1467 versus canonical lines 245-256, 438-454, 2709-2905, and 3032-3136.** Live uses a short clear sequence, direct `ws.send()`, per-client buffering, no shared scrollback request id, no high-water eviction, and `killed` only deletes `hostSessions`; canonical resets terminal modes, correlates scrollback, bounds viewer buffers, and closes/cleans viewers and lease state. Risk: stale alt-screen state, black or fragmented reconnects, memory growth behind slow viewers, and stale clients after session death.
- **Pin/rename crash semantics, live lines 505-519 and 1302-1307 versus canonical lines 2940-3019.** Live migrates pin state only after muxd confirms rename and has no durable rename intent; canonical records and reconciles the intent across restart. Risk: a relay crash during rename can strand or misapply a device pin.
- **Unknown-session WS attach, live lines 1410-1430 versus canonical lines 3264-3276.** Live creates an empty host session for any unknown web tab; canonical requires an explicitly created known session and closes the attach otherwise. Risk: a typo, stale tab, or race can create an unintended PC session and a second writer.

## Port checklist
1. DROP - `t==claude`/`t==codex` WS behavior: no handler or sender exists in the fetched live file; target canonical host switch at lines 2742-2889 only after a verified source revision.
2. DROP - `PUBLIC_RETURN`: target canonical auth middleware at lines 201-203 already contains the live behavior.
3. DROP - `POST /__test/autoheal`: target no canonical route; use canonical host/API test setup.
4. DROP - `POST /__test/autoheal-tick`: target no canonical `autoHealTick`; exercise muxd-owned behavior through the host protocol.
5. DROP - `POST /__test/boot-recreate`: target no canonical `bootRecreate`; exercise explicit session creation/relaunch instead.
6. DROP - Relay `_healOn`, `AUTOHEAL_FILE`, `autoHealTick()`, and `bootRecreate()`: target canonical `/api/sessions/:name/autoheal` at lines 1204-1224 and the muxd-ownership boundary at lines 1772-1773.
7. DROP - Live auth-cache constants and whole auth-gate replacement: target canonical `isOwner()` and middleware at lines 70-207, including transcript/test-mode exemptions.
8. DROP - `SAFE()`-only route identity and unbounded viewer dimensions: target canonical `strictMuxName()`, `opaqueIdentity()`, and `clampTermDimension()` at lines 219-264.
9. DROP - Protocol-2/raw host-link acceptance and optimistic pre-hello replacement: target canonical `normalizeHostSession()`, `announcedHostProtocol()`, and validated `/host` handler at lines 273-355 and 2733-2790.
10. DROP - `cmdSig`, `parseResumeSessionId()`, `projectedChatCandidates(name, command)`, and command-bearing create/recreate frames: target canonical opaque `/api/sessions` and relaunch flows at lines 932-1025.
11. DROP - Live `hostSessions` field shape: target canonical `normalizeHostSession()` and `listSessions()` at lines 273-307 and 781-822.
12. DROP - Live synchronous legacy tmux probes: target canonical TTL-cached `legacyTmuxNames()` at lines 477-490.
13. DROP - Live projection acceptance and raw persistence: target canonical projection validation/normalization at lines 1250-1415 and 1585-1617.
14. DROP - Live unleased app-command queue and unconstrained ack: target canonical lease/intent queue at lines 1851-2269.
15. DROP - Live upload metadata and cleanup path: target canonical validated upload store at lines 2466-2701.
16. DROP - Live clear/replay/direct-send/session-cleanup behavior: target canonical `CLEAR_SCREEN`, tail correlation, `sendViewer()`, scrollback state, and killed-session cleanup at lines 245-256, 438-454, 2817-2905, and 3032-3136.
17. DROP - Live pin migration without rename intents: target canonical rename-intent functions at lines 2940-3019.
18. DROP - Live auto-create-on-WS-attach: target canonical known-session guard at lines 3264-3276.

## Ambiguities
- The sealed brief says the live fork handles `t==='claude'` and `t==='codex'`, but an exhaustive literal scan of the provided 1,477-line file found no such handler, sender, or dispatch branch. This inventory follows the file contents and records the mismatch rather than inventing code.
- The sealed brief calls `PUBLIC_RETURN` live-only, but canonical line 202 uses it identically. It is treated as already ported and requires no code action.
- The test-route verdict assumes canonical's muxd-owned autoheal/relaunch contract is the intended replacement. Preserving a legacy test API would require reintroducing the live relay-owned healing engine, which conflicts with canonical lines 1772-1773.
- "Behavioral drift" is interpreted as material runtime, security, persistence, or state-shape behavior present in the live fork and absent or deliberately replaced in canonical; canonical-only features with no live predecessor are not listed.
