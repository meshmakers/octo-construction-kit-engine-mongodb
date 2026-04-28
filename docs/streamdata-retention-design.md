# StreamData Retention & Downsampling — Design

**Status**: Design only (concept §8 T17). No implementation in this commit; the doc captures the
chosen direction so the follow-up implementation tasks land against a stable shape.

## 1. Why per-archive

Time-series data growth is unbounded; without retention every archive table grows linearly with
ingestion forever. Different archives have very different shelf lives — production telemetry may
need 30 days at full resolution; diagnostic streams might warrant 365 days; aggregation feeds may
need only 7 days raw plus permanent rollups. Per-archive policy is therefore the right
granularity (concept §3 already pegs CkArchive as the unit of configuration).

## 2. Policy shape

Two orthogonal knobs on `CkArchive`:

```yaml
ckType: CkArchive
  attributes:
    # ... existing attributes (targetCkTypeId, columns, status) ...
    - id: CkArchive.RetentionDuration
      valueType: TimeSpan
      isOptional: true             # null = retain forever (current default)
    - id: CkArchive.RollupRule
      valueType: Record
      valueCkRecordId: CkArchiveRollupRule
      isOptional: true             # null = no rollup

ckRecord: CkArchiveRollupRule
  attributes:
    - id: BinDuration       ; type: TimeSpan ; required: true   # e.g. 1h
    - id: AggregationFunction ; type: Enum    ; required: true   # avg | min | max | sum | count
    - id: AggregationColumns  ; type: StringArray ; required: true # paths to aggregate
```

- **Retention**: rows older than `now() - RetentionDuration` are eligible for deletion.
- **Rollup**: before deletion, raw rows in each `BinDuration` window may be replaced by a single
  aggregated row carrying the configured aggregations. Rollup is **optional**; a pure-retention
  archive simply deletes.

## 3. Storage strategy

CrateDB has native time-partitioned tables (`PARTITIONED BY DATE_TRUNC(...)`). Two execution
paths considered:

### 3a. CrateDB partitioning + DROP PARTITION
- DDL: `CREATE TABLE … PARTITIONED BY (DATE_TRUNC('day', timestamp))`.
- Retention: a scheduled job iterates `information_schema.table_partitions`, drops every
  partition whose `partition_ident` is older than `now() - RetentionDuration`.
- Cheap (DROP PARTITION is metadata-only) and atomic.
- **Chosen** for retention.

### 3b. Aggregated rollup tables
- Each archive with a rollup rule gets a sister table `{archive}_rollup_{binDuration}` with the
  same standard columns plus the aggregation result columns.
- Insert path is unchanged; the rollup runs in batch from the rollup job (3a's schedule).
- For each newly closed bin window, run `INSERT INTO rollup SELECT … GROUP BY DATE_TRUNC(bin, ts)`,
  then DROP the corresponding raw partition.
- Queries that span the rollup window read from the rollup table; queries inside the raw window
  read from the main archive. The query layer chooses the source based on the requested
  resolution; UI defaults to "best available".

### 3c. Rejected: external scheduler / DELETE WHERE
- DELETE on a CrateDB shard rewrites segments — much more expensive than DROP PARTITION.
- Adding a third process (cron, Hangfire) outside the engine increases operational surface for
  little gain; the engine already has IHostedService for periodic work.

## 4. Scheduler

A single `ArchiveRetentionScheduler : IHostedService` runs in the asset-repo process. Hourly
cadence (configurable). For each tenant with StreamData enabled:

1. Enumerate Activated archives via `ICkArchiveRuntimeStore.EnumerateAsync`.
2. For each archive with a non-null `RetentionDuration`:
    a. If `RollupRule` is set: produce rollup rows for any complete bins now outside the
       retention window but newer than the last-seen-rollup mark.
    b. Drop partitions older than the cutoff.
3. Record per-archive metrics (`streamdata.crate.retention_partitions_dropped`,
   `streamdata.crate.rollup_rows_inserted`).

Failures per archive are isolated; the scheduler logs and moves on (one bad archive must not
block the rest).

## 5. CkArchive lifecycle interaction

- Setting / changing the rollup rule is a schema change; the existing immutability rule
  (concept §10) blocks it once the archive leaves Created. Future relaxation: "rollup rule may
  change while no rollup table exists yet" — kept out of scope until a real customer needs it.
- Retention duration is a runtime knob with no physical schema impact; the existing
  immutability validator should explicitly **allow** changes to `RetentionDuration` even on
  Activated archives.
- Deletion of a CkArchive (via lifecycle service) drops the rollup sister table alongside the
  main table; idempotent `DROP TABLE IF EXISTS` keeps that safe.

## 6. Concept-doc hooks

The main concept doc (`streamdata-archive-concept.md`) already lists T17 as deferred under §17.
This file expands that into a concrete plan. The doc is the source of truth; updating §3 to
include the two new attributes happens in the implementation commit, not here.

## 7. Follow-up tasks (when implementation kicks off)

| Stage | What |
|---|---|
| 1 | Add `CkArchive.RetentionDuration` + `CkArchive.RollupRule` to the StreamData CK model (engine repo); breaking model migration drives downstream consumers. |
| 2 | Update the CkArchive immutability validator: `RetentionDuration` mutable on Activated; `RollupRule` immutable. |
| 3 | Update `ArchiveDdlGenerator` to emit `PARTITIONED BY DATE_TRUNC('day', timestamp)` on every archive table. |
| 4 | Implement rollup-DDL generator: builds the sister-table schema from `RollupRule.AggregationColumns`. |
| 5 | Implement `ArchiveRetentionScheduler` (IHostedService) with the per-tenant loop above. |
| 6 | Add per-archive query-routing override that points the read-path at the rollup table when the requested time window predates the retention boundary. |
| 7 | Refinery Studio: surface `RetentionDuration` and `RollupRule` in the archive create/edit form (T21 follow-up). |

## 8. Open questions

- **Rollup-source partition lifecycle**: drop the raw partition immediately after rollup or
  keep both for `gracePeriod`? — recommendation: keep for one bin width to allow
  reconciliation reads; configurable.
- **Rollup retention**: do rollup tables themselves have a retention duration? — yes, default
  to 10× the raw retention so the rollup table is the long-term archive; expose as
  `CkArchiveRollupRule.RollupRetentionDuration`.
- **Schema evolution of rollup columns**: covered by the `RollupRule` immutability rule above —
  add a column means new archive.
- **Cross-tenant scheduling**: with N tenants the hourly job touches a lot of partitions. Run
  per-tenant in parallel, capped at 4 concurrent tenants by default to bound CrateDB load.

## 9. Out of scope (future)

- User-defined aggregation functions beyond the five enum values.
- Hot/cold tiering (cold partitions to object storage).
- Streaming rollups (continuous aggregation triggered by every insert).
