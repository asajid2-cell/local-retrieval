# VPS security apparatus — audit + integration decisions (apex, 2026-08-02)

Audited live, no secret VALUES recorded here (key NAMES only; the real values stay on the VPS).

## What the hardening actually looks like now

- **Edge auth_request gate pattern.** `/etc/nginx/sites-enabled/harmonizer` gates each app at
  the EDGE: `location = /_gate_<app> { internal; proxy_pass http://127.0.0.1:4200/internal/gate?page=<app>;
  proxy_pass_request_body off; proxy_set_header Content-Length ""; proxy_set_header X-Internal-Key <secret>;
  proxy_set_header Cookie $http_cookie; }` plus `error_page 401 = @login_<app>` / `@pass_<app>` /
  `@noaccess_<app>` redirect targets. Live pages include archive, convert, cameraroom, veripact,
  language, cloud-squeeze, led, cosign, cloud-link, neondrift(2), topology, whoami.
- **hl-auth** runs on `127.0.0.1:4200`; `/internal/gate?page=<page>` answers 401 without a valid
  cookie. VERIFIED: `page=remote` is a live gate identity (401 uncookied, same as `page=archive`)
  — so an edge gate for /remote needs no new hl-auth registration.
- **Relay service**: `multiplex-app.service`, `ExecStart=/usr/bin/node /opt/multiplex-app/server.js`,
  `EnvironmentFile=/etc/multiplex-app.env`, `MUX_STATE_DIR=/var/lib/multiplex`, user svc-multiplex.
  Env keys present: PORT, HLAUTH_BASE, HL_INTERNAL_KEY, HLAUTH_COOKIE, HLAUTH_PUBLIC_BASE,
  PUBLIC_RETURN, MUX_HOST_TOKEN, MUX_DISPATCH_KEY, MUX_DISPATCH_SESSIONS.
- **Web mount**: `/multiplex/` → `127.0.0.1:7682` with `X-Forwarded-Prefix /multiplex` and WS
  upgrade headers. (7682 = the relay's public listener.)
- **/remote/ is deliberately 404'd**, with the reason in a comment: ":8765 not running;
  neutralized so it can never proxy to a port that later gets reused by another service."
  That reason is now STALE — the PC reverse tunnel is live: from the VPS,
  `curl 127.0.0.1:8765/healthz` → 200 `{"ok":true,"service":"codex-local-retrieval"}`,
  and `ss -ltn` shows 127.0.0.1:8765 bound (loopback only, correct).

## Conflicts with the campaign plan, and the rulings

1. **`Server/deploy/nginx-remote.conf` in the repo is PRE-HARDENING and must not be pasted as-is.**
   It says "no auth_request needed here — the server gates itself". That was true before the edge
   gate existed; it is now inconsistent with every other app on this vhost. RULING: /remote/ gets
   the SAME edge gate as its neighbours (`auth_request /_gate_remote` + `error_page 401 =
   @login_remote`), keeping the server's own CLR_REMOTE_HLAUTH self-gate as defence in depth.
   Two independent layers, matching house style; neither alone is load-bearing.
2. **The repo snippet has NO WebSocket upgrade headers.** The PC server serves live agent
   sessions over `/api/agent` (WS). Through a proxy without `Upgrade`/`Connection` headers that
   silently fails. RULING: copy the `/multiplex/` location's WS + long-timeout treatment.
3. **X-Forwarded-Prefix.** `/multiplex/` sets it and the relay honours it (PUBLIC_RETURN
   fallback chain). The PC server's hl-auth middleware builds its login redirect from
   CLR_REMOTE_PUBLIC_PATH (`/remote`), so the prefix header is not required for correctness —
   but set it anyway for parity/diagnostics.
4. **Do NOT widen the tunnel bind.** 8765 must stay loopback-only on the VPS; the PC side keeps
   `CLR_REMOTE_BIND=0.0.0.0` for LAN, which is a separate trust domain the owner already accepts.
5. **Relay deploy vs env.** Canonical reads extra keys not in /etc/multiplex-app.env
   (MUX_BRIDGE_TOKEN, MUX_COMMAND_LEASE_MS, MUX_AUTH_CACHE_*, alert/notify keys...). All have
   safe defaults EXCEPT the bridge token: canonical's `POST /api/archive-index` REQUIRES a
   bearer. Since v3 decision 1 REMOVES the archive-index push entirely, no new secret needs to
   be provisioned — the endpoint simply goes unused. If a future feature needs it, add
   MUX_BRIDGE_TOKEN to /etc/multiplex-app.env and to the PC push, never a hardcoded default.

## Deploy-time actions (apex-run, at Wave I)
1. Add `_gate_remote` + `@login_remote` + the real `/remote/` proxy block (WS headers, gate,
   prefix) replacing the two `return 404;` lines. Model on the `archive` gate + `/multiplex/`
   proxy verbatim, reusing the SAME X-Internal-Key value already in the file.
2. `sudo nginx -t && sudo systemctl reload nginx`; verify: uncookied `/remote/` → 302 login,
   cookied → the app, and a WS upgrade on `/remote/api/agent` succeeds.
3. Relay deploy (Wave R dossier) is independent of the above and can land first.
