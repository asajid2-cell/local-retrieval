# Reliability Plan

1. Build trusted failing verifiers for duplicate create, relaunch overlap, protocol compatibility,
   durable persistence, queue delivery, canonical launch identity, and orphan cleanup.
2. Consolidate muxd local and relay creation through one serialized launch coordinator.
3. Share the existing session claim-file protocol with muxd and persist canonical aliases.
4. Require confirmed process-tree exit before replacement or kill acknowledgement.
5. Replace command-bearing relay flows with opaque `sessionId`, `tool`, and `muxName` intents.
6. Resolve every intent in the local archive and construct executable arguments only on the PC.
7. Make app, relay, muxd, queue, and event writes durable and read-back verified.
8. Add process containment for app-server children and preserve live-owner detection after crashes.
9. Run the unified acceptance runner, independent review, deploy all components, and smoke production.
