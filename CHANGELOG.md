# Changelog

## Unreleased

- AB#8897 New: a runtime-entity query that runs too long is stopped by MongoDB (`maxTimeMS`) and ends with
  `RuntimeQueryTimeoutException` (tenant, CK type, limit, elapsed time) instead of keeping the database and the service
  busy. The limit applies to each MongoDB operation of a query (page and count of a paged query each get the full
  limit). **Opt-in per host:** `AddRuntimeQueryTimeLimit()` on the runtime engine builder; the host binds
  `RuntimeQueryOptions` (section `RuntimeQuery`, `MaxTimeMs`, default 120,000 ms, 0 = off). Without the call nothing
  changes. Covered: the queries by type, by id, by type with navigation and by CK interface, and their counts
  (`SingleOriginQuery`, `RtInterfaceQueryExecutor`). Not covered yet: association and deep-graph queries
  (`MultipleOrigin*`). A query that completes after more than 80 % of its limit is logged as a warning (tenant, CK type,
  duration, limit). Batch paths override the limit at the call site: `using (RuntimeQueryTimeLimit.Override(TimeSpan))`
  or `using (RuntimeQueryTimeLimit.Disable())`, an `AsyncLocal` scope that holds across `await` and for the tasks started
  inside it and falls back to the previous limit on dispose; the CK migrations of `TenantContext` and the blueprint of a
  new child tenant run inside `Disable()`. Tests: `RuntimeQueryTimeLimitTests` (unit),
  `RuntimeQueryTimeLimitIntegrationTests` and `InterfaceQueryTimeLimitTests` (real MongoDB, including a query MongoDB
  stops with `MaxTimeMSExpired`).
- AB#9342 Fix: two CK types of one collection with an Ascending index on the same attributes share one index. Both
  produce the key spec `{ckTypeId, attr..., _id, rtState}` and MongoDB holds one non-unique index per key spec, so the
  index update of the second type failed with MongoDB 85 ("Index already exists with a different name"), logged a warning
  and stored a `Failed` index state on every update; on a collection with one legacy index shared by both types the
  normal update built the tiebreak index of one type next to the legacy index of the other, which the tiebreak upgrade
  (AB#8960) could then never replace (`UpgradeTiebreakIndexesAsync` left the legacy index and ended incomplete, seen on
  test-2 `octosystem`). The index update now dedupes per collection on the key spec: the index that holds it (existing,
  or built earlier in the same collection run, also a legacy index kept without the upgrade) serves every type with
  those keys, whatever the order of the types - no createIndexes call, state `Applied` under the shared index name, one
  information line per type. On a collection that holds both the legacy index of one type and the tiebreak index of the
  other, an update without the upgrade now drops the legacy index after the other indexes (the tiebreak index serves
  both types, nothing is built), as it already did for a single type whose tiebreak index exists. Unique and UniqueNotDeleted indexes keep their per-type filter, text
  indexes are merged per collection as before; an index with foreign options (sparse, hidden) is still kept and
  reported. Existing tenants converge on the next index update or upgrade; no CK model, index shape or name changes.
  Side fix: a removed index state (an index replaced, dropped or no longer serving the type) is now removed from the
  stored states of the CK type too, not only from the states tracked in the run, so a `Failed` state an earlier run left
  disappears once the type is served; an index the run recorded as `Applied` for the type keeps that state even when a
  later definition of the type expected another index under its name (two definitions whose existing indexes hold each
  other's keys). Tests: `SharedIndexKeySpecTests`.
- AB#9250 Test: the integration test containers get a CPU limit next to their memory limit (mongod 4 CPUs, CrateDB 2 CPUs
  each; the CrateDB heap is set explicitly for every CrateDB fixture), xUnit runs at most 8 collections at once
  (`maxParallelThreads: 8`), and the CrateDB pool tests wait for their container and name it when it does not answer instead
  of failing with a bare `BrokenCircuitException`. Test project only; the test README describes the local run on Windows
  (Docker Desktop VM of at least 20 GiB / 12 CPUs).
- Fix: a restore or a backup with detach that throws after its drop or detach was committed and whose tenant-deleted
  notification failed now keeps the warning ("... run ClearCache for the tenant") in `CommandResult.Output` of its failure
  result. Before, the failure result of the throw carried only the exception message and only the WARN log named the
  recovery. A throw without a failed notification gives the same failure result as before.
- Fix: the tenant-deleted notification (`PosDeleteTenant`) of a child-tenant drop or detach is published only after the
  removal is committed. `DetachChildTenantAsync` and `DropChildTenantAsync` published it inside the caller's transaction,
  so services could tear down a tenant whose removal was later rolled back, or act before the removal was visible. Inside
  a caller's transaction it now runs as an after-commit callback (nothing on a rollback), with the same three bounded
  attempts as the tenant-created notification and then a warning naming the tenant and the recovery (ClearCache for the
  tenant, or a service restart); without a transaction it is published at once as before, and the two-phase delete
  (`DropTenantDatabaseAsync` after the committed metadata delete) is unchanged. The backup's detach and the restore's
  drop of an existing tenant publish it themselves after their commit; a failed publish keeps them successful and adds
  the warning ("... run ClearCache for the tenant") to `CommandResult.Output`. A clear in a transaction now publishes
  delete, create, update after the commit. The internal `TenantCreatedNotificationScope` is now `TenantNotificationScope`
  and collects both notifications. Details: `CLAUDE.md`, "Tenant-deleted notification after the commit".
- AB#9177 Fix: the tenant-created notification (`PosCreateTenant`) of a child-tenant create or attach is published only
  after the registration is committed. `CreateChildTenantAsync` and `AttachChildTenantAsync` published it inside the
  caller's transaction, so every service set the tenant up before it was registered (a restore logged "Setup tenant
  failed" on every service, recovered later by the setup retry). Inside a caller's transaction the notification now runs
  as an after-commit callback (nothing on a rollback), with three attempts (250 ms, 1 s apart) and then a warning naming
  the tenant and the recovery (ClearCache for the tenant, or a service restart); without a transaction it is published at
  once as before. `ClearChildTenantAsync` inside a transaction publishes its `PosUpdateTenant` when the transaction ends,
  after the tenant-created notification. The restore (`RestoreTenantAsync` with attach) publishes it itself after its commit
  (`TenantCreatedNotificationScope`); a failed publish keeps the restore successful (the data is restored and attached) and
  is reported on the result: new `CommandResult.TenantCreatedNotificationFailed`, the warning ("... run ClearCache") in
  `Output`, a WARN log; `CloneTenantToTempAsync` passes the flag on. Every publish attempt after a commit, the restore's
  included, is bounded to 5 s. A tenant the same transaction detached or dropped again gets no tenant-created
  notification (recorded per session, no registry read needed). Details: `CLAUDE.md`, "Tenant-created notification after the commit".
- AB#9127 Fix: a tenant dump (`TenantBackupService.BackupTenantAsync`, used by the bot's dump job, the pre-sweep dump and
  the clone to a temp tenant) leaves out platform-only collections, today `RtEntity_SystemCommunicationSecretDefault`
  (`MongoDumpOptions.PlatformOnlyTenantCollections`, `ForTenantArchive`, one `--excludeCollection` each). The secret
  defaults the communication controller projects into a tenant are platform-only and must never reach a file a tenant
  identity can read; the controller projects them again after a restore (AB#7245). `MongoDumpOptions.ExcludeCollections`
  is new and empty by default, so `ForArchive` and every other dump are unchanged.
- AB#9138 Fix: a deep sorted page of a paged runtime-entity query no longer spends its time in MongoDB's plan trial.
  Since AB#8896 the page (`$match`, `$sort`, `$skip`, `$limit`) is planned as a whole; for a deep page none of the
  candidate plans returns results early, so every request ran all candidates through the trial (909,000 entities, skip
  50,000: 56-67 s, execution ~90 ms) and the plan cache never kept the winner. The page read is now hinted to the index
  that provides its sort (`SortIndexHint`: `ckTypeId` followed by the sort keys, same or reversed directions; never a
  partial, sparse, hidden, collated, text, hashed or 2dsphere index), from skip `PagingIndexHintMinSkip` on (**new
  setting**, default 1000). When the filter has an index of its own the page is hinted only with at least
  `PagingIndexHintMinMatches` (**new setting**, default 50,000) matches, counted exactly when the total is asked for (the
  count then runs before the page and is not repeated) or with a capped count. No hint inside a transaction, for
  unsorted pages, for pipelines with stages around the match (`$geoNear`, association filters) and when no index
  provides the sort. The indexes come from `CollectionIndexCache` (one `listIndexes` per collection and process every 5
  minutes, invalidated by this process's index updates); a hint naming a dropped index is retried once without it. New
  `IMongoDbDataSourceCollection.Aggregate` overload with `AggregateOptions`. Measurements: `CLAUDE.md`, "Deep sorted
  pages: the index hint".
- AB#9110 Fix: paged stream-data queries return every row exactly once, also without a sort order. The simple query
  (`ExecuteQueryAsync`, transient and persistent `rows`) ran LIMIT/OFFSET without ORDER BY when no sort order was given
  (`AddOrderByTiebreaker` did nothing without an existing ORDER BY), and with a sort order only `timestamp` broke ties.
  CrateDB returns the rows of a sharded table in any order per call, so the pages of a multi-node cluster repeated some
  rows and skipped others (FH VSCT, 3.5.71: 103-122 of 192 rows unique). `CrateQueryBuilder.EnsureDeterministicOrder`
  (replaces `AddOrderByTiebreaker`) appends the row key, ascending, to every page query: raw archives `timestamp, rtid,
  cktypeid`, windowed archives `window_start, window_end, rtid, cktypeid`. Without a sort order that is the default order
  (time axis, then rtId); with one, the key columns the caller did not name break its ties, in the caller's direction
  where the caller named them. The grouped aggregation (query builder and LOCF statement) now orders by its group-by
  columns; the GraphQL connection pages its groups by running it again per page. ORDER BY renders a column the
  way GROUP BY does (`QueryVariable.ToOrderByString`), so `row_origin` / `manual_count` are ordered by their
  `COALESCE(...)` expression, not by the bare name. Downsampling already ordered by bin,
  then series. **Behaviour change:** rows that had no defined order now come in the default order. **Cost:** measured on
  2,880,000 rows (3 shards, single-node test container, median of 5): first page 7 -> 17 ms unfiltered, 9 -> 8 ms for one
  day; deep page 28 -> 2650 ms unfiltered (offset 2,879,900), 10 -> 57 ms for one day (offset 95,900). Accepted by Odin
  (correctness first). Tests: `CrateQueryBuilderTests` (`EnsureDeterministicOrder_*`), `TimeWeightedQuerySqlBuilderTests`,
  `StreamDataPaging/StreamDataPagingIntegrationTests` (real CrateDB, 4 shards, 8 anchors on the same timestamps / windows,
  pages of 50 three times: raw and time-range, without sort order and with a sort on an all-ties column, full key order
  checked over all pages; grouped aggregation on both, and the LOCF path with TimeWeightedAverage on the raw archive),
  measurement `StreamDataPaging/StreamDataPagingOrderCostMeasurement` (`OCTO_PAGING_COST_DAYS`).
- AB#8960 Fix: paging through runtime entities returns every entity exactly once, also without a sort or with a
  sort key that is not unique. Before, `$skip`/`$limit` ran over an undefined order of the ties: 300 entities with the
  same `Name`, paged by 7, came back as 300 items of which only 35 were distinct. The page pipeline of
  `SingleOriginQuery` (and the enrichment read of the page) ends its `$sort` with `_id` in the direction of the last
  sort key unless the caller's sort already contains `rtId`; without a caller sort it sorts by `_id` ascending (a
  `$geoNear` or a text search without a caller sort keeps its own order). **Behaviour change:** an unsorted paged query
  is ordered by rtId, ties of a sorted paged query by rtId.
  **Index shape (new):** CK Ascending indexes are created as `{ckTypeId, attributes..., _id, rtState}` under the name
  `<Type>_<n>_id`, so that they provide the order of a page; unique, UniqueNotDeleted, text and system-level indexes are
  unchanged. Existing indexes in the legacy shape `<Type>_<n>` are **kept** by CK imports and `UpdateIndexesAsync`;
  the new `ITenantContext.UpgradeTiebreakIndexesAsync` (default interface method) rebuilds them per tenant - the new
  index is built before the legacy one is dropped, idempotent. `PagingTiebreakGate` leaves out the tiebreak only for a
  page sorted by keys whose order a legacy Ascending index provides completely, or that start with all keys of a CK
  Unique index (the tiebreak would read every matching document per page: measured 2 ms to 113 ms for the first page over 100,000 entities); it decides
  from the collection's indexes, read through the `CollectionIndexCache` of AB#9138 (5 minutes, one invalidation); a deep
  sorted page with the tiebreak is hinted only to an index that provides `_id` after the sort keys. Unsorted pages and pages sorted by other attributes
  get the tiebreak right after the deploy, also on existing tenants. Unpaged queries, the association navigations and
  the cached navigation path are unchanged; interface queries already broke ties by rtId (AB#7019). Rules and
  measurements: `CLAUDE.md`, "Page order of runtime queries".
  **Index updates without a gap (AC 8, AC 13):** an index whose definition changed is no longer dropped before the new
  one is built. When a CK update gives an index slot other keys, its expected name is still held by the old index
  (MongoDB can neither rename an index nor build one with other keys under a taken name); the new index is built under
  `<expected name>_<6 hex digits>` (the digits from the SHA-256 of the definition, so a repeated update finds the same
  name), and the old one is dropped at the end of the collection, after the build. If the build fails, the old index
  stays (also when it is the slot's index under its alternate name). A text index is still dropped before its
  replacement is built (a collection holds only one), whatever its name. An index with the right definition under another name is kept as it is (it used to be dropped and rebuilt
  under the expected name); definitions compare their keys in order (several field groups are one key list), and a
  Unique or UniqueNotDeleted index must name exactly the definition's types in its partial filter - after a descendant
  was added it is replaced like an index with other keys. **Pre-existing:** a text index never matched its definition
  either (the default language `en` was compared with a missing one) and was dropped and rebuilt on every import. A legacy index leaves the tiebreak out only when it provides
  the complete caller sort. An index with options the engine never sets
  (collation, sparse, hidden, TTL, a partial filter on a non-unique index, a unique index without exactly the engine's
  type and state filter) is never taken for a CK index; if it blocks
  our build, it stays (warning, ours tracked as failed). **Pre-existing bug fixed:** `GetIndexListAsync` read every
  partial unique index as UniqueNotDeleted, so a CK Unique index never matched its definition and was dropped and rebuilt
  on every CK import; UniqueNotDeleted is now only an index whose partial filter has a condition on `rtState`. The paging gate counts
  an Ascending index as legacy only when it starts with `ckTypeId` (an engine-made index), so a hand-made index whose
  default name matches the CK name pattern gets the tiebreak. **Naming rule:** a CK index is `<Type>_<n>` / `<Type>_<n>_id`, or the same with
  `_<6 hex>` after an update of its slot; the name is not a contract, the paging gate and the tiebreak shape are
  decided from the keys (the gate only recognises CK indexes by the name pattern `..._<n>` with the optional hash).
- AB#8896 New: a paged runtime-entity query loads at most one page of documents. `SingleOriginQuery.ExecuteQuery`
  no longer puts count and page into one `$facet`, which pushed every matching document through the server and sorted
  the whole set in memory. A paged query now runs a page pipeline (`$match`, the association stages that navigation
  filters need, one `$sort`, `$skip`, `$limit`; the enrichment `$lookup`s afterwards on the page only) and a separate
  server-side count (the `$match` + `$count` pipeline of AB#8895, no document returned). Page and count are two reads
  on the caller's session, one after the other; outside a transaction they are not one snapshot. Items, order and
  total count stay the same (page and count parity tests per query shape and per data-permission caller). New
  additive overloads `ITenantRepository.GetRtEntitiesByTypeAsync` / `GetRtEntitiesGraphByTypeAsync` with
  `includeTotalCount`: `false` runs no count at all and returns a lower bound as `TotalCount` (skip + returned
  entities, 0 for an empty page). Exception: a paged graph query that uses the query result cache (Filter mode with
  navigation pairs) returns the exact total of the cached id list, also for an empty page beyond the cached ids.
  The existing overloads always count and are unchanged for every caller. A paged
  query with `take` 0 returns no entity without reading a page (it used to fail with `$limit` 0).
- AB#8993 Test (deferred review finding of #249, applied with AB#9016): `DownsamplingManyIdsPerformanceTests.CreateSeededArchiveAsync`
  drops the archive table when the seed or its `REFRESH TABLE` fails, so a failed seed leaves no partial multi-million-row
  table in the shared CrateDB container. A failing cleanup is reported and does not replace the seed's error.
- AB#9016 New: a late stream-data delivery refreshes the rollups only for the series it touched, and in place.
  `CrateDbStreamDataRepository` records the distinct rtIds of the late, in-reach automated rows on the dirty window. Above
  `StreamData:Recompute:MaxScopedRtIds` (**new setting**, default 200) it records none, which means every series.
  `MongoArchiveRecomputeStateStore` / `MongoRecomputeJobStore` persist the sets (System.StreamData 1.26.0).
  `CrateDbArchiveRecomputeExecutor.ExecuteScopedRefreshAsync` (the repository delegates to it) stages the buckets for those
  series only (`rtid IN (...)` in the aggregation, the expected-row count and the LOCF carry). It then upserts the staged
  rows into the live table at the generation the readers select for each (window, rtId)
  (`GenerationFilterSql.RenderActiveGeneration`, the same CASE as the read predicate). No new generation, no pointer flip,
  no generation sweep. A bucket row of the series whose source rows are gone is removed, as a full recompute removes it:
  rows at the active generation older than the refresh marker, the `rtchangeddatetime` of the oldest staged row. They are
  counted first and deleted only when there are any. A slice CrateDB stores short is written once more, then the refresh
  fails (AB#8680 rule; the check counts only rows newer than the marker). The recompute
  preparation is shared with `ExecuteAsync` (`PrepareRunAsync`); the full recompute is unchanged.
  `RecomputeLateDataOptions` (`LateDataDebounce` default 5 min, `MaxScopedRtIds` default 200) is bound from
  `StreamData:Recompute` in both registration paths. `TenantContext` hands it to the orchestrator. Needs the engine
  release of AB#9016: this package builds only against it (merge order, §2.4a: engine with or before this repository).
  Tests: `LateDeliveryScopeTests` (unit), `StreamDataLateDelivery/ScopedRefreshEquivalenceTests` (real CrateDB: scoped
  refresh and full recompute give the same reader view for every series on an hourly + daily ladder, TimeWeightedAvg
  carry bucket included; no pointer added).
- AB#8993 Test: performance budget for downsampling over many ids. `DownsamplingManyIdsPerformanceTests` (trait
  `Category=Performance`, runs only in the performance pipeline) seeds a time-range archive of 3500 entities with 15-minute
  windows into a CrateDB testcontainer and requires every downsampling call over 400 ids and 7 days to finish in under 5 s
  (limit 800 and 200, a relative window off the window grid, 10 parallel calls of 350 ids), with every bin complete.
  Measured locally: 734 ms cold, 22-58 ms warm, 10 parallel calls 409-455 ms each. The analysis behind it (prod-2 tenant
  `sbeg`, 20-65 s reported) found that the SQL shape is not the cost driver: the IN list, the `DATE_BIN` GROUP BY, the §7
  containment and the phase snap take ~50 ms and do not grow with the history outside the window. The SQL is unchanged. The
  resolver in the engine now prefers the hourly rollup for such windows (meshmakers/octo-construction-kit-engine#370,
  `MinRollupPoints`).
- AB#8947 Fix: resolving a CK model outside a transaction no longer queries the lock collection (`SysLock`) on every
  call. Since AB#8677 `DatabaseCkModelRepository.IsExistingAsync` asked for the running model imports first, and it
  runs on every tenant lookup of every request (`SystemContext.TryFindTenantContextAsync` ->
  `IsSystemTenantExistingAsync` -> `IsCkModelSatisfiedAsync`): one extra MongoDB `find` per request in every service
  (test-2: ~2 M generated spans per day). The held import locks now come from `CkModelImportLockCache`: one answer per
  tenant database and process, reused for at most 2 s, shared by concurrent readers (one query in flight, cancelled
  after 30 s), and dropped at once when this process acquires or releases a model import lock. When the resolve finds
  no matching version, it asks the lock collection directly, waits for a running import of the model as before
  (AB#8677), and reads the model once more (the import may also have finished in between). A cached answer that
  predates an import in another process, or by a service on an older package during a rolling deployment, therefore
  does not decide a miss; the importer itself does not wait. What remains is the race of AB#8677: the lock check and
  the model read are separate statements, so an import that starts between them can still make a reader see no
  Available version, or read elements while they are replaced (the cache load retry or the caller's retry applies).
  Bound: at most one `SysLock` read per tenant database, process and 2 s for resolves that find their model (at most
  43,200 per day and pod per busy tenant database, far less with normal traffic), plus one `SysLock` read and one more
  `CkModel` read per resolve that finds none. The wait loop, the CK cache load retry and the import's failure
  classification still read the lock collection directly. No interface change.
- AB#8992 Fix: the window-phase probe of a time-range downsampling query (AB#8962) no longer exhausts the tenant's
  CrateDB connection pool. On prod-2 (tenant sbeg) a MeshBoard line chart sent ~10 parallel downsampling queries on a
  time-range archive without rollup; every query ran the probe first under the resilience pipeline (retried, 30 s) and
  the pool of 32 ran dry. Now:
  - the probe runs once, outside the resilience pipeline (no retry, no circuit-breaker sample), through the new
    `IStreamDataDatabaseClient.QueryRowsOnceAsync` (default interface implementation for other implementers; none exists
    outside this repository); its wait for a pooled connection and its statement share one budget;
  - the query waits for the probe at most `StreamData:DownsamplingPhaseProbeTimeout` (**new setting**, default `5 s`),
    then logs one warning and goes on with the unsnapped axis starting at `From` (the AB#7659 behaviour);
  - a single phase is remembered per archive table, window length and filter set (ckTypeId, rtIds, field filters; the
    time bounds are not part of the key) for `StreamData:DownsamplingPhaseProbeCacheDuration` (**new setting**, default
    `00:05:00`, `0` switches the cache off). At most one probe runs per key; one its query stopped waiting for keeps
    running for at most 30 s (one pooled connection) and fills the cache for the next query. A query of another range
    takes only a single phase from a running probe (checked like a remembered phase, see below) and otherwise probes
    its own range after it. With the cache off
    every query probes and the probe ends with the wait;
  - the remembered phase ends when this process drops or (re)activates the archive's table and when it writes (ingest
    or import) a time-range window of another phase into it; a probe that answers after such a write is not
    remembered. With one window per bin, an empty result on a remembered phase (or one taken from another range's
    probe) evicts it, probes this range once more and runs the query again when the windows sit at another phase.
  Residual risk: the cache key has no time part, so a phase probed in one range is used for other ranges of the same
  filters. When a series' windows sit at different phases in different periods (for example after its delivery changed
  without a table drop), a query with several windows per bin (k > 1) can drop at most 1/k of that series' windows in
  the range whose phase differs. This can last longer than one cache duration, because a later probe of the other range
  can remember its phase again. At one window per bin (k = 1) such a query returns no row, and the empty-result check
  recovers it (evict, probe this range, run again). Not changed: the downsampling query and the bucket-count probe of raw archives still have no
  cancellation token (`IStreamDataRepository` is in the engine), so an abandoned request still finishes its main query.
- AB#8962 Fix: downsampling a time-range archive over a window that does not start on a stored window start (a
  relative "last N days" ending at now) returns the stored values again. The bin axis started at the raw `From`
  (AB#7659), so every 15-minute bin straddled two stored windows and the fully-contained predicate dropped all of them:
  resolution-aware line charts on raw time-range archives stayed empty until the resolver switched to a rollup
  (~9 days). The engine now probes the phase of the stored windows in range (`window_start` modulo the window length,
  `MIN`/`MAX` over the same source filters). When they share one phase, it snaps the origin down to the last window
  start at or before `From` (windows at :05/:20/... keep their phase). The first bin may start up to one window before
  `From` and the axis may have one bin more, as on rollups. Requests that already start on a window start, rollups and
  raw archives are unchanged. With no window in range, or with series stored at different phases, the axis starts at
  `From` as before; there, windows of a phase other than `From`'s that straddle a bin edge are still dropped. The fix
  adds one cheap aggregate query per time-range downsampling. The grain route also normalises `From`/`To` to UTC
  (`ToUniversalTime`, as the SQL literals do) before it computes the bin count.
- AB#8895 New: a runtime-entity query that asks only for the total count counts on the server without loading any
  document. `ITenantRepository.CountRtEntitiesByTypeAsync` (type query) and `CountRtEntitiesGraphByTypeAsync` (type
  query with navigation pairs, as persistent queries use) run `$match` - plus the geospatial stage and the association
  stages that navigation filters need - followed by `$count`; no sort, no `$facet`, no enrichment `$lookup`, no entity
  is materialised. The count equals the `TotalCount` of `GetRtEntitiesByTypeAsync` / `GetRtEntitiesGraphByTypeAsync`
  with the same options, data permissions of the caller included (count parity tests per query shape). Additive: two
  new interface members, implemented here; existing queries are unchanged. Before, an unpaged query that only needed
  the count loaded every matching document (ASSET1002 OUT_OF_MEMORY on large entities, AB#8793).
