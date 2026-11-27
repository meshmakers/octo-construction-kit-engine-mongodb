---
description: "StreamData queries and export: paged order, time filters, integrals, downsampling, native copy, restore, archive export"
applies_to: src/Runtime.Engine.CrateDb/QueryBuilder/**, src/Runtime.Engine.CrateDb/Copy/**, src/Runtime.Engine.MongoDb/StreamData/**
---

### Paged Queries Have a Total Order

Every stream-data query whose result is paged has a total, deterministic row order. CrateDB returns the rows of a
sharded table in any order per call, so LIMIT/OFFSET (or a caller slicing a re-executed result) without a total order
repeats some rows and skips others. Rules to keep:

- **Simple query** (`ExecutePaginatedQueryAsync`): `CrateQueryBuilder.EnsureDeterministicOrder` appends the row key
  (`RowKeyColumns`: raw `timestamp, rtid, cktypeid`; windowed `window_start, window_end, rtid, cktypeid` - the primary key
  without `generation`, a reader sees one generation per window) after the caller's sort orders, skipping key columns the
  caller already named. Without a sort order it is the default order. Do not make it conditional on a sort order or a page
  size: a LIMIT without ORDER BY returns an arbitrary subset too.
- **Grouped aggregation**: ordered by all group-by columns (query builder path and `TimeWeightedQuerySqlBuilder`); the
  GraphQL connection runs it again per page and slices the result.
- **Downsampling**: ordered by bin, then the series columns (`CompileDownsamplingQuery`); the bin axis is filled in that order.
- **Cost**: the deep pages of an unfiltered large archive pay a sort (2.65 s at offset 2.9 M on the single-node test
  container, 28 ms before); measured by `StreamDataPaging/StreamDataPagingOrderCostMeasurement`. Accepted for
  correctness; a keyset cursor would be the next step if it matters.

Tests: `CrateQueryBuilderTests` (`EnsureDeterministicOrder_*`), `TimeWeightedQuerySqlBuilderTests`,
`StreamDataPaging/StreamDataPagingIntegrationTests` (real CrateDB). The single-node test container returns the shards in a
stable order, so it cannot show the duplicates of a multi-node cluster; the tests pin the order itself.

### Query Time Filter — One-sided Ranges

`StreamDataQueryOptionsBase.From` / `.To` are independently optional. The three read paths
(`ExecuteQueryAsync`, `ExecuteAggregationQueryAsync`, `ExecuteGroupedAggregationQueryAsync`) pass
whatever is set to `CrateQueryBuilder.WithTimeFilter(DateTime?, DateTime?)`, and
`CrateQueryCompiler.AppendWhereClause` emits **one predicate per set boundary**:

| Boundaries | Raw axis (`timestamp`) | Windowed axis (`window_end`, overlap semantics) |
|---|---|---|
| From + To | `ts >= from AND ts <= to` | `window_start < to AND window_end > from` |
| From only | `ts >= from` | `window_end > from` |
| To only | `ts <= to` | `window_start < to` |
| neither | no time predicate | no time predicate |

`CrateQueryBuilder.HasTimeFilter` (`From is not null || To is not null`) is the single condition
driving the `WHERE` emission and the inter-condition `AND` wiring (ckType, IN-lists, field filters,
generation predicate) — never a `{ From: not null, To: not null }` pattern. Before the one-sided time filter was supported both
boundaries were required at every level, and a one-sided range was dropped **silently**: a
persisted SD-query or a `GetQueryById@1` pipeline node configured with only a start returned the
entire archive. `CompileCountQuery` shares `AppendWhereClause`, so `TotalCount` is scoped
identically to the page.

**Still closed-range-only:** downsampling (needs both to derive the bin width — validated in
`ExecuteDownsamplingQueryAsync`, so `AppendDownsamplingSourceFilters` never sees a one-sided range)
and `TimeWeightedAverage` / `StateDuration` / `Integral` over a raw archive (the LOCF carry-in is defined
relative to the window — explicit guard that throws the contracts `StreamDataException` with the archive id,
a structured 400 / coded GraphQL error; it used to be the uncoded connection-level exception).

### Integral over raw archives

`AggregationFunction.Integral` is the third LOCF column kind of `TimeWeightedQuerySqlBuilder` (`IntegralColumn`, next to
`TwaColumn` / `StateColumn`): `SUM(CASE WHEN v IS NOT NULL THEN v * dt_ms END) / 3600000.0`, alias `{column}_integral`, value
unit × hours (kW → kWh). Step only (the last row holds until the next row of the same rtId or the window end), carry-in from
the last row before `from` (engine default lookback), rtIds summed (grouped: per group), NULL rows contribute 0, an empty
window without carry and a window with only NULL values are `null`; the carry row is looked up at most 35 days back. Routed by `IsLocfFunction` in both aggregation entry points; a time-range archive is
refused up front (`RejectIntegralOnTimeRangeArchive`). Never split a signed power with a
field filter (it selects the LOCF event set and would carry the last positive value across export periods); use a computed
column. Semantics: `octo-construction-kit-engine/docs/concept-time-weighted-aggregation.md` §6.5. Tests: `TimeWeightedQuerySqlBuilderTests`,
`CrateDbStreamDataIntegralRoutingTests` (unit), `StreamDataIntegral/StreamDataIntegralIntegrationTests` (real CrateDB: cadence,
on-change rows, gap, carry, empty, NULL, several rtIds, grouped, missing window, and the 1 % meter check from recorded grid rows
in `testData/integral/grid-5s-meter-check-*.csv`: the window and a fresh window bounded by two register reads).

### Integral over rollups

A rollup with a `TimeWeightedAvg` aggregation on the column answers `Integral` from its stored integral: `SUM("{base}_integral") /
3600000.0` (value·ms → value × hours), alias `{path}_integral`, in aggregation, grouped and downsampling queries; a cascade reads the
SUM-chained integral column (`RollupQueryAggregationResolver`, `RollupChainAggregationResolver`; only the TWA numerator qualifies, never
an AVG numerator). A rollup without it is refused after the chain resolver found nothing (`RejectUnresolvedRollupIntegral`, structured
`StreamDataException` naming the missing TimeWeightedAvg) - the single-column fallback would emit `INTEGRAL("col")`. Downsampling with
Integral over a raw or time-range archive is refused too (`RejectIntegralDownsamplingWithoutRollup`). Edge buckets follow the rollup read
rule (overlap, whole buckets): only bucket-aligned windows equal the raw Integral. Tests: `CrateDbStreamDataIntegralRoutingTests`,
`RollupQueryAggregationResolverTests`, `RollupChainAggregationResolverTests` (unit), `StreamDataIntegral/StreamDataRollupIntegralIntegrationTests`
(real CrateDB: 5-minute TWA rollup and an hourly SUM-chained rollup over it, aligned, rtId scope, grouped, downsampling per hour,
unaligned window, empty window, AVG-only column, the recorded meter rows). Semantics: `octo-construction-kit-engine/docs/concept-time-weighted-aggregation.md` §6.5.

### Several StateDuration columns on one attribute

The output key of an aggregation column is `{column}_{functionSuffix}` and a wire contract (Refinery Studio `aggregationFunctionSuffix`,
MeshBoard widgets, persisted queries). `StreamDataAggregationOutputNaming` owns the rule, for the engine row keys and the GraphQL wire mapping alike:

- a single StateDuration column on an attribute keeps `{column}_stateduration` (unchanged);
- several StateDuration columns on the same attribute (path compared case-insensitively) get `{column}_stateduration_{qualifier}`, e.g.
  `position_stateduration_0` and `position_stateduration_100`. Qualifier = trimmed comparison value, lower-cased; letters and digits kept,
  `-` becomes `neg`, other characters `_` (runs collapsed, trimmed), nothing left becomes `x` (`-1.5` is `neg1_5`).
- the rollup read path (`RollupChainAggregationResolver` / `RollupQueryAggregationResolver`) takes the comparison value and the suffix: it reads the
  materialised StateDuration whose comparison value equals the column's (before, it read the first StateDuration of the path whatever the
  state was); a state the rollup does not materialise is refused (`RejectUnresolvedRollupStateDuration`). Two StateDuration specs of one path
  need distinct `targetColumnName`s on the rollup, or they share one physical column.
- `ValidateDistinct` refuses columns that would share an output key (same attribute and function twice, states that reduce to one qualifier) with a
  readable `StreamDataException`; the aggregation, grouped and downsampling entry points call it before any SQL.
- Rejections, all readable `StreamDataException`s via `InvalidLocfQuery`: TimeWeightedAverage / StateDuration in downsampling over a raw or
  time-range archive (`RejectLocfDownsamplingWithoutRollup`), StateDuration over a time-range archive (`RejectStateDurationOnTimeRangeArchive`).

Tests: `StreamDataStateDurationAliasTests`.

### Downsampling Query — Single Grouped Scan, Bin Axis Built Caller-Side

`CrateQueryCompiler.CompileDownsamplingQuery` emits **one grouped aggregation over the filtered
source rows — no join at all**:

```sql
SELECT DATE_BIN(<interval>, d."<binColumn>", <origin>) AS "T"
     [, d."<groupCol>" AS "<groupCol>"]
     , AVG(d."<col>") AS "<alias>"
     , COUNT(d."<timeAxis>") AS "__binCount"
FROM <archive_table> AS d
WHERE 1 = 1 [AND d."window_end" <= DATE_BIN(...) + <interval>] AND <source filters>
GROUP BY DATE_BIN(<interval>, d."<binColumn>", <origin>)[, d."<groupCol>"]
ORDER BY DATE_BIN(...) ASC[, d."<groupCol>" ASC]
```

Bins with no source row are **not** in the result set. `CrateDbStreamDataRepository.
ExecuteDownsamplingQueryAsync` materializes the full axis instead: it walks bin
`0..effectiveLimit-1`, emits the SQL rows for bins that have data and one all-null row
(`__binCount = 0` semantics) for every gap, via `MapDownsamplingRow`. Rows whose bin falls outside
the axis — possible when interval rounding makes `limit × interval` fall short of the range — are
dropped, matching the old inner-join-to-axis behaviour.

**Why no bin-axis join.** Two earlier shapes joined a `generate_series` axis against the data:
first directly against the source table (`ON DATE_BIN(...) = bins.ts`), then against a
pre-aggregated subquery. CrateDB cannot hash-join a table function, so either way the join is a
nested loop whose cost grows with bins × joined-rows. The subquery variant helped only while the
aggregated side stayed small; with per-series grouping the aggregated side is itself
`bins × series`, making the whole thing **quadratic in the bucket count**. Measured live on a
31-day, 31-series chart on a demo tenant: 50 buckets 0.5 s, 200 buckets 3.9 s, 670 buckets past the
30 s `CrateResiliencePipeline` timeout — while the same range at 50 buckets scanned exactly the
same source rows in half a second, proving the scan was never the problem. Without the join the
query is O(source rows); the caller-side axis fill is O(bins).

**Shared bin geometry.** `CrateQueryBuilder.WithDownsampling` computes
`DownsamplingIntervalSeconds` (rounded to whole seconds, min 1 — truncation would put the bin below
the source resolution and, with the windowed containment predicate, drop every row) and
`DownsamplingOrigin` (UTC, truncated to the millisecond precision `Constants.DateTimeFormat`
renders). The compiler and the repository both read those, so the SQL's `DATE_BIN` grid and the
caller-side axis can never drift — a sub-millisecond origin would otherwise make every per-bin
lookup miss.

Semantics preserved:

- **§7 fully-contained predicate (windowed archives):** bins on `window_start`; the lower half
  (`window_start >= bin start`) is implied by `DATE_BIN` keying on `window_start`, so only
  `window_end <= DATE_BIN(...) + interval` is emitted. Straddling windows are still dropped.
- **Per-series group columns:** grouped and ordered in SQL; the caller's gap-fill emits
  a single null row per empty bin, as the old LEFT JOIN did.
- **Bin geometry — declared grain for rollups, distinct-bin clamp otherwise:** §7 keys the bin on `window_start` and drops any window whose `window_end` overruns
  the bin, so for windowed archives the bin width must be an *integer multiple of the grain*.
  **Rollup archives** get that by construction:
  `DownsamplingBinQuantizer.QuantizeToGrain(requestedLimit, range, snapshot.Period)` derives
  `merge = round((range/limit)/grain)`, `interval = merge × grain`,
  `effectiveLimit = ceil(range/interval)` and passes the interval *explicitly* into
  `WithDownsampling(limit, from, to, intervalSeconds)` — no probe, no data dependence. The former
  route (probe `COUNT(DISTINCT window_start)` → `Quantize` → re-derive the width as
  `round(range/effectiveLimit)`) is wrong in two ways: the count is of windows *with
  data*, so a single empty grain slot (event-driven series) makes the re-derived width drift off
  the grain (288 five-minute slots with 3 empty → 303 s bins → all but ~5 buckets read null,
  observed in production as "sensor data stopped hours ago"); and even a complete count only yields a
  grain multiple when the merge divides it evenly (720 windows at merge 7 → 103 bins → 25 165 s
  ≠ 7 h). **Raw archives** keep the probe + clamp-down (a request finer than the data
  only yields sparse bins). **Time-range archives** with a declared `Period` take the grain route too
: the probe-derived width drifted with every data gap (one year of 15-minute windows, `limit` 365: 367 buckets of
  85,929 s instead of 365 of 86,400 s); a request finer than the window length is clamped to one window.
  A `Period` is only a window length (windows may sit at any phase, e.g. 00:05–00:20), so the time-range origin does not
  snap to the grain like a rollup's: one probe over the same source filters (`CompileWindowPhaseProbeQuery`:
  `MIN`/`MAX` of `window_start` in epoch ms modulo the `Period`) finds the phase of the stored windows. When all windows
  in range share one phase, `From` is snapped down to the last instant `≡ window_start (mod Period)`. No
  window in range, series at different phases (no single origin keeps them all) or a failing probe keep `From`. **The probe
  is bounded and remembered:** it runs once outside the resilience pipeline (`QueryRowsOnceAsync`, no retry),
  the query waits for it at most `StreamData:DownsamplingPhaseProbeTimeout` (5 s), and a single phase is remembered per
  table, window length and filter set (`WindowPhaseCache` in `RecomputeProcessState`,
  `StreamData:DownsamplingPhaseProbeCacheDuration`, 5 min, 0 = off; single flight per key, a probe the query stopped
  waiting for runs on for at most 30 s and fills the cache; a query of another range takes only a single phase from it and checks it like a remembered one;
  forgotten on table drop/activation and on an in-process write (ingest or import) of another phase, also while a probe
  runs; at one window per bin an empty result evicts the phase and re-probes once). Never put the probe back
  under `GetDataAsync`: ten parallel chart queries with retried probes exhausted the pool of 32 of a large tenant. Before the bin edges followed the stored phase the raw
  `From` was the origin: a relative "last 2 days" request (From 18:37:42) put every bin edge inside a stored 15-minute
  window and the §7 predicate dropped all of them (a test lab: 0/192 bins with a value, resolution-aware charts empty until
  the resolver switched to a rollup).
  Only a time-range archive without a `Period` still uses the probe route.
  The grain route snaps the origin itself (rollups on the grain, time-range archives in their window phase); callers
  need not pre-align `From` — the line-chart widget no longer does. The probe route (raw archives,
  time-range archives without a `Period`) still bins from `From`.
  `DownsamplingBinQuantizer` is a pure, unit-tested helper (`DownsamplingBinQuantizerTests`).
- **Generation filter:** `AppendDownsamplingSourceFilters` also emits the
  active-generation predicate when `GenerationTracked` — previously the downsampling path missed
  it entirely, so a read during a recompute double-counted the swapped windows.

### Downsampling into local-day bins

**Behaviour change.** Downsampling queries take
`TimeZone`, `BinOrigin` and `BinInterval` (engine `StreamDataDownsamplingQueryOptions`) and return one bin
per local day (23 / 24 / 25 h across DST changes) from raw, time-range and rollup archives. **Behaviour change for every
downsampling query: a COUNT column of an empty bin reads `0` instead of `null`** (a count over nothing is 0; Studio charts
of COUNT series show 0 instead of a gap). Other aggregations of an empty bin stay `null`. Without the three options the
compiled SQL is unchanged (`DownsamplingSqlSnapshotTests`, recorded on main 6966ce9, after).

Rules to keep:

- **One axis, from the engine.** `DownsamplingBinAxis.Resolve` validates the options and `DownsamplingBinAxis.Build`
  builds the bin starts with `BucketBoundary` on a `BucketCalendar` (CalendarDay, zone, day start = `BinOrigin`), the
  primitive of the day rollups, so raw bins and gas-day rollup buckets coincide. No second primitive here; the end of the
  last bin is `NextBucketEnd(start, calendar)` × interval (`AdvanceLocalDays`). Invalid options (unknown zone, origin
  outside `[00:00, 24:00)` or not a whole minute, interval not whole days, more than `MaxBins` = 10,000 bins, no From/To)
  throw the engine's `DownsamplingBinOptionsInvalidException` (a contracts `StreamDataException`, structured 400) before
  any SQL.
- **`Limit` does not decide the bin count** for local-day bins and is not required;
  From and To stay required. Without the options From, To and Limit are required as before.
- **Every bin is a whole local day** (review of PR #226, decided 2026-11-04). The source range is the axis,
  `[binStarts[0], end of the last bin)`, not the requested From/To: a `From` inside a day gives a first bin that counts
  that whole day (hourly raw, gas day 06:00 Vienna, From 2026-03-27T00:00Z: first bin 2026-03-26T05:00Z, Count 24, not 5),
  and a `To` inside a day gives a last bin that counts that whole day, as the grain route reads from its snapped origin.
  `CrateQueryBuilder.WithDownsamplingByLocalDays(binStarts, axisEnd)` sets From/To from the axis itself.
- **SQL bin key = one `DATE_BIN` per run of equal-width bins** (`LocalDayBinRuns`, `CrateQueryBuilder.WithDownsamplingByLocalDays`,
  `CrateQueryCompiler.LocalDayBinExpression`): `CASE WHEN t < run1End THEN DATE_BIN(w1, t, run1Start) … ELSE DATE_BIN(wN, t, runNStart) END`;
  a single run (UTC, no DST change in range) is a plain `DATE_BIN`. A year in Vienna has five runs. Still one grouped scan,
  O(source rows), no `generate_series` join; the keys equal the C# axis to the instant. Raw rows key on
  `timestamp` and get `AND timestamp < <end of last bin>` (the raw filter's inclusive `<= To` would otherwise put the
  first instant of the next day on no bin); windowed rows key on `window_start` and keep the §7 containment against the
  bin end of their run.
- **Sources.** Raw, time-range and fixed-size rollups are binned by key/containment. **A source window that straddles a
  local-day boundary refuses the query** (review of PR #226): before the downsampling SQL, a time-range archive or a
  fixed-size rollup gets one probe (`CompileLocalDayStraddlingWindowCountQuery`: same source filters, `window_end` beyond
  the bin end of its `window_start`); any such window throws `DownsamplingBinOptionsInvalidException` naming the count,
  because §7 would drop it and its bin would read Count 0 (or too little) as if measured. Raw archives and a matching
  CalendarDay rollup need no probe. The caller picks the archive (this repository has no rung fallback); the message
  points to the raw archive or a CalendarDay rollup with the same zone and day start. A calendar rollup must cut the same
  days — `CalendarDay`, same zone rules, same day start — otherwise it is refused (`RejectCalendarRollupUnderLocalDayBins`):
  every stored window would straddle a bin and the chart would read empty.
- **Rows carry the axis instant** (`DateTimeKind.Utc`), not the `DATE_BIN` value (a timestamp without zone).
- **Per series** (`GroupByColumnPaths`, e.g. `rtId`): every series found in the result gets a row in every bin
  (`FillLocalDayBinsPerSeries`); a day without values of one series reads Count 0 with that series' rtId. A series without
  any value in the whole range is unknown and gets no rows; with no data at all there is one empty row per bin. The
  fixed-width path keeps its single empty row per bin (unchanged).
- **Row cap per query: `CrateDbStreamDataRepository.MaxLocalDaySeriesRows` = 100,000** (bins x series of the per-series
  fill; review of PR #226). Checked after the SQL, before any row is built; above it the query is refused with
  `DownsamplingBinOptionsInvalidException` naming bins, series and the cap. The bin count alone stays bounded by the
  engine's `DownsamplingBinAxis.MaxBins` (10,000).
- SQL literals of the local-day path (`UtcLiteral`) normalise with `ToUniversalTime()` like the source filter, so a bin
  origin and a range bound of one instant render the same text.
- Tests: `LocalDayBinRunsTests`, `LocalDayDownsamplingQueryTests`, `DownsamplingSqlSnapshotTests` (unit);
  `StreamDataDownsampling/LocalDayDownsamplingIntegrationTests` and
  `GasDayRollupIntegrationTests.LocalDayDownsampling_OverTheOffsetRollup_…` (real CrateDB 6.4.6, also green on 5.10.10):
  gas days 24/23/24 and 24/25/24 from hourly raw data, per rtId with a missing day, an empty day (Count 0, Sum null),
  Vienna calendar days from a year of 15-minute time-range data per rtId (92 on 2026-03-29, 100 on 2026-10-25, 96 otherwise),
  a gas-day rollup with a deleted day; review of PR #226: whole gas days for a `From`/`To` inside a day (24/24/23/24), a
  time-range archive with windows at half past refused under 06:00 gas days and answered under 06:30 days. Performance (`Category=Performance`, not in the CI): one year of hourly data for
  50 series (438,000 rows), gas days per rtId: median 237 ms (6.4.6) / 260 ms (5.10.10) on a local Testcontainer, budget 1 s.

### Native Copy, Sweep Grace Period, Rollup Validate/Repair, Restore Reconcile Marker

The full text is `docs/streamdata-archive-concept.md` §18a and, for the sweep, `octo-construction-kit-engine/docs/concept-rollup-recompute.md`
("Sweep grace period"); this is what to keep in mind when touching the code.

- **Native copy** (`Runtime.Engine.CrateDb/Copy/`): `COPY … TO DIRECTORY` / `COPY … FROM` per counted time slice
  (`StagingCopyRunner`), the storage credential only inside `CopyStatement` (never log `Reveal()`). A rollup's generation
  map is the last slice of a plan: a dump copies it first and the data after it, a load the data first and the map last. The
  repeat rule is `RollupArchiveDumper` (read map, copy, read map; repeat up to `MaxMapAttempts`, then
  `GenerationMapKeptMoving`). The plan in a dump result carries the rows the copy *reported*, not the counted rows. A dump leaves out the Hidden columns and the leftovers of computed columns (`ArchiveColumnOwnership`); the load refuses a dumped column the table lacks (`streamdata-write-path.md`, "Backups leave the leftovers of computed columns out").
- **Slice retry and disk headroom**: `CopySliceFromAsync` loads a slice up to 3 times; only a transient failure
  (`CopyFailureClassifier`: circuit breaker, rejected execution, node/shard not available, statement timeout, a `RETURN SUMMARY` that is short with
  `error_count = 0`, rejected rows that all carry such a text) is repeated, after 30 s and 60 s, with `overwrite_duplicates = true` and half the
  `bulk_size` (5000, 2500) of the attempt before; after a repeat the slice's range is counted and must equal the manifest. Policy constants
  stay in the runner. The disk check (`StagingDiskHeadroomCheck`) works against the room **below the high disk watermark**, with the replicas the
  table gets back (`min(1 + replicas, nodes)` copies) and `DiskSpaceFactor` 12; it runs per archive and, through
  `IStreamDataRepositoryFactory.CheckStagingDiskHeadroomAsync`, for the whole backup before the MongoDB restore. Details: `docs/streamdata-archive-concept.md` §18a.
- **Sweep grace period** (`StreamData:Recompute:SweepGracePeriod`, default 2 h, 0 = at once): a recompute no longer sweeps
  or removes contained pointer entries at the flip; it writes a row into `archive_<rtId>__sweep`
  (`PendingSweepSqlBuilder`, every column explicit) and `SweepDueAsync` executes the due ones (at the start of a
  recompute of that rollup and from the host's recompute tick via `SweepSupersededGenerationsAsync`). The sweep deletes rows
  strictly *below* the entry's generation and drops the record of an entry that was replaced. The once-per-process reclaim
  pass skips entries with a record. A repository/executor built without options keeps the immediate sweep.
- **NotChecked**: a rollup the validator cannot look at gets `RollupConsistencyEntry.NotChecked` (`NoLiveTable`,
  `NeverActivated`, `SourceMissing`, `Disabled` + reason, `IsBlocking`) and is not ok unless it is `NeverActivated` or `Disabled` (status
  Disabled, no table: legacy rollups disabled in the source kept the marker of every restore; informational: a rollup without
  data cannot be inconsistent, and blocking would stop the whole tenant's rollups for good; a failed restore load keeps the marker through
  the job's `ArchiveFailed` entry); `RollupConsistencyReport.IsOk` is false
  while any entry blocks, the repair skips a rollup without a table, and the marker is not cleared (`RollupRepairer`: also not after a repair of some rollups unless the
  whole tenant validates ok). Tests: `RollupConsistencyValidatorTests`, `RollupRepairerTests`, `RollupReconcileIntegrationTests`.
- **Validate / repair** (`Reconcile/`): `RollupConsistencyValidator` is read only (counts, MIN/MAX); the checks work on the
  *governed* part of a pointer entry (the entry minus the ranges of higher generations). `RollupRepairer` order: create map,
  delete orphans, recompute gaps (own executor, immediate sweep, no gate), rewind watermark (decided again when it runs).
- **Restore reconcile marker** (`RestoreReconcileMarker`, table `__restore_reconcile`, row keyed by the full tenant id; no
  row = not pending): the hosted services skip a marked tenant (`RestoreReconcileGate`), `AggregateBucketAsync` and the
  executor refuse with `RestoreReconcilePendingException`, the sweep returns 0; only the repair runs and clears it.

### mongorestore Batch Size

`MongoToolArguments.BuildMongoRestoreArguments` always passes `--batchSize`: `MongoRestoreOptions.BatchSize` when set, else
`OctoSystemConfiguration.MongoRestoreBatchSize` (default **100**, valid 1..1000, env `OCTO_System__MongoRestoreBatchSize`; an
invalid value throws `ConfigurationErrorException` naming the option when the configuration is bound, an invalid per-call value
throws `ArgumentOutOfRangeException`). Without it the tool's 1000 documents per batch can end a message over the server's
48,000,000 bytes (`maxMessageSizeBytes`) once documents average above ~48 KB: the restore of a collection of 864,070 EDA messages
(73 KB average) died with `msgLen 48000084 > 48000000` after 250,447 documents (mongorestore 100.16.0, MongoDB 8.0.12; mongo-tools
TOOLS-4142, TOOLS-4145, TOOLS-2875 describe the byte rule overshooting). 100 documents keep a batch below the limit for typical documents
(averages up to ~480 KB) - it reduces the risk, it does not exclude it: the count bounds the number of documents, not their size, so a few
documents near 16 MiB can still exceed the limit; the rationale is in the xml doc of the property. Restore requirement: lower the value
when a collection averages above ~480 KB or holds very large documents. Test: `LargeDocumentRestoreTests` (2400 documents averaging above 70 KB through the real dump and restore; not red without the
fix against the local MongoDB container, see the comment in the test), `MongoToolArgumentsTests`.

### Archive Export — Time Slices, Not Keyset Pages

`CrateDbStreamDataRepository.ExportRowsAsync` is the scan behind a tenant dump with archive data
and behind `export-archive-data`. It reads an archive as **consecutive time slices, one statement
per slice**:

```sql
SELECT MIN("window_start") AS "export_min", MAX("window_start") AS "export_max", COUNT(*) AS "export_count" FROM <table>;
-- then, per slice proposed by ExportSlicePlanner:
SELECT COUNT(*) FROM <table> WHERE "window_start" >= '<from>' AND "window_start" < '<to>';
SELECT * FROM <table> WHERE "window_start" >= '<from>' AND "window_start" < '<to>'
ORDER BY "window_start", "rtid", "cktypeid", "window_end"[, "generation"];
```

(`timestamp`, `rtid`, `cktypeid` for a raw archive; `generation` on a rollup.) The slices ascend
without gap or overlap and each is ordered by (time, rtid, cktypeid) and then by the rest of the
table's primary key, so the order is total and the rows come out in exactly the order a single
ordered scan would give — the contract of `IStreamDataRepository.ExportRowsAsync` is unchanged.
The last slice is bounded by the last row itself (`<=`): the exclusive end one millisecond behind
it need not exist as a `DateTime`.

The keyset cursor it replaces compared (time, rtid, cktypeid) only. Rows that share those three —
two windows starting at the same instant, or two generations of a rollup window between the
pointer flip of a recompute and its sweep — were skipped when a page boundary fell between them.
A slice is cut by time alone, so it cannot split such rows.

**Why not keyset pages.** Until then the scan paged over the whole table: `ORDER BY key LIMIT 5000`
behind `key > cursor`. Such a page is a top-n search over **every row behind the cursor**, so its
cost follows the size of the table, not the size of the page. Measured through the driver:

| Table | Old: per page of 5,000 rows | Old: rows a second | New: rows a second |
|---|---|---|---|
| 739,000 rows (local) | 0.02 s | 209,000 | 469,000 |
| 115 million rows (local, single node), first million rows | 1.0 s | 4,989 | 199,000 |
| 115 million rows (three nodes, production) | 3.6 s | 1,381 | not run |

The whole 115 million row table, written as compressed NDJSON the way the dump job does it, took
9 min 45 s with the new scan (197,000 rows a second; 469 slices read, 477 counted). On three
tables the old and the new scan were compared row by row through the driver against a real
CrateDB — a time-range archive, a rollup with its generation column and a raw archive with
irregular timestamps, whole and with a window: same rows, same order.

The production figure is a tenant dump that had read 7.2 of 115.3 million rows after 3 h 15 min
and would have run for about another day. The same dump of tenants with 400,000 to 900,000 rows
took 22 to 49 seconds, which is why the defect stayed unseen.

Rules to keep when touching the scan:

- **Never page a large archive with `LIMIT` behind a cursor.** Bound the statement on both sides of
  the time axis instead. `BackfillComputedColumn` still pages that way (`BackfillPageSize`, keyset
  over the key columns) and has the same cost on a large archive.
- **The order names the whole primary key.** Leave a key column out and rows that tie on the
  rest come out in an arbitrary order.
- **A slice is counted before it is read.** `ExportSlicePlanner` proposes, the scan counts, the
  planner accepts or refuses and proposes narrower. Sizing the next slice from the last one alone
  is not enough: an archive that is sparse for a year and dense afterwards (a back-fill) would get
  months of the dense part in one statement. A range count is answered from the index; it costs
  one cheap statement per 250,000 rows.
- **No row buffering per statement.** `StreamRawRowsAsync` hands rows off the open reader, so the
  slice size (`ExportSliceTargetRows`, at most twice that) bounds the length of a statement, not
  memory. Do not replace it with a buffered read.
- **The bounds are read once.** Rows written behind the last instant while the export runs are not
  part of it. The export never was a snapshot of a table that is being written; export a disabled
  archive, or accept that.

Leaving the enumeration early disposes the open reader, and the driver then cancels the statement
(`XX000: Job killed`); cancel through the token instead of breaking out of the loop.
