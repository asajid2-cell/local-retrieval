# Deep search: storage cost and growth (measured 2026-08-02)

Every number here was measured on the owner's real machine (CRACKERBARREL), not estimated. Re-run
the commands to refresh; do not edit the numbers without re-measuring.

## The corpus this indexes

| | |
|---|---|
| transcript files | 9,267 (claude 6,041 · codex 3,217 at time of measure) |
| total bytes | 39.2 GB |
| largest single file | 5.19 GB |
| files over 128 MB | 41 |
| C: free at measure | 46.9 GB |

## The index

| | |
|---|---|
| steady-state size | **12.5 GB** |
| ratio to source | **31 %** of indexed bytes |
| first full build | **36.7 min** (2,199,464 ms) over ~40 GB |
| WAL during build | 5.6 GB — **checkpoints to 0** when the build completes |
| schema | FTS5, `content=''`, `contentless_delete=1` — transcript text is NOT duplicated; the 12.5 GB is the inverted index |

The 5.6 GB WAL is transient and alarmed us once. It collapsed on checkpoint and free space went UP.
If a future run leaves a large WAL behind, that is a bug (missing checkpoint), not the design.

## Growth, and the runway that actually matters

Measured by file creation date:

| window | corpus growth |
|---|---|
| last 7 days | 0.06 GB/day |
| last 14 days | 0.06 GB/day |
| last 30 days | 0.17 GB/day (**~5.1 GB/month**) |

The 30-day rate is ~3x the 7/14-day rate: recent weeks were quieter than the month, so growth is
bursty and campaign-driven. This counts file CREATION only, so appends to existing transcripts are
undercounted — treat 5.1 GB/month as a FLOOR.

- index growth ≈ **1.6 GB/month** (31 % of corpus growth)
- index alone would consume 46.9 GB free in ~29 months
- **corpus + index together: ~7 months** ← the number to watch

**The index costs roughly five weeks of disk runway.** The corpus grows 3x faster than its own
index, so search is about a quarter of the problem and cannot be the fix for it.

## Decision on record: keep `detail=full`

FTS5's `detail=` currently defaults to `full` (token positions stored). Dropping to `detail=column`
would save an estimated 3-5 GB — about one month of runway — and would **break exact-phrase search**.
Phrase search is the owner's primary query mode (`[petunia]` codename lookups, remembered
sentences), so trading it for one month is a bad deal. Revisit only if disk becomes acute AND
phrase search has been proven unused.

Better levers, in order, if disk gets tight:
1. The 41 files over 128 MB hold a disproportionate share of 39.2 GB and are archival, not working
   data. Compress or offload them — frees multiples of anything index tuning can.
2. Prune/compress transcripts older than N months (they stay searchable if the index is retained).
3. Only then consider index tuning.

## Refresh model (why there is no cron job and no scan button)

The index rides the existing sync — `StartTranscriptSearchIndexBuild()` fires at app startup and at
the end of `MergeScanAsync`, so every sync (launch, the 30 s cycle, the Sync sessions button)
catches it up. A build already in flight will not stack a second one.

Per file the `files` table stores `indexed_length`, `observed_length`, `tail_hash`,
`last_write_ticks`, `index_version`, `complete`:
- unchanged stamp + hash → **the file is never opened**
- grew → only the appended delta is read
- shrank / same-size mutation / hash mismatch / index-version bump → that ONE file rebuilds
  (this branch is load-bearing here: the NUL-corruption history means files do mutate in place)

**Measured proof, immediately after the first build:** a re-sync would read **1 file of 6,965** and
effectively **0 bytes**, against 37.4 GB indexed — 0.0000 % of the corpus versus 100 % for a naive
reparse.

## How to re-measure

```
# index + WAL size, and disk headroom
powershell -Command "Get-Item $env:LOCALAPPDATA\CodexLocalRetrieval\search-index.sqlite* | Select Name,Length; Get-PSDrive C"

# what a re-sync would read (incremental proof)
#   query the files table: sum(max(observed_length - indexed_length, 0)) and the count of rows
#   where observed_length > indexed_length.  NOTE: Python's bundled sqlite3 is too old to open the
#   FTS table (no contentless_delete); the `files` table reads fine, and full FTS queries need the
#   app's Microsoft.Data.Sqlite (3.49.1).
```
