# Fold - wp-search

Status: mechanism gate awaiting apex decision. No production files changed.

## Recommendation

Build a disposable SQLite FTS5 index beside `app-store.json`. Use contentless-delete FTS rows
over fixed-size transcript chunks, plus ordinary tables that map each row to session id, source
path, byte offset, and length. Track each file's stamp, indexed length, and a tail hash.

- Unchanged stamp: no transcript read.
- Append-only growth: replace the previous final chunk and add new chunks.
- Shrink, same-size mutation, tail-hash mismatch, parser/index version change, or session identity
  change: delete and rebuild only that file.
- Search reads the source chunk with `FileShare.ReadWrite | FileShare.Delete` to construct a
  snippet; transcript text is not duplicated in SQLite.
- Keep unbounded `rg`/streaming grep as the correctness oracle and repair fallback, not the normal
  query path.

This mechanism is necessary because the corpus contains multi-gigabyte files, so one FTS row per
file is not viable, while a stream scan pays the full corpus I/O cost on every query. The installed
`Microsoft.Data.Sqlite` runtime reports SQLite 3.49.1 and successfully created, queried, and deleted
from an FTS5 table using `content=''` and `contentless_delete=1`.

The existing `MaxIndexedFiles` cap must be removed. It is applied before Claude journals and
subagents are excluded, so valid old chats can fall outside the newest 4,000 candidates even when
the final valid-chat count is lower. The existing 2,000/3,000 search caps and 128 MiB head/tail
sampling must also stop being the content-search path.

## Measured Corpus

Measured on Sunday, August 2, 2026:

- Persisted archive: 7,369 sessions, 9,231 file stamps, 49,273,595-byte `app-store.json`.
- Physical roots: 9,251 JSONL files, 42.084 GB including Claude journals/subagents.
- Eligible chat transcripts after excluding `journal.jsonl` and `subagents`: 6,947 files,
  37.368 GiB.
- Codex eligible: 3,215 files, 32.458 GiB.
- Claude eligible: 3,732 files, 4.910 GiB.
- Largest Codex rollout: 4,949.9 MiB.
- Largest Claude transcript: 497.1 MiB.
- Free space before building an index: 65,124,556,800 bytes on C:.

An unbounded fixed-string `rg` over both roots, excluding Claude journals/subagents, took
76,112.5 ms for `effective software engineer` and returned 3,233 files. This common phrase is a
deliberately harsh baseline, but it proves a per-query whole-corpus stream scan is not interactive
for desktop or phone use.

## Old-Chat Correctness Oracle

The oldest live Codex rollout is:

`C:\Users\Ahmed\.codex\sessions\2026\05\14\rollout-2026-05-14T06-05-50-019e2660-f982-7e42-82de-f60b602afec7.jsonl`

It was last written `2026-05-14T13:41:55.6783110Z`, is 2,216,973 bytes, and line 1 contains the
exact phrase `effective software engineer`. I independently re-ran:

```powershell
rg -n -F -i -m 1 -o -- "effective software engineer" <old-file>
```

Result: `1:effective software engineer`.

The implementation acceptance run must find this source through the archive API/index, then prove
the same query through the web discovery surface. A rarer old-file-only phrase should also be
selected before the final benchmark so the result set is unambiguous.

## API And UI Contract

- Preserve exact `[phrase]` codename behavior; bracket queries remain metadata-only.
- For plain `q`, content search produces the ordered candidate sessions first. Existing tag,
  project, agent, date, hidden, and user-message predicates apply without reapplying the old
  capped-text query.
- Pagination occurs after all predicates. `sort=recent` preserves content relevance; explicit
  created sorts retain deterministic id tie-breaking.
- Chats and facets must use the same candidate set and expose the same additive coverage object:
  `complete`, `partial`, or `unavailable`, with indexed and eligible chat counts plus detail.
- The web must render partial/unavailable coverage before any "No matching chats" message.
- Discovery rows remain metadata-only: do not expose source paths, snippets, or launch commands.

## Required Tests And Measurements

Automated:

- Whole-corpus enumeration has no silent cap and excludes sidechains before counting coverage.
- Initial index build finds content outside the former newest slice and inside a middle chunk.
- No-change sync reads no transcript bodies.
- Append sync replaces the final chunk and indexes only the appended region.
- Shrink/mutation rebuilds only the changed file.
- Partial/incomplete index state is returned by Discovery API and rendered honestly by the web.
- Content hits compose correctly with every metadata filter, facets, stable pagination, and exact
  bracket-phrase behavior.

Binding live measurements:

- Fresh index build wall time and throughput.
- No-change incremental sync time.
- One real live-file append incremental time.
- SQLite database plus WAL/SHM size after checkpoint.
- Process-cold query latency and warm p50/p95/p99 for rare, common, absent, and old-file queries.
- FTS source-path results compared with an unbounded `rg` oracle.

## Verification Re-run

- Temporary SQLite runtime probe: passed. FTS5 contentless-delete create/query/delete worked on
  SQLite 3.49.1.
- `node --test relay/tests/discovery-page.test.js`: passed 4/4.
- Discovery/deep/incremental `.NET` filter: attempted from the shared tree, but compilation is
  currently blocked by concurrent out-of-scope edits in `ArchiveService.Branch.cs`; missing
  `RecordBranchOperation`, `RecordCheckpointOperation`, and `SnapshotSource` symbols. I did not
  touch or revert those files.
- Old-file fixed-string proof: passed at line 1 of the named May 14 rollout.
- Whole-root streaming baseline: completed in 76,112.5 ms with 3,233 matches.

## Apex Decision Requested

Approve the chunked, contentless, append-aware SQLite FTS5 mechanism above, or redirect the
mechanism before production implementation. The charter explicitly requires this decision before
building, so this lane stopped at the gate rather than silently choosing architecture.
