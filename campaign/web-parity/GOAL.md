# GOAL — Web parity + session discoverability (campaign: web-parity)

Owner: Ahmed. Written 2026-08-02 from the owner's words; change-controlled (owner approves edits).

## The grand goal

Make the mux web experience a first-class citizen of the archive:

1. **Web connection parity (the broken bridge).** The live VPS relay (`/opt/multiplex-app`,
   multiplex-app.service) is a diverged ~1477-line fork of canonical `relay/server.js` (~3418
   lines) with NO `/api/app-commands/lease` endpoint. The newly deployed desktop app polls the
   lease endpoint, so web→PC commands are dead until the relay is reconciled. Reconcile the fork
   onto canonical WITHOUT losing any live-only behavior, validate, deploy with
   MUX_DEPLOY_CONFIRM=1, and confirm end-to-end: web-queued command → held long-poll (waitMs) →
   PC executes → ack visible on the web.

2. **Full session discoverability on the web.** Today the web (`relay/public/projects.html`) is
   one large unpaginated projects list; only filed sessions are findable. The owner's real usage
   is the desktop app's discovery panel: filter by tags/phrases, sort, date, agent,
   your-message-count thresholds, hidden-chat toggle, project filter, plus search. Port that
   experience to the web: see chats, filter them, see phrases, search, paginate — and resume.
   The watcher/web surface must be LIGHTWEIGHT: able to see, parse, and resume sessions without
   hauling the whole archive into the browser.

## Fixed architectural points (owner doctrine, from prior sessions)

- The PC is the source of truth. The PC-side headless server (CodexArchiveRemote, port 8765)
  already serves the full archive and is published to the VPS at 127.0.0.1:8765 via the
  standing reverse tunnel. Discovery data on the web should be SERVED from the PC archive
  through that tunnel — not duplicated into a second index on the VPS.
- The terminal architecture stays: a real local terminal remoted (muxd ↔ relay WS). No
  transcript-replay imitation of live sessions.
- Nothing ships until its verifier runs green under the integrating mind's hands; deploy to the
  VPS only after reconciliation validation (the deploy script's refusal exists for a reason).

## Win condition

- A web-queued kill/open command round-trips against the deployed relay with the long-poll
  active (idle lease spawns ~1.5/min on the PC, delivery instant).
- On the web: a chats view backed by the PC archive with search, tag/phrase filter, agent
  filter, message-count filter, pagination, phrase visibility, and one-click resume — usable on
  both desktop and phone widths.
- No live-only relay behavior regressed (inventory proves coverage; muxd ↔ relay WS protocol
  unchanged or compatibly extended).
- All of it merged to master, deployed (VPS relay + anything PC-side), and demonstrated.
