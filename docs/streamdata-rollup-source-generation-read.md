# Rollup Ladders — Reading a Rollup as the Source of the Next Level

Concept for how one rollup level reads the level below it, so that every level of a ladder
(for example hourly → daily → monthly → yearly) equals the aggregate of its source at all times —
including while the source is being recomputed.

Related: `streamdata-archive-concept.md` (archives, storage layout), the *Optimistic Recompute —
Per-Window Generation Pointer* section of the repository `CLAUDE.md` (the recompute itself).

## 1. Background: what a rollup table holds

A rollup recompute replaces a range of windows without a transaction, because CrateDB has none
that spans statements. It does so with a per-window **generation**:

1. the recomputed rows are copied into the live table under the next generation `N+1`; the rows of
   generation `N` stay where they are,
2. a single-row write in the rollup's pointer table (`archive_<rtId>__genmap`) declares `N+1` the
   active generation of the range — the commit,
3. the rows of the superseded generation are deleted (the sweep).

Between step 1 and step 3 the live table holds **two generations of the same windows**. That is
by design: a reader selects the active one through the pointer, and a crash at any point leaves a
consistent generation readable.

Two properties of CrateDB shape everything below:

- **Search reads see the last refresh, not the last write.** A statement that is not a primary-key
  lookup reads the state as of the table's last refresh (periodic, about once a second, or an
  explicit `REFRESH TABLE`). This applies to inserts, updates and deletes alike, and to the pointer
  table as much as to the data table.
- **There is no snapshot across statements.** Reading the pointer and reading the rows are two
  statements; the pointer can move in between.

## 2. The rule

> Every reader of a rollup table selects one generation per window — the one the pointer names —
> and nothing else. A rollup level that aggregates another rollup level is such a reader.

The query path has always followed this rule. The aggregation that builds a rollup from a rollup
source follows it too: a scan of the source by time alone would aggregate both generations of a
window while the source is between step 1 and the visible end of step 3, and the level above would
come out too high — up to exactly twice its source when the whole bucket is affected, a factor in
between when the sweep is visible on some shards only. Levels further up inherit the error, since
each is built from the one below.

The rule has three parts. Each closes a gap the others leave open.

### 2.1 The source scan carries the generation predicate

When the source is a rollup, the aggregation statement restricts the source scan to the active
generation:

```sql
... FROM <source rollup table>
WHERE "window_start" >= <bucket start> AND "window_end" <= <bucket end>
  AND "generation" = CASE WHEN ("window_start" >= <s> AND "window_start" < <e>) THEN <g> ELSE 0 END
GROUP BY "rtid"
```

- The predicate is rendered by the same code as the query path's (`GenerationFilterSql`), so both
  readers select the same rows.
- Only the pointer entries **overlapping the bucket** are emitted. Entries outside it cannot match
  any of its windows; leaving them out keeps the statement at one `WHEN` in the common case however
  many entries the pointer table has accumulated.
- With no overlapping entry the predicate is `"generation" = 0` — never absent. Rows copied in by a
  recompute that has not committed yet must stay out.
- The switch is **"the source is a rollup"**, not "the source is windowed". Time-range archives are
  windowed as well and have no generation column.

This applies to both writers of a rollup: the recompute executor and the forward aggregation
(closed buckets and the open-bucket refresh).

### 2.2 The pointer is checked around every bucket

The predicate is only as good as the pointer it was built from. Without a snapshot, this sequence
is possible:

1. the reader loads the pointer: generation `N`,
2. the source commits a recompute — pointer to `N+1`, generation `N` swept,
3. the reader's statement runs and asks for generation `N`, which no longer exists.

The level above would then come out **too low or empty** — worse than too high, because nothing
looks wrong. So each bucket is aggregated like this (`RollupSourceBucketAggregator`):

1. read the source's pointer entries overlapping the bucket,
2. run the aggregation statement with the predicate built from them,
3. read the entries again,
4. if they differ, discard the result and start over; give up after three attempts and fail the
   run, which the orchestrators retry.

An unchanged pointer proves the statement ran before the flip of that range was visible, and
therefore before the sweep of the generation it selected (see 2.3 for why the order holds).

The pointer is read **per bucket, immediately around the statement** — not once per chunk or per
tick. A wider interval would reopen the gap for as long as the chunk runs.

When a recompute repeats a bucket, the discarded attempt's rows are removed from the staging table
first, so a series present in the discarded attempt but not in the repeated one cannot reach the
live table. The forward aggregation upserts the generation-0 row in place and needs no cleanup.

### 2.3 The commit sequence makes its own writes visible

Every step another statement depends on is followed by an explicit refresh:

| Step | Statement | Refresh after | Why |
|---|---|---|---|
| 0 | read next generation (`MAX(generation)+1`) | pointer table **before** | a run directly after another would otherwise be handed the same number, and its rows would collide with the previous run's on the primary key |
| 1 | copy staged rows as generation `N+1` | live table | the rows must be readable before they become authoritative |
| 2 | flip the pointer to `N+1` | pointer table | every reader must select `N+1` **before** `N` is removed |
| 3 | sweep generations other than `N+1` | live table | the dependents of this rollup are recomputed right after it; the deleted rows must be gone |
| 4 | delete pointer entries contained in the range | pointer table | readers must not pick up an entry whose rows are gone |

The order of step 2's refresh and step 3 is what the pointer check in 2.2 relies on: once a reader
can observe the old generation as missing, it can also observe the new pointer.

### 2.4 A rewind keeps the pointer of the rows it does not rewind

Rewinding a rollup's watermark to a boundary `B` hands the range from `B` on back to the forward
aggregation (generation 0): pointer entries reaching past `B` are removed, and recomputed rows from
`B` on are deleted. An entry that **straddles** `B` also covers rows before `B`, which are not
rewound and stay on their generation. Before the entry is removed, its part before the boundary is
written as an entry of its own — same generation, same scope, ending at `B`. Without it, every
reader would look for those rows at generation 0 and find nothing. If an entry with exactly that
range already exists, the higher generation is kept.

## 3. What this guarantees

For a ladder of any depth, once the recompute of a range has finished on every level:

- each level equals the aggregate of its source level, for every series,
- regardless of whether the levels run back to back in one orchestrator tick, or a level is
  rebuilt or forward-aggregated while its source is being recomputed,
- no level is short or empty because of a pointer that moved under it,
- a rewind does not hide earlier recomputed windows from the levels above or from queries.

A level that aggregates a source bucket *before* a recompute of that bucket commits reads the
previous generation completely — a consistent older value. The source's completed recompute then
schedules the dependents' recompute for that range, as before.

## 4. Cost

The additions are small against the aggregation work itself. Measured on a local single-node
CrateDB with an archive of realistic size (about 3,400 series, 29 million hourly rollup rows in
three shards), aggregating one day into the daily level:

| Item | Cost |
|---|---|
| aggregation statement, by time only | about 8 ms |
| same statement with the predicate, one overlapping entry | about 8 ms |
| same statement with a predicate over 1,000 entries (the shape avoided by emitting overlapping entries only) | about 64 ms |
| one read of the pointer table (1 to 1,000 entries) | 2–4 ms |
| refresh of the pointer table | 2–4 ms |
| refresh of the live table after a sweep of about 80,000 rows | about 90 ms |

So a bucket read from a rollup source costs two pointer reads more, and a recompute call about
0.1 s more for its refreshes. The first rollup level reads a raw or time-range archive and is not
affected at all. Refresh cost on larger tables and on clusters with replicas has not been measured.

Two alternatives were measured and rejected: selecting the highest generation per key with a
window function (about nine times the statement time), and joining the pointer table as a
correlated sub-query (no result within two minutes for one day).

## 5. Limits

- A source that commits a recompute of the same bucket during each of three consecutive attempts
  fails the bucket; the run is retried by its orchestrator. This needs a source that is recomputed
  continuously over the same range and is not expected in normal operation.
- Pointer entries are keyed by their exact range, so a rollup recomputed over many different
  ranges accumulates entries (roughly one per recomputed range that is not contained in a later
  one). Section 2.1 makes this irrelevant for the aggregation; the query path still renders all
  entries. Pruning entries is a separate topic.
- A manual recompute may write a generation for a bucket the forward aggregation has not closed
  yet; the bucket then exists at generation 0 and at the recomputed generation. Readers following
  the rule above see the recomputed one. Constraining manual recomputes to closed buckets of the
  target is not part of this concept.

## 6. Tests

- `StreamData.UnitTests/RollupSourceGenerationReadTests` — the predicate (overlap selection,
  generation-0 baseline, raw/time-range sources untouched, inside the First/Last ranking
  sub-select), the pointer check (kept, repeated, given up; unrelated ranges ignored), the rewind
  statements.
- `octo-asset-repo-services` → `RollupSourceGenerationReadTests` — against a real CrateDB, raw →
  hourly → daily with several series: the daily level directly after an hourly recompute, repeated
  and without any refresh by the test; the daily level while an hourly generation is copied in but
  not committed, through the recompute and the forward path; a rewind through a recomputed range.
