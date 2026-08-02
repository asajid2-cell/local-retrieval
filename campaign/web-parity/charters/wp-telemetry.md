# Charter — branch mind `wp-telemetry` (make the app explain itself)

Owner: "add robust logging, not just branch failures but across the app, in depth telemetry."

The concrete failure this exists to prevent: branching broke repeatedly for weeks and left NO
evidence. Resumes write to the session-event ledger; branches, checkpoints, syncs and launches do
not. An hour of hypotheses was beaten by a screenshot of the error text — that is a telemetry bug,
not a debugging-skill bug.

Repo: Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval (master). Persistent tandem peer; fan
out sealed junior lanes; re-run every verifier yourself.

## What exists (build on it, do not invent a parallel system)

- `Core/Remote/SessionEventLedger.cs` — the durable, per-session event ledger with a sidecar index,
  secret redaction (`SecretAssignment`, `CommonSecret`, `WindowsUserPath`), and best-effort append.
  This is the spine. `MainPage.Sessions.cs` already records `resume.refused.*` / `resume.started.*`
  / `resume.failed.*` — that IS the pattern to spread.
- `Native/Diag.cs` — a flat text tracer to `%TEMP%\clr-startup.log`. Unstructured, unrotated, and
  it grew to ~8 MB. It is fine for startup tracing and wrong as the app's telemetry.

## Required outcomes

1. **Every user-visible operation that can fail records a structured event**: branch, checkpoint,
   spawn-from-checkpoint, sync/merge, collection filing, rename, kill, start-chat, remote command
   execution, launch claims. Each carries: what was attempted, the outcome, the REAL error text
   (the thing that was missing — `'0x00' is an invalid start of a value` should have been in the
   ledger), and enough identity to correlate (session id, intent id, tool).
2. **A failure the user sees must be findable afterwards without a screenshot.** That is the
   acceptance bar. Demonstrate it: force a branch failure, then show the event that explains it.
3. **Redaction is not optional.** Everything goes through the ledger's existing redaction; add
   tests proving a path/secret in an exception message does not land in the ledger. (A live key was
   burned once by logging an exception message verbatim — see the house rule.)
4. **Bounded**: rotation/retention for `Diag` and the ledger. An 8 MB unrotated log is data loss by
   another name — the interesting line ages out of reach.
5. **Surface it**: the app already has a Custody/Integrity panel and a session-events view
   (`MainPage.SessionEvents.cs`). A user hitting a failure should be able to see WHY in the app.

## Non-goals / boundaries
No new external dependency, no telemetry leaving the machine, no sampling framework. This is local,
structured, redacted, bounded event logging.

Write-scope: `Core/Remote/SessionEventLedger.cs`, `Native/Diag.cs`,
`Native/MainPage.SessionEvents.cs`, and the RECORDING call sites you add in
`Core/Services/ArchiveService.Branch.cs`, `Native/MainPage.Branch.cs`, `Native/MainPage.Sessions.cs`,
`Native/MainPage.StartChat.cs`, plus tests. Do NOT touch `ArchiveService.Search.cs`,
`Core/Remote/DiscoveryApi.cs`, `Core/Agents/**`, `muxd/**`, or `relay/**` — other lanes own those.

Verification: `dotnet test` green, plus a demonstrated force-a-failure-then-find-the-event trace
pasted into your fold. Fold: `campaign/web-parity/folds/wp-telemetry.md`.
