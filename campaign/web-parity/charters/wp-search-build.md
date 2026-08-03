# Charter — `wp-search-build`: make deep search actually find things

The owner's deliverable, verbatim: search that is "useful and working" on their REAL chats, tested
on them. Not a design doc. The benchmark is already done — this is the build.

Repo: Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval (master). Persistent tandem peer; fan
out sealed junior lanes; re-run every verifier yourself.

## The measured baseline (facts from this machine — do not re-derive, do not contradict)

Corpus: 9,258 transcript files, 42.1 GB (claude 6,041 / codex 3,217).
- Native `DeepSearchContentAsync` (ArchiveService.cs:936) reads **2.30 GB per query** and can see
  **5.5% of the corpus**. Two caps, not one: `.Take(3000)` newest sessions AND
  `MaxIndexedFiles = 4000` (:25) — different code paths. Plus within-file truncation: files over
  128 MB get first 64 MB + last 64 MB, middle skipped.
- **41 files exceed 128 MB. The largest single file is 5.19 GB.**
- Web (`DiscoveryApi` → `FilterChats` → `Search()` :814 → `SearchText`) searches the **last 6,000
  chars** per session, silently — not metadata-only, as was previously believed.
- Invisible today: 39.5 GB across 6,258 files (age) + 0.30 GB (big-file middles).
- `rg` literal over the whole corpus, warm: **31.8 s**, and it is the correctness oracle.

Three surfaces run three different engines over three different corpora. Same query, three answers.

## Design (settled with a deep-tier partner — build to this)

1. **Sub-file indexing is a correctness requirement, not a ranking preference.** A 5.19 GB file
   cannot be an indexable unit. Index at TURN level; a fixed-size CHUNK fallback is acceptable for
   pathological files — do NOT let perfect turn semantics on the worst 41 files block shipping for
   the other 9,217.
2. **Term frequency is anti-signal.** In agent transcripts high TF usually means tool output, the
   opposite of relevance. Role-weighted fields are mandatory: user ≫ assistant prose ≫ tool output.
   A stock FTS drop-in will feel bad in a specific, predictable way.
3. **SQLite FTS5**, index stored beside `app-store.json`, disposable and rebuildable. Do not
   duplicate transcript text into SQLite where avoidable — store offsets and read the source for
   snippets.
4. **Append-aware incremental.** Files grow while an agent writes them. Track per file: stamp,
   indexed byte length, tail hash. Unchanged ⇒ no read. Append-only growth ⇒ index only the delta.
   Shrink / same-size mutation / tail-hash mismatch / index-version change ⇒ rebuild that file only.
   (Assume shrink HAPPENS — this corpus has NUL-corruption history.)
5. **FileShare discipline is non-negotiable:** open sources `FileShare.ReadWrite | FileShare.Delete`.
   A default-share read makes the live agent silently DROP A TURN — this codebase has already been
   burned by exactly that. An indexer that gets this wrong destroys the data it indexes.
6. **Index DECODED text.** On-disk JSONL is escaped; indexing raw makes exact-phrase search lie.
7. **NEVER CAP SILENTLY.** The empty state is a COVERAGE STATEMENT, not an existence claim.
8. **Grep is the escalation lane, not the competitor.** When results are partial or empty, run an
   exhaustive literal scan of the UNINDEXED REMAINDER — bounded (≤32 s, shrinking to zero as the
   index completes) — and stream late hits in ("2 more matches found in unindexed chats"). With
   this lane the UI is never in a position to assert absence it has not verified. Keep it wired
   permanently; a design whose exhaustive lane atrophies will eventually lie again.
9. **First index must be usable before it finishes:** newest-first priority so the tool works while
   it builds, with honest progress.
10. **Then unify the surfaces**: native, `DiscoveryApi`/web, and the agent tool must query ONE
    engine. Three engines is the root defect behind "same query, different answers".

## The benchmark (build it FIRST, it is how we know we won)

Stratify by MECHANISM OF EXCLUSION — one stratum per defect, roughly equal query counts, NOT
proportional to bytes (that would dump ~95% of queries into the age cell and flatter the fix):
(a) beyond the 3,000-session query cap; (b) beyond the 4,000-file index cap (distinct path);
(c) inside a >128 MB skipped middle — for the 5.19 GB file sample at start / ~2.5 GB / tail, which
after the fix also tests jump-to-turn at extreme offsets; (d) reachable only via the web's 6 KB
tail; (e) appended to a live file minutes before the run; (f) encoding traps (quotes, newlines,
turn-spanning); (g) tool-output-turn vs user-turn provenance. PLUS one proportionally-sampled
stratum as the headline "what does a random real memory feel like" number.

Queries are PLANTED-BY-CONSTRUCTION: extract a distinctive phrase from a known file at a known
offset, so ground truth is exact. `rg` is the oracle and a baseline ROW in the results table.
Metrics: success@10 and rank-of-target per stratum; distinct conversations in top-10; latency
p50/p95 cold and warm PER SURFACE; % of hits the UI can actually navigate to; % of partial searches
that say they are partial. Harness runs in-process against the REAL store
(`CodexLocalRetrieval.Native.Tests` exists; `DiscoveryApi` is HTTP-agnostic by design).

## Definition of done
The stratified table flips red→green, measured on the owner's real corpus, before and after, in
your fold. A phrase the owner remembers from an old chat is found and openable. Report real
numbers: first-index wall time, index size on disk, incremental sync time, query p50/p95.
Write-scope: `ArchiveService*.cs`, `DiscoveryApi.cs`, new index files, search UI call sites,
`relay/public/chats.*`, tests. Fold: `campaign/web-parity/folds/wp-search-build.md`.
