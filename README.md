# MUX

The whole product in one repo: the MUX desktop app and its remote/multiplex capability.

- `app/`   — Windows desktop app (WinUI): chat archive, retrieval, session lifecycle, integrity/custody
- `relay/` — VPS web relay + browser terminal (multiplex): remote viewing/driving of PC sessions
- `muxd/`  — PC-side session host (Python/ConPTY): owns the real terminals, dials out to the relay

History: imported via git-subtree from the three original repos (each subdir carries its full history).
Runtime: muxd deploys to C:\Users\Ahmed\muxd-runtime (scheduled task MuxdSessionHost); relay deploys to
harmonizer@VPS:~/multiplex-app. This repo is the development source of truth; the runtime directory is
a deploy target and state root, never a checkout - edit `muxd/` here and run `scripts/deploy-muxd.ps1`.
