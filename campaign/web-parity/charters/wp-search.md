# Charter — branch mind `wp-search` (make deep search actually search everything)

Owner: fix deep search, and test the fix after.

## The problem, stated honestly

"Deep search" today is not deep. Content is only indexed for the newest slice of the corpus — the
disk scan caps at roughly the newest 2,000/3,000 sessions (`Core/Services/ArchiveService.cs` around
lines 851 and 936, and `MaxIndexedFiles = 4000` near line 25). The owner has ~7,300 chats and 3,199
codex rollouts, some transcripts hundreds of MB (a 497 MB one is real). So a phrase that exists in
an older chat simply cannot be found, and the UI does not say so — it reports "no results", which
is a lie the user then acts on.

The web made this worse-shaped: `Core/Remote/DiscoveryApi.cs` serves metadata search only, and the
apex deliberately refused to promise transcript search on the web until this is real.

Repo: Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval (master). Persistent tandem peer; fan
out sealed junior lanes; re-run every verifier yourself.

## What to build

1. **Decide the mechanism and justify it in your fold.** A durable content index (SQLite FTS is the
   house-adjacent pattern — the `nsa` tool uses FTS over this same corpus) versus streaming grep
   with a bounded worker pool. Consider: 40+ GB of transcripts, files that are appended to while
   live, incremental update on sync, and a cold-start cost the user will actually tolerate. State
   the trade-off you chose and the numbers you measured — not a guess.
2. **Correctness over the WHOLE corpus, not the newest slice.** If a hard cap must remain for a
   specific reason, the UI must SAY the result set is partial and why. Silent truncation is the
   defect being fixed; do not reproduce it in a new place.
3. **Incremental**: a sync must not re-read 40 GB. Reuse the existing per-file stamp cache idea
   (`Store.FileStamps`) so only changed files are re-indexed.
4. **Then expose it on the web** through `DiscoveryApi` so the phone gets the same search the
   desktop has — that was explicitly deferred pending this work.

## Verification (binding — measure, don't assert)

- A phrase that exists ONLY in an old, previously-unindexed chat is found. Prove it with a real
  local chat, naming the file and the phrase.
- Report real numbers: cold index build time, incremental sync time, index size on disk, and query
  latency at the 7k-chat scale. "It works" is not a result.
- `dotnet test` green, plus tests for incremental re-index and for the partial-results signalling.

Write-scope: `Core/Services/ArchiveService.Search.cs`, the scan/index paths in
`Core/Services/ArchiveService.cs`, `Core/Remote/DiscoveryApi.cs`, the search UI in
`Native/MainPage.xaml.cs`/`MainPage.Tags.cs` as needed, `relay/public/chats.*`, and tests. Do NOT
touch `Core/Remote/SessionEventLedger.cs`, `Native/Diag.cs`, `Core/Agents/**`, `muxd/**` — other
lanes own those. Fold: `campaign/web-parity/folds/wp-search.md`. Pull the apex on the mechanism
decision BEFORE building it.
