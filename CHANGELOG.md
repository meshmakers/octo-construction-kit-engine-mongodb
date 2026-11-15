# Changelog

## Unreleased

- AB#8947 Fix: resolving a CK model outside a transaction no longer queries the lock collection (`SysLock`) on every
  call. Since AB#8677 `DatabaseCkModelRepository.IsExistingAsync` asked for the running model imports first, and it
  runs on every tenant lookup of every request (`SystemContext.TryFindTenantContextAsync` ->
  `IsSystemTenantExistingAsync` -> `IsCkModelSatisfiedAsync`): one extra MongoDB `find` per request in every service
  (test-2: ~2 M generated spans per day). The held import locks now come from `CkModelImportLockCache`: one answer per
  tenant database and process, reused for at most 2 s, shared by concurrent readers (one query in flight), and dropped
  at once when this process acquires or releases a model import lock. When the resolve finds no matching version, it
  asks the lock collection directly before it believes the miss, and waits for a running import of the model as before
  (AB#8677). So a cached answer that predates an import in another process, or by a service on an older package
  during a rolling deployment, never turns an in-place upgrade into "no Available version"; the importer itself does
  not wait. Bound: at most one `SysLock` read per tenant database, process and 2 s for resolves that find their model
  (at most 43,200 per day and pod per busy tenant database, far less with normal traffic), plus one per resolve that
  finds none. Unchanged since AB#8677: a reader that found the Available row just before an import took the lock can
  still read elements while they are replaced (the cache load retry or the caller's retry applies). The wait loop, the
  CK cache load retry and the import's failure classification still read the lock collection directly. No interface
  change.
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
