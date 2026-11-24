---
description: "StreamData rollups: lifecycle, gas-day rollups, optimistic recompute, open buckets, retro reach, late deliveries, manual writes"
applies_to: src/Runtime.Engine.CrateDb/**, src/Runtime.Engine.MongoDb/StreamData/**
---

### Rollup Lifecycle

- `MongoCkRollupArchiveRuntimeStore` extends the runtime-store contract with:
  - `InsertAsync(...)` — builds the `RtCkRollupArchive` entity (Created status,
    `Columns` + `Aggregations` via `AttributeRecordValueList<T>`) and persists.
  - `AdvanceWatermarkAsync` / `SetFrozenUntilAsync` — orchestrator + lifecycle writes.
  - `EnumerateAsync` / `CountActiveRollupsForSourceAsync` — orchestrator tick + source-delete guard.
- `TenantContext.GetRollupArchiveLifecycleService` wires both the rollup store and the shared
  archive store into `RollupArchiveLifecycleService`; the archive store is needed by
  `CreateAsync` to look up the source archive's `TargetCkTypeId`.

### Gas-day rollups: DayStartOffsetMs (AB#7359)

A `CalendarDay` rollup may declare `ReferenceTimeZone` and `DayStartOffsetMs` (System.StreamData 1.22.0; e.g. 06:00 Europe/Vienna =
`21600000`), so one bucket is one gas day of 23, 24 or 25 hours. Rules to keep:

- **One calendar for every path.** Bucket boundaries come from `BucketCalendar.For(rollup)` (engine `Runtime.Engine/StreamData/BucketCalendar.cs`)
  in the live orchestrator, the recompute (`RecomputeBucketEnumerator`, `CrateDbArchiveRecomputeExecutor`), the repair watermark target
  (`RollupRepairer.WatermarkTargetAsync`), the calendar bin axis of a downsampling query (`BuildCalendarBinAxis`) and its local-day bins (`DownsamplingBinAxis`, AB#7360). **Never call the old
  `BucketBoundary.AlignDown/NextBucketEnd/InitialWatermark(alignment, bucketSize, zone)` overloads in this repository**: they silently mean a
  midnight day and put recompute and repair buckets next to the 06:00 buckets of the live pass (a grep for them must stay empty; the
  `FixedSize` call in `DownsamplingBinQuantizer` is the one exception, a fixed grid has no calendar).
- **Persistence.** `MongoRollupArchiveRuntimeStore.InsertAsync(..., long? dayStartOffsetMs)` writes `DayStartOffsetMs`; null and 0 leave the
  attribute unset (midnight day, a row identical to before), `MapToSnapshot` projects it into `RollupArchiveSnapshot.DayStartOffsetMs`; a rollup
  stored without it loads as null.
- **No offset rollup before this is deployed.** An offset rollup must not be created or recomputed by a build without this change.
- Tests: `GasDayRollupIntegrationTests` (real CrateDB: live pass 23/24/25 h through the engine's `RollupOrchestrator`, recompute, repair,
  backfill by the repair's rewind, downsampling bins equal the stored `window_start`), `MongoRollupArchiveRuntimeStoreDayStartOffsetTests`,
  `RecomputeBucketEnumeratorGasDayTests`. No CHANGELOG file exists in this repo; this section is the entry.

### Optimistic Recompute — Per-Window Generation Pointer (AB#4184, Phase 6)

A partial-range rollup recompute must swap the recomputed windows **atomically** even though CrateDB
has no multi-statement transaction. The mechanism is a per-window `generation` pointer:

- **`generation` column** — `CrateDbStreamDataRepository.EnsureArchiveCreatedAsync` provisions rollup
  tables via `ArchiveDdlGenerator.GenerateCreateWindowedTable(..., includeGeneration: true)`, which
  adds `generation BIGINT NOT NULL DEFAULT 0` **and keys it into the PK**
  `(window_start, window_end, rtid, cktypeid, generation)`. Time-range archives pass
  `includeGeneration: false` and are unaffected. Forward aggregation
  (`RollupAggregationSqlBuilder`) always writes generation `0` and includes it in the `ON CONFLICT`
  key, so a forward re-aggregation collapses onto the generation-0 row.
- **Pointer side-table** — `GenerationMapSqlBuilder` creates a tiny `archive_<rtId>__genmap` table
  per rollup (at activation) holding `(range_start, range_end, rtid_scope, generation)`. This is the
  active-generation pointer. **Design note:** the concept doc (§4) places this pointer in "Mongo
  metadata"; we deliberately co-locate it in CrateDB next to the data so the flip is a single-row
  write in the same store — no CK-model bump, no cross-store coordination.
- **Executor flip** (`CrateDbArchiveRecomputeExecutor`) — compute into staging, then **refresh the
  staging table** (CrateDB applies inserts to the read path asynchronously, so the staging→live
  `INSERT … SELECT` would otherwise copy zero rows — found by the integration test below), then:
  (0) leftover guard: rows already under the next generation `N+1` in the range are deleted first;
  (1) `BuildInsertFromStagingSlice` copies the staged rows into the live table stamped with `N+1`
  (the previous generation stays visible); (2) `RefreshArchiveTableAsync`;
  (3) `GenerationMapSqlBuilder.BuildUpsertPointer` flips the pointer to `N+1` — the **atomic commit**;
  (4) `BuildSweepSupersededGenerations` deletes the now-superseded generations in the range; (5) the
  contained pointer entries are deleted; (6) staging is cleared. A crash before the flip leaves
  readers on the previous generation; a crash after the flip but before the sweep just leaves dead
  rows the reclaim pass removes. **Every step the next statement or reader depends on is followed by
  a refresh — the genmap table included:** before `MAX(generation)+1` is read, the live table before
  the leftover guard counts, after the copy, after the flip (readers must select `N+1` before `N` is
  swept), after the sweep (the dependents recompute right after this rollup), after the
  contained-pointer delete. Search reads see the last refresh, not the last write; do not remove one
  of these as "redundant".
- **Row bound (AB#6283)** — the copy (1), the sweep (4), the leftover guard (0) and the reclaim pass
  each run as **one statement per time slice** of the range, and every slice is **counted before it
  is executed** (`ExportSlicePlanner`, the planner of the archive export: propose, count, accept or
  narrow). No statement touches more than twice `StreamData:Recompute:SliceTargetRows` rows,
  whatever a chunk holds — the orchestrator's chunking by buckets bounds the number of aggregation
  statements, not the rows, and an estimate of rows per bucket fails on an archive that is sparse
  for a year and dense afterwards. The bound lives in the executor because only there the rows are
  there to be counted. The unit of commit is unchanged — the whole call: all copy slices write the
  same generation `N+1`, which no reader selects before the pointer names it, the flip is still one
  single-row write, and every sweep slice runs after the flip and its genmap refresh. There is no
  refresh between the slices of one step (disjoint `window_start` ranges). The sweep slices
  partition the **whole** range, not only the staged windows: a window whose source rows are gone
  has no new row, and its old generation still has to go. Do not move the bound into the
  orchestrator as smaller chunks: every chunk writes a pointer entry, and the query path renders
  every entry into its `CASE`.
- **Leftover guard** — the next generation number is read from the pointer table only, so a copy
  that was cut short before its flip leaves rows under the very number the retry is handed. CrateDB
  **skips** a row whose primary key exists in an `INSERT … SELECT` without `ON CONFLICT` (no error):
  the retry would adopt the first attempt's values and keep rows for entities it no longer produces
  (pinned by `RollupRecomputeRowBoundTests.LeftoverRowsAtTheNextGeneration_AreNotAdopted`). Hence
  step (0), restricted to the range and scope of the call.
- **Committed stays committed (AB#6283)** — once the flip and its refresh have succeeded,
  `ExecuteAsync` reports the range as done. A failure in the sweep, the contained-pointer delete or
  the staging cleanup is logged as a warning (rollup, range, scope, generation, step) and **not
  thrown**, unless the caller's token is cancelled (then the orchestrator records an interruption and
  re-runs the chunk, whose sweep `!= N'` removes everything). Throwing there made the orchestrator
  fail the job although readers already saw the new generation, kept the dependents from being
  enqueued, and made every retry aggregate and copy a further full generation only to finish a
  delete.
- **Reclaim pass** — the rows a failed sweep leaves are found from the pointer table, with no extra
  state: for a pointer entry `(range, scope, g)` every reader selects a generation `>= g` inside the
  range (the read `CASE` takes the newest matching entry), so rows there with `generation < g` are
  dead. `ReclaimDeadGenerationsAsync` deletes them in slices, at the start of `ExecuteAsync`: for the
  entries whose sweep failed in this process, and **once per process and rollup for all entries** —
  which is what survives a pod restart and what removes dead rows left before this pass existed. It
  uses `<`, never `!=` (a newer overlapping recompute's rows lie above `g`), re-reads the entry
  before every statement and leaves it alone when it is gone or changed (a rewind in between), and
  never fails the recompute. Not covered: rows *above* every pointer of their range (an uncommitted
  copy) — the leftover guard and the sweep of any later recompute over the range remove those.
- **Staging table** — `archive_<rtId>__rc`, one shard, `number_of_replicas = 0` (disposable: losing
  it fails one call, which is retried), and **kept between runs**. Per process and rollup it is
  dropped and created once, on first use; after that a run of up to `SliceTargetRows` staged rows
  issues no DDL (the rows are deleted at the end, the next run refreshes and counts to make sure it
  starts empty), a larger run drops the table at the end and the next run creates it. Because it is
  kept, `ArchiveTableDrop` drops it with the archive.
- **"Per process" means `RecomputeProcessState`** — the executor cannot remember anything itself:
  `TenantContext` is created per request and per orchestrator tick, it creates its stream data
  repository (`GetStreamDataRepository`), and the repository creates its executor. The staging memo,
  the pointer-table memo, the "reclaim pass done" memo and the remembered failed sweeps therefore live
  in `RecomputeProcessState`, held by the singleton `CrateDbStreamDataRepositoryFactory` and handed to
  every repository and executor; entries are keyed by the schema-qualified live table (tenant schema
  with instance prefix + archive rtId). `ArchiveTableDrop` forgets an archive's entries when this
  process drops its tables; a drop by another process is covered by the executor's "staging table is
  gone" path. Do not put such a memo into a field of the executor, the repository or the tenant
  context — it would be reset with every job (`CREATE`/`DROP` of the staging table and a full reclaim
  pass per job). The public executor constructor, used outside the factory, has a state of its own.
  `RollupSourceBucketAggregator`'s own pointer-table memo is still per instance: the forward path
  issues `CREATE TABLE IF NOT EXISTS …__genmap` once per tick and rollup source.
- **Statement budget (AB#6283)** — `CrateStatementClass { Interactive, Maintenance }`. Every
  statement of the executor and of `RollupSourceBucketAggregator` (so the forward aggregation too)
  is sent as `Maintenance` with the caller's token: `IStreamDataDatabaseClient.ExecuteNonQueryAsync /
  RefreshArchiveTableAsync / GetCountAsync(…, CrateStatementClass, CancellationToken)` and
  `IStreamDataDatabaseManagementClient.ExecuteDdlAsync(…, CancellationToken)`. The counts that size
  the recompute's slices (and its staging and pointer checks) are maintenance statements too — a
  count under the interactive 30 s default in front of a statement with a two-minute budget would be
  the weakest link. The older `GetCountAsync` overloads are unchanged (pipeline default, no explicit
  command timeout). `CrateDatabaseClient`
  runs all non-query paths and the classed count through one helper (`ExecuteWithBudgetAsync`) that
  passes the caller's token **and** the resilience
  pipeline's token to the driver and sets the command timeout explicitly — `Interactive`:
  `StreamData:Resilience:PerAttemptTimeout` (30 s), `Maintenance`:
  `StreamData:Recompute:CommandTimeout`. The pipeline's timeout comes from the call's
  `ResilienceContext` (`CrateResiliencePipeline.CallTimeoutKey`, command timeout + 5 s, so the
  driver's timeout fires first); it is **one budget across the retries**, and there is one pipeline
  and one circuit breaker for both classes (its opening is logged with the operation key
  `<class>.<kind>` and the exception type). CrateDB answers every cancellation with
  `XX000: Job killed`; the helper maps it to `OperationCanceledException` when the caller's token is
  cancelled and to `StreamDataStatementTimeoutException` when the budget elapsed, and passes any
  other kill through (`ClassifyJobKilled`). "Budget elapsed" is either of two things: the attempt ran
  for its command timeout, or the pipeline's call-wide timeout cancelled the call — the command
  timeout restarts with every retry and only after the connection lease, so a late attempt is ended
  by the pipeline before its own command timeout, and CrateDB answers that with the same `XX000`
  (Npgsql turns only `57014` into a cancellation, so Polly never sees one to convert). That exception is deliberately not a connection failure for
  `RecomputeOrchestrator.IsTransientConnectionFailure`: a statement that exceeded its budget will
  exceed it again under the same load, and the range-level retry is the answer.

  | Option | Env var | Default | Meaning |
  |---|---|---|---|
  | `StreamData:Recompute:SliceTargetRows` | `OCTO_STREAMDATA__RECOMPUTE__SLICETARGETROWS` | `100000` | rows a copy / sweep / reclaim statement aims at (at most twice that); also the keep-or-drop threshold of the staging table. `> 0`. |
  | `StreamData:Recompute:CommandTimeout` | `OCTO_STREAMDATA__RECOMPUTE__COMMANDTIMEOUT` | `00:02:00` | command timeout of every `Maintenance` statement. `>= 1 s`. |

  Bound to `CrateRecomputeOptions` in `AddCrateDbStreamDataRepository` (and, when the host has an
  `IConfiguration`, in `AddStreamDataDatabase`); the host's `RecomputeOrchestratorOptions` share the
  section under other property names. Not bounded by this: the rewind's
  `DELETE … window_start >= B AND generation != 0` is still one statement over everything behind
  the boundary.
- **A rollup as the source of the next level** — `RollupAggregationSqlBuilder` reads a rollup source
  through the same predicate as the read path (`GenerationFilterSql`, entries overlapping the bucket
  only, `generation = 0` when none), in the recompute executor and in the forward
  `AggregateBucketAsync` alike. `RollupSourceBucketAggregator` reads the source's pointer before and
  after each bucket statement and repeats the bucket when it moved: without a snapshot across
  statements the predicate alone can ask for a generation that was swept in between, and the level
  above would come out short. A scan by time alone would count both generations of a window during
  a source recompute (the level above up to 2x its source). Switch on "source is a rollup", never on
  "windowed" — time-range archives have no generation column. Full reasoning, guarantees and cost:
  `docs/streamdata-rollup-source-generation-read.md`.
- **Read path** — the four windowed query methods call `LoadGenerationRangesAsync` (reads the genmap)
  and pass the ranges to `CrateQueryBuilder.WithGenerationRanges`; `CrateQueryCompiler` emits
  `"generation" = CASE WHEN <range> THEN <gen> … ELSE 0 END` (ranges ordered newest-generation-first
  so an overlapping re-recompute wins). Empty genmap ⇒ the baseline `generation = 0`, never no
  predicate — rows a recompute has copied in but not yet committed must stay hidden.
- **Integration test:** `RollupRecomputeGenerationPointerTests` (in `octo-asset-repo-services`,
  reusing its CrateDB+Mongo `StreamDataFixture`) drives the real executor end-to-end against a CrateDB
  Testcontainer and asserts the generation flip, the no-mixed-read filter (an injected uncommitted
  generation stays hidden), and the post-flip sweep. This is the automated replacement for the
  previously-manual live validation; it caught both the staging-refresh bug above and the empty-genmap
  baseline-filter bug. `RollupRecomputeRowBoundTests` (slice target 20) covers the slices, the
  leftover guard and the reclaim pass against the same CrateDB, `RollupRecomputeStatementBudgetTests`
  a 300,000-row recompute under a one-second statement budget and the timeout / cancellation
  mapping. The commit sequence itself is pinned without a database by
  `CrateDbArchiveRecomputeExecutorTests` (`RecomputeCrateSimulator`).
- **Upgrade self-heal:** `EnsureWindowedTableShapeAsync(requireGenerationColumn: isRollup)` drops an
  existing rollup table that is on the windowed shape but lacks the `generation` column (provisioned
  before Phase 6) so the subsequent `CREATE TABLE IF NOT EXISTS` re-adds it with the generation-keyed
  PK; the orchestrator re-aggregates on the next watermark advance (the same lossy-but-self-healing
  trade-off as the pre-Phase-7 single-timestamp migration). No-op once the column is present.
- **Caveats:** rollup tables provisioned *before* Phase 6 lack the generation column/PK — handled by
  the upgrade self-heal above (dropped + recreated); `LoadGenerationRangesAsync` also tolerates a
  missing genmap table on the read side. Per-rtId scoped recompute is supported: the executor restricts
  aggregation, pointer entry (`rtid_scope` = the entity's rtId) and sweep to that entity, and since
  AB#5189 the drain merges and runs obligations per scope. A `rewindRollupWatermark` through a
  recomputed range (`ClearRecomputeGenerationsAsync`) keeps the part of a straddling pointer before
  the boundary as an entry of its own (`BuildTruncateStraddlingPointers`) — those rows are not
  rewound, stay on their generation, and would be invisible to every reader without a pointer.

### Open-Bucket Refresh + Recompute Cap (AB#4306)

The forward `RollupOrchestrator` optionally re-aggregates the **current open (in-progress) bucket**
on every tick at `generation 0`, without advancing the watermark, so a coarse partial period (*this
month / this year so far*) stays live instead of showing nothing until the period closes. Controlled
by `RollupOrchestratorOptions.RefreshOpenBucket` (`StreamData:Rollup:RefreshOpenBucket`, default
**true**, wired in `TenantContext.GetRollupOrchestrator`); a frozen bucket is skipped.

The generation pointer makes this fragile: if a **recompute** also claimed the open bucket, its
`BuildUpsertPointer` flip would move that window to generation `N+1`, and the highest-generation-wins
read path would then **mask** the refresh's `generation 0` write — freezing the partial-period total.
So `RecomputeArchiveInternalAsync` **caps `to` at `BucketBoundary.AlignDown(now, …)`** (the start of
the bucket containing `now`), leaving only closed buckets in range; an all-open range becomes a
`Completed` no-op. This keeps the open bucket on the always-fresh generation-0 lane. In a cascade the
open bucket of each level is refreshed cheaply from the level below, so the current period stays live
all the way up (see also the voest cascade Design Decision in the `voest-app` repository).

### Recompute-Work Purge + Fail-Fast on Non-Activated Rollups (AB#4300)

The periodic drain (`RecomputeOrchestrator.TickAsync`) skips any rollup that is not `Activated`, so
queued work on a non-activated rollup would otherwise sit at `Pending` forever. Two guards, both
reachable from `TenantContext` DI:

- **Fail-fast at the enqueue/manual entry points** — `EnqueueBackfillFromSourceAsync` and the manual
  `RecomputeArchiveInternalAsync` path return a `Failed` job immediately when `rollup.Status !=
  Activated`, instead of pre-creating a `Pending` job + range the drain would skip.
- **Purge on disable/delete** — `ArchiveLifecycleService.DisableAsync`/`DeleteAsync` clear
  `PendingRecomputeRanges` and terminate the single active (`Pending`/`Running`/`Swapping`) job as
  `Failed`. `TenantContext` wires `GetArchiveRecomputeStateStore()` + `GetRecomputeJobStore()` into
  the lifecycle service (both optional ctor params, so the backward-compatible construction still
  works). Prevents a delete+re-import that reuses the same rtId from inheriting stale ranges/jobs.

### Bounded Retro Reach — Automatic-Recompute Cap (AB#4196)

Automatic recompute is driven by `RetroactiveWriteDetector.TryBuildDirtyWindow`, called from every
`CrateDbStreamDataRepository` insert path. AB#4196 adds a cap on how far *before* the consumed
watermark a single very-late write may drag that automatic recompute — otherwise one stray old
timestamp schedules a recompute of years of history.

- **Config:** `Archive.MaxRetroactiveReachMs` (per source archive, `System.StreamData` 1.6.8, config
  not runtime-state, `null` = unbounded) + the host `StreamData:Recompute:MaxRetroactiveReachHardLimitMs`
  fleet ceiling on `StreamDataConfiguration`. `CrateDbStreamDataRepository.ResolveEffectiveRetroReach`
  computes `min(perArchive, hardLimit)`.
- **Primary enforcement (detection):** the detector floors the automatic dirty window at
  `consumedWatermark - effectiveCap`; a fully-out-of-reach batch records no window. When any
  retroactive timestamp is dropped it sets `reachCapped`, and the repo logs a `WARN` so the operator
  can run an unbounded manual `recomputeArchive` for the deeper tail.
- **Belt-and-suspenders (propagation):** `RecomputeOrchestrator.PropagateDirtyWindowsAsync` loads the
  source's per-archive cap and passes it to `EnqueueOnDirectDependentsAsync`, which floors each
  dependent's stale-range start at `dependentWatermark - cap` (bucket-aligned). This bounds any dirty
  window recorded before the cap existed. Chain propagation after a committed recompute passes a
  `null` cap — manual/chained recompute stays unbounded, matching `recomputeArchive` /
  `rewindRollupWatermark`.

### Late Deliveries Refresh Only Their Series, In Place (AB#9016)

`DetectAndRecordRetroactiveWriteAsync` records the distinct rtIds of the late, in-reach automated rows on the dirty window
(`RetroactiveWriteDetector.TryBuildDirtyWindow(..., maxScopedRtIds, ...)`). Above `StreamData:Recompute:MaxScopedRtIds` it
records none, which means every series. The engine's orchestrator debounces the obligations and runs them as one
set-scoped refresh: `CrateDbArchiveRecomputeExecutor.ExecuteScopedRefreshAsync`, to which the repository delegates. Rules
to keep:

- **Stage like the full recompute, write at the active generation.** The aggregation, the expected-row count and the LOCF
  carry are restricted with `RollupAggregationSqlBuilder.ScopeCondition` (`rtid IN (...)`). The staged rows are upserted
  into the live table with `RollupRecomputeSqlBuilder.BuildUpsertFromStagingAtActiveGenerationSlice`, whose generation is
  `GenerationFilterSql.RenderActiveGeneration` over the pointer entries with scope `''` or one of the series. The reader
  predicate is built from the same function, so the two cannot drift.
- **No generation, no pointer, no sweep.** Do not add a flip or a generation sweep to this path: avoiding them is the
  point (the cleanup DELETEs dominated CrateDB cost on prod-2).
- **Refresh marker.** The marker is `BuildReadRefreshMarker`, the `rtchangeddatetime` of the oldest staged row. Rows the
  upsert wrote are at least as new; untouched rows are older.
- **Write check.** A short slice is written once more and then fails (AB#8680). The check counts only freshly written
  rows (`BuildCountRefreshedInScope`).
- **Vanished buckets.** After the upsert, the rows of the series at the active generation in the range that are older
  than the marker (all of them when nothing was staged) are removed (`BuildStaleInScope`, count first, DELETE only when
  needed). Without this step the refresh would be weaker than the full recompute.
- **Not atomic across rows.** While a refresh runs, or after it failed part way, readers can see some buckets of the series
  refreshed and others not yet; every written row holds its final value and the retry completes the rest. The full
  recompute keeps its all-or-nothing flip.
- **Serialised by the engine.** The active-job guard keeps every other recompute of the rollup out. The source's pointer
  entries are read unscoped (a superset is correct).
- Tests: `LateDeliveryScopeTests` (unit), `StreamDataLateDelivery/ScopedRefreshEquivalenceTests` (real CrateDB). Concept:
  `octo-construction-kit-engine/docs/concept-rollup-recompute.md` §4a.

### Manual Writes Reach the Rollups (AB#7594)

`DetectAndRecordRetroactiveWriteAsync` splits a batch by origin. Automated rows keep the single, reach-capped
dirty window above. Manual rows go through `RetroactiveWriteDetector.BuildManualDirtyWindows`: **no reach cap**
(a person corrected a known window; a rollup must not keep the replaced value) and **one window per cluster** of
corrected windows (`ManualClusterGap` = 1 day), source `RecomputeChangeSource.Manual`, so two corrections months
apart recompute two small ranges instead of everything in between. The recompute itself is the existing
bounded, generation-flipped path (AB#5483, AB#6283); a replaced value is recomputed away, never added twice.
Tests: `RetroactiveWriteDetectorTests` (unit), `RollupManualWriteTests` (asset-repo integration, real CrateDB).

### Dirty-Window End Survives the Store (AB#8621)

A dirty window is a MongoDB record (`CkArchiveDirtyWindow`), and a BSON date holds milliseconds. The detector used to
end a window at `latest + 1 tick`; the store cut that back to `latest`, and for a timestamp on a bucket boundary (every
hourly value is stamped on the hour) the planner's half-open alignment (`RecomputePlanner.AlignRangeToBuckets`) ended
the recompute range at `latest`, so the bucket that holds the latest value of a late delivery was never recomputed.
Only a later delivery that overlapped it repaired it, so the last bucket before a pause in the source stayed missing
for good (voestalpine, test-2 13.10. 03:00Z and staging-1 30.10. 04:00Z, hourly rollup `6a4268000000000000000001`).
Both detector paths (`TryBuildDirtyWindow`, `BuildManualDirtyWindows`) now end the window at
`RetroactiveWriteDetector.ExclusiveEndAfter(latest)`: `latest` cut to `StoredWindowResolution` (1 ms) plus 1 ms. The
value is on the stored grid (the round trip leaves it unchanged) and never reaches into the next bucket. The forward
`RollupOrchestrator` was never involved: it closes buckets by the clock, and a bucket closed before its data arrived
is the recompute path's job. Buckets lost before the fix are closed with `octo-cli -c RecomputeArchive` over the
range (documentation: *Rollups & recompute*, "Closing buckets a late delivery missed"). Tests:
`RetroactiveWriteDetectorBucketBoundaryTests` (detector, BSON date conversion, planner alignment; red with the old end).
