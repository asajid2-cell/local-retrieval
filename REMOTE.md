# Remote access — reach your archive from anywhere

`CodexLocalRetrieval.Server` is a small headless web server that exposes your archive and the
co-pilot over HTTP, so you can browse chats, read rollouts, talk to the co-pilot, pin chats, and
resume a chat in a terminal from your phone or any browser.

It reuses the same engine as the desktop app (`CodexLocalRetrieval.Core`). You run it on the machine
that has your chats and the `codex`/`claude` CLIs; you reach it from anywhere by reverse-proxying it
through your own server. **Your chats and your model key never leave that machine** — the VPS only
forwards traffic.

## Security model (read this)

- **Bearer token required.** Set `CLR_REMOTE_TOKEN` to a long random secret (≥24 chars). Every
  `/api/*` route requires `Authorization: Bearer <token>`. The server refuses to start without it.
- **Binds localhost only** by default (`127.0.0.1`). Put TLS + the public hostname on your VPS
  (nginx), never expose the raw port to the internet.
- **The co-pilot redacts secrets** in anything sent to the model (DeepSeek). Direct reads are *not*
  redacted by default (it's your own authenticated session to your own data); set
  `CLR_REMOTE_REDACT_READS=1` to redact rollout/message reads too on untrusted networks.
- **State changes are explicit taps** (pin, resume) — there is no remote co-pilot that mutates on its
  own. Resume only launches a terminal on the host when `CLR_REMOTE_ALLOW_LAUNCH=1`; otherwise it
  returns the command for you to run. The trusted-exe check still applies.

## Configuration (environment variables)

| Var | Default | Meaning |
|---|---|---|
| `CLR_REMOTE_TOKEN` | — (required) | shared bearer secret, ≥24 chars |
| `DEEPSEEK_API_KEY` | — | model key for the co-pilot (omit to disable chat) |
| `CLR_REMOTE_PORT` | `8765` | listen port |
| `CLR_REMOTE_BIND` | `127.0.0.1` | listen address (keep localhost; the VPS is the edge) |
| `CLR_REMOTE_SYNC` | on | set `0` to skip the disk re-index on startup |
| `CLR_REMOTE_STORE` | — | point at a specific `app-store.json` (e.g. a synced copy) |
| `CLR_REMOTE_REDACT_READS` | off | `1` to redact secrets in direct rollout/message reads |
| `CLR_REMOTE_ALLOW_LAUNCH` | off | `1` to let "Resume" actually launch a terminal on the host |

## Run it locally

```powershell
$env:CLR_REMOTE_TOKEN = "<a long random secret>"
$env:DEEPSEEK_API_KEY = "<your key>"     # optional, enables the co-pilot
dotnet run --project native/CodexLocalRetrieval.Server -c Release
# -> http://127.0.0.1:8765  (open it, paste the token)
```

Or publish a self-contained build to run as a service:

```powershell
dotnet publish native/CodexLocalRetrieval.Server -c Release -o publish/remote
# publish/remote/CodexLocalRetrieval.Server.exe  (wwwroot is copied alongside it)
```

## Reach it from anywhere (VPS reverse proxy + tunnel)

The server listens on localhost on your machine. Forward your machine's port to your VPS and let
nginx publish it under a path with TLS.

**1. Reverse SSH tunnel** (from the Windows box; keep it up with autossh / a scheduled task):

```bash
# forwards VPS:127.0.0.1:8765  ->  this machine's 127.0.0.1:8765
ssh -N -R 127.0.0.1:8765:127.0.0.1:8765 youruser@your-vps
```

**2. nginx location** on the VPS (inside your existing TLS server block):

```nginx
location /archive/ {
    proxy_pass http://127.0.0.1:8765/;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 300s;   # co-pilot turns can take a little while
}
```

Now `https://your-domain/archive/` is your archive — paste the token once and it's stored in that
browser. (Alternatives to the SSH tunnel: Cloudflare Tunnel or Tailscale Funnel both work the same
way — they just publish your localhost:8765 under a hostname.)

> Tip: keep the token in a password manager. To revoke remote access, change `CLR_REMOTE_TOKEN` and
> restart — every stored browser session is invalidated.

## API (for scripts / your own clients)

All require `Authorization: Bearer <token>` except `/healthz`.

- `GET /healthz` · `GET /api/stats`
- `GET /api/chats?q=&limit=` — search/list
- `GET /api/chats/{id}` — summary + paged messages (`?page=&pageSize=`)
- `GET /api/chats/{id}/events?limit=` — raw rollout events
- `POST /api/copilot` — `{ "message": "...", "history": [{role,content}] }` (read-only tool loop)
- `POST /api/chats/{id}/resume` — `{ "launch": true }` (send the next task)
- `POST /api/chats/{id}/favorite` — `{ "favorite": true }`
