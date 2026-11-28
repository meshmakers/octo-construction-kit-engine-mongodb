---
description: "StreamData write path: schema prefix, CrateDB pool, conflict precedence, row origin, write result, value checks, partial delivery"
applies_to: src/Runtime.Engine.CrateDb/**, src/Runtime.Engine.MongoDb/StreamData/**, tests/StreamData.UnitTests/**, src/Runtime.Contracts.MongoDb/Configuration/StreamDataInstanceConfiguration.cs, tests/Runtime.Engine.MongoDb.IntegrationTests/StreamDataWriteLoss/**
---

## StreamData: Archives and Rollups

### Schema Instance Prefix

`TenantSchema.SchemaName` derives the per-tenant CrateDB schema from the tenant id alone, so two
OctoMesh instances sharing one CrateDB cluster collide on identical tenant ids.
`StreamDataInstanceConfiguration.SchemaInstancePrefix` (optional, **default empty**, root
`StreamData` config section ⇒ uniform env var `OCTO_STREAMDATA__SCHEMAINSTANCEPREFIX` across all
services) prepends an instance prefix: `{prefix}_{tenant}`. Rules:

- **Empty prefix ⇒ byte-identical legacy naming** — pinned by
  `TenantSchemaInstancePrefixTests.SchemaName_WithoutPrefix_IsByteIdenticalToLegacyNaming`.
  Existing instances must never set it, or their tenants' schemas would move. Deliberately a
  separate setting, NOT derived from the RabbitMQ `instancePrefix` (an existing test environment already runs
  prefix `main` with un-prefixed CrateDB schemas).
- The prefix is cleaned (lowercase alphanumeric) and applied **process-wide, set-once**
  (`TenantSchema.SetInstancePrefix`, initialized from the bound instance options in the
  `CrateDatabaseClient` ctor — the singleton every CrateDB path flows through): schema naming is one-per-instance
  and threading a constant through every static SQL builder (genmap, recompute staging, DDL)
  would churn the whole surface. A conflicting second value throws at startup — two prefixes in
  one process would silently split a tenant's data across two schemas. A late empty value never
  clears a configured prefix.
- The `MaxSchemaLength` hash-suffix fallback keeps the prefix (plus separators) inside the
  budget; the hash stays over the cleaned tenant id (its job is per-tenant uniqueness).
- Genmap / recompute-staging side tables derive from the schema-qualified name and follow
  automatically.
- Tests mutating the process-wide prefix forced `[assembly: Parallelization(Mode = ParallelMode.None)]`
  on `StreamData.UnitTests` (pure-logic assembly, serialization costs ~nothing); the naming
  matrix itself tests the pure `SchemaName(tenantId, prefix)` core.

### CrateDB connection pool: failed statements give their connection back

Every CrateDB access leases its connection through `CrateDatabaseClient.LeaseConnectionAsync`; the pool is per tenant
(`StreamData:MaxPoolSize`, default 32, one `NpgsqlDataSource` per tenant in `CrateDbConnectionAccess`). The key
is bound into `StreamDataInstanceConfiguration.MaxPoolSize` (environment variable `OCTO_STREAMDATA__MAXPOOLSIZE`): a positive
value overrides the host's `StreamDataConfiguration.MaxPoolSize`, otherwise (unset, zero or negative) the host's value
(default 32) applies; `CrateDbConnectionAccess.EffectivePoolSize` decides it for the data source and for the clamp of
`MaxConcurrentInsertStatements`. Rules to keep:

- **A failed statement used to cost a pool slot for the life of the process.** CrateDB reports the failed-transaction
  state after every failed statement, Npgsql's reset on close then sends a `ROLLBACK`, CrateDB rejects it (5.x `XX000`,
  6.x `42601`), and the exception leaves `NpgsqlConnection.CloseAsync` **before** the connector goes back to the pool.
  `DisposeConnectionSafelyAsync` swallowed the error but the connection stayed open. A rollup over a
  missing table (every tick) drained the 32 slots within a minute of each pod start; after that every statement of the
  tenant, a `DROP TABLE` of `DeleteArchive` too, waited 15 s for a slot, was retried and ended as a "35.0 s"
  `StreamDataStatementTimeoutException` (a scratch tenant on a test environment, 2026-11-04).
- **Now:** after the rejected `ROLLBACK` the still-open connection runs `SELECT 1` (CrateDB answers with the idle state,
  so the next reset sends no `ROLLBACK`) and is disposed again, which returns it to the pool. If that fails too, the
  physical connection is broken through the driver's internal `NpgsqlConnector.Break` (reflection, resolved once; there is
  no public API) so the pool drops it and frees the slot; if even that is impossible it is logged as an error (slot lost).
  `CrateDatabaseClient.CanDiscardConnections` is pinned by a unit test, so an Npgsql update that renames the internals turns
  the build red. Npgsql has no setting that skips the rollback (`No Reset On Close` only covers `DISCARD ALL`), and
  `PoolingEnabled=false` would cost a new connection per statement again.
- **An exhausted pool fails fast.** `LeaseConnectionAsync` turns the driver's "connection pool has been exhausted"
  (`NpgsqlException` + inner `TimeoutException`, `IsConnectionPoolExhausted`) into
  `StreamDataConnectionPoolExhaustedException` (tenant id in the message). It is not an `NpgsqlException`, so
  `CrateResiliencePipeline` neither retries it nor counts it in the shared circuit breaker (one tenant's full pool must not
  cut every tenant off). The caller sees it after the pool's wait time (connection string `Timeout`, 15 s default), not
  after the 35 s call budget.

Tests: `CrateCompat/CrateConnectionPoolReturnTests` (real CrateDB, 5.x and 6.x: pool of 2, five failing reads and writes on a
missing table, then a read and `DROP TABLE IF EXISTS` succeed; a held pool of 1 fails at once with the driver's real exhaustion
error; a discarded connection frees its slot; red without the fix),
`StreamData.UnitTests/CrateDatabaseClientConnectionPoolTests` (classification, no retry, breaker stays closed, driver
internals present).

### Backups leave Hidden columns out

**Behaviour change.** A tenant backup leaves out the values of every column
known to reach a Hidden attribute (residual risk below). `CrateDbStreamDataRepository.FindHiddenColumnExclusion(snapshot, includeRecorded)` is the one
rule: the columns that reach Hidden under the current CK model (`ArchiveHiddenColumnGuard.FindHiddenColumns`, any status), with
`includeRecorded` also `Archive.HiddenColumns`; for a rollup the aggregate columns of an aggregation over a Hidden attribute; plus
the computed columns derived from them (current or pending formula, transitively, every stored version). Used by:

- the native copy: `ResolveCopyTargetAsync` sets `ArchiveCopyTarget.ExcludedColumns`, `StagingCopyRunner.PlanAsync` drops them
  from the data table's description (= the manifest), and a data slice is copied with a column list
  (`CopyStatement.CopyTo(..., columns)`, `format = 'json_object'` - a column list alone makes CrateDB write `json_array`, which
  `COPY FROM` cannot load). An archive without such a column is copied with the statement as before;
- the row export `ExportRowsAsync` (format 1 and the archive data export) - **behaviour change:** it used to leave out only the
  recorded columns of an archive Failed on Hidden columns;
- `EnsureArchiveCreatedAsync` and `ImportRowsAsync` for a raw or time-range archive that is not Activated
  (`WithoutColumnsStillHidden`, current model; the recorded columns only when the model cannot be read): the restore provisions
  and imports such an archive without them and without conflict precedence keys on them. `ImportRowsAsync` decides per column and leaves
  out only the ones the table lacks (`WithoutHiddenColumnsTheTableLacksAsync`): a column of a table provisioned before it became
  Hidden keeps receiving its values (with its precedence key), because a required column is `NOT NULL` there. The lifecycle refuses every activation while a column reaches
  Hidden, so no other path gets there; a column visible again is provisioned as before (`RetryActivation`).
- The COPY projection names top-level columns only: `information_schema` also lists object sub-columns (`obj['member']`), which
  are copied with their object column and are kept in the manifest description (`StagingCopyRunner.TopLevelColumn`).
- When the CK model cannot be read, a backup leaves out the recorded columns only (warning). Failing the dump instead was
  rejected (decided 2026-11-16): a tenant with a broken model (ResolveFailed) must stay backupable for its recovery. Residual
  risk: a column made Hidden by a model that cannot be read, and not yet recorded by the re-validation, can end up in the backup.
  Test: `HiddenColumnExclusionTests.AModelThatCannotBeRead_LeavesTheRecordedColumnsOut`.

Tests: `ComputedColumns/ArchiveHiddenColumnBackupIntegrationTests` (real CrateDB: staged files, manifest, export, restore,
redaction), `StreamData.UnitTests/Copy/HiddenColumnExclusionTests`, `CopyStatementTests`. Rule and restore: engine
`octo-construction-kit-engine/docs/archive-hidden-columns.md`, "Backups".

### Backups leave the leftovers of computed columns out

The physical table of an archive only grows, so it can hold columns its definition no longer owns: removing a computed
column takes it out of the definition and leaves its physical column in the table (with every `{base}__v{N}` version of
its formula changes), and a formula change of a computed column that stays leaves the earlier version behind. The restore
provisions the table from the definition, and its column check (`StagingCopyRunner.EnsureColumnsExistAsync`) refuses a
dump that holds a column the table lacks ("Nothing was loaded"). Before this rule, a native backup of every archive that
ever removed a computed column, or changed the formula of one, could not be restored.

- **What the dump leaves out.** `ResolveCopyTargetAsync` sets `ArchiveCopyTarget.Ownership`
  (`Copy/ArchiveColumnOwnership`), and `StagingCopyRunner.PlanAsync` drops its leftovers from the data table's description
  (= the manifest; a warning names them) and copies the data slices with a column list, like the Hidden columns, but
  only when the source table really holds a leftover (AB#9932, next item). A column
  is a leftover only on positive evidence. It is not owned (standard and platform columns, the ingested or aggregate
  columns, the active and pending names of the computed columns), its type is one a computed column has (`boolean`,
  `integer`, `bigint`, `double precision`, `timestamp with time zone`), and one of these holds:
  - it is the base or a stored version of a computed column the definition still has, other than the active and pending
    name. This holds even when the computed column carries the name of an attribute of the type;
  - it has a versioned name (`{base}__v{N}`), or it is the base of such a column in the same table;
  - the archive's CK type is known and no attribute path of it (records followed) maps to the name. For a rollup this check
    always holds, because all its columns are derived.
- **The whole row unless a column is left out (AB#9932).** A column list makes `COPY TO` write `json_object` lines
  that name every listed column, a NULL as an explicit `null`, and `COPY FROM` keeps that NULL. The whole row leaves a
  NULL out, and the load gives the column its DEFAULT: a NULL `rtcreationdatetime` comes back as `CURRENT_TIMESTAMP`, a
  NULL `was_updated` (the replica defect of CrateDB before 5.10.12, AB#6647) as `FALSE`. That is the documented
  behaviour of the native copy (AB#5490; `CrateVersionMatrixCases.Format2_ANullInAColumnWithADefault_…` in
  `octo-asset-repo-services` pins it). So `StagingCopyRunner.DumpedColumns` names columns only when the target excludes
  Hidden columns or the source table holds leftovers (`ArchiveColumnOwnership.FindLeftovers` over the table, read once
  per dump); otherwise the dump copies the whole row, as before. An archive with a Hidden column or a leftover is
  still dumped with a column list; its explicit NULLs are fixed after the load (next item).
- **Column defaults after the load.** When `StagingCopyRunner.CopyFromAsync` loaded into a table that was empty
  before the load, `FillColumnDefaultsAsync` sets every NULL of a column with a DEFAULT (read from
  `information_schema.columns.column_default` of the table itself, nullable columns only: `rtcreationdatetime`,
  `rtchangeddatetime`) to that default, after the first `REFRESH` and before the row count; an INFO line names the column
  and the number of rows. The UPDATE runs once per data slice of the plan (its time range), so no statement covers more
  rows than the slice's `COPY FROM` within the same command timeout. The data is complete before the fill: a fill
  statement that fails leaves those NULLs (WARN) and never fails the load. So a dump with a column list restores the same
  rows as a whole-row dump. A load into a table that already held rows leaves every NULL alone (rows it did not write keep
  what they hold). Known limit: a load repeated into the same table after a partial load (a failed slice) finds rows and
  skips the fill, so the NULLs of that dump stay; drop and recreate the table before such a repeat, as a fresh restore does. The read path is null-safe as
  well: `CrateDatabaseClient.ToDataPointDto` leaves `RtCreationDateTime` / `RtChangedDateTime` at their default for a NULL
  instead of failing the whole read with an `InvalidCastException` (archives restored before this fix).
- **What the dump keeps.** Every other column stays in the manifest, so the restore still refuses it before loading
  anything. That covers a plain column named like an attribute path of the type (ingested data the definition no longer
  declares), unless a computed column of the definition explains it, as well as a column of another type (`text`, `object`, arrays), and a plain unknown column when the CK type cannot be read.
- **What is dropped, and why that is right.** The values of a removed computed column were computed from the stored
  columns with a formula the archive no longer has; the values of an earlier formula version are superseded by the active
  version. Both are derived, not data, and no reader of the archive reads them.
- **Provisioning names the current versions.** `EnsureArchiveCreatedAsync` provisions a computed column under its active
  name (`{base}__v{N}` after a formula change) and, during a formula change, also under its pending name
  (`WithCurrentComputedColumnNames`). The resolvers name a computed column by its base, which is the active name only until
  the first formula change. Before, a restored definition whose formula had changed got a table without the column its
  values live in: the load was refused, and new rows could not be written.
- **Older dumps.** A native dump made before this rule still carries the leftovers. `COPY FROM` cannot skip a JSON key that
  the table lacks: a strict table rejects the row, and a column list helps only for CSV (checked against CrateDB 6.4). So
  the restore still refuses such a dump, and the refusal says that the columns are leftovers and that a new dump leaves them
  out. The restore never creates them.
- Known limit: a column that someone added by hand, with a name that is no attribute path of the type and with a
  computed column's type, cannot be told apart from a removed computed column. It is left out. The other way round, a
  removed computed column that carried the name of an attribute of the type keeps its version-0 column in the dump: the
  definition no longer explains it, so the restore refuses it. Its versioned columns are left out.

Tests: `ComputedColumns/ArchiveComputedColumnLeftoverBackupIntegrationTests` (real CrateDB). It covers a dump and restore
round trip after a computed column was added and removed, after add, formula change and remove, and for a computed column
that stays after a formula change (its active version round trips, its values are read by the logical name). It also
covers a Hidden column together with a removed computed column, a rollup, a freshly provisioned time-range archive during a
formula change (no column lost), and two refusals: an undeclared ingested column and a `text` column.
`StreamData.UnitTests/Copy/ArchiveColumnOwnershipTests` and `StagingCopyRunnerTests` cover the rules and the refusal
message for an older dump.

### Conflict Precedence — the upsert stops being last-write-wins

Every archive write is an upsert on the row key, and the `DO UPDATE SET` was unconditional: the
**last** delivery won regardless of the data, so the stored value reflected arrival order. Replaying
the same messages in a different order produced a different archive — a correctness defect wherever
one window can be written more than once (corrections, re-sends, a backfill next to a live feed).

`Archive.ConflictPrecedence` (System.StreamData 1.13.0, on the abstract `Archive` base so raw and
windowed alike) is the opt-in that makes the update conditional: an **ordered list of keys**, each
naming one of the archive's own user columns plus `HigherWins` (timestamps, sequence numbers) or
`LowerWins` (rank codes numbered best-first). **An empty list is the default and behaves exactly as
before.**

- **Lexicographic, never conjunctive.** `BuildConflictGuard` / `BuildPrecedenceLevel` in
  `CrateDatabaseClient` render `better(k1) OR (equal(k1) AND (…))`. That is a total order over the
  data, so the surviving value is its maximum — the same whichever write lands first (for writes
  the keys tell apart; on a tie in every key the last level admits equality, so the later write
  replaces the stored row — idempotent re-delivery, and keyless rows behave as before opting in). ANDing the
  keys ("better rank AND newer") would leave the result arrival-order dependent, which is the whole
  defect. Do not "simplify" it into a conjunction.
- **The guard wraps every assignment**, `rtchangeddatetime` and `was_updated` included
  (`Guarded(...)` → `CASE WHEN <guard> THEN <incoming> ELSE "col" END`), so a losing write leaves no
  trace at all. For `was_updated` the fallback is `COALESCE("was_updated", FALSE)` (next rule).
- **No column of an upsert relies on a deterministic column DEFAULT.** CrateDB before
  5.10.12 (measured: 5.9.10, 5.9.13, 5.10.10) stores the NEW rows of an `INSERT … ON CONFLICT DO
  UPDATE` that also hits an existing key in the same shard without such defaults on the **replica**
  copy. `was_updated BOOLEAN NOT NULL DEFAULT FALSE` then reads as NULL there (`IS NULL` finds
  nothing, `GROUP BY` does, a read answers from either copy) and a rollup over it drops the row.
  So `BuildTimeRangeInsertSql` names `was_updated` in the INSERT list with the literal `FALSE` in
  every row, and `RollupAggregationSqlBuilder` always names it too (`FALSE AS "was_updated"` over a
  raw source, `COALESCE(MAX(...), FALSE)` over a windowed one); `generation` and the genmap's
  `rtid_scope` were always written explicitly. `CURRENT_TIMESTAMP` defaults are shipped from the
  primary and are safe. A new column with a constant DEFAULT has to be written by every upsert
  that can insert a row. The single-node test container cannot show this (no replica); the proof
  is `TimeRangeInsertReplicaFlagTests` in `octo-asset-repo-services`, on a two-node cluster.
- **Null rules:** stored NULL is always replaceable (otherwise the first write freezes the history);
  an incoming NULL never displaces a stored value (a write that cannot prove it is better must not
  win); both NULL is a tie and falls through to the next key. The **last** key admits equality, which
  is what makes an identical re-delivery idempotent.
- **Keys must be user columns of the archive** (except the reserved `$Origin`, see *Row Origin*). The guard reads
  `EXCLUDED."k"`, so an unknown name would produce SQL that fails on every single write; the builder refuses and
  names the valid columns.
- **Ingest paths only.** `ImportRowsAsync` deliberately does not apply it — an operator restoring a
  snapshot must get back what they gave.

Full write-up incl. the rendered SQL: `docs/streamdata-archive-concept.md` → *Conflict precedence*.

### Row Origin — manual or automated

Every raw and time-range row records `row_origin` (`manual` / `automated`; `NULL` = written before the origin
was recorded = automated), and a manual row `manual_written_by` / `manual_written_at` / `manual_reason`
(writer from the caller, time from the repository clock, reason optional; NULL on automated rows; a
manual point without a writer is refused). Selectable in queries by these names, not in the default projection.
Rules to keep:

- **Platform column, not a user column.** `ArchiveRowOriginColumns` owns the name, DDL and SQL fragments. Rollup
  tables do not carry it; the windowed insert names it only when `generationTracked` is false. A rollup reports its
  manual values in its own platform columns instead (next section).
- **Always written explicitly**, nullable without a DEFAULT, so `ALTER TABLE … ADD COLUMN` can add it to
  populated tables. `EnsureOriginColumnsAsync` does that at activation and before the first write/import of each
  table per process (`RecomputeProcessState.EnsuredOriginColumns`). Do not remove that step: every insert names
  the column, so a table without it would refuse every write.
- **`$Origin` precedence key** (`ArchiveConflictKey.OriginKey`, `HigherWins` only): manual beats automated,
  automated never beats manual, manual vs manual = the later write wins (no fall-through), automated vs automated =
  the following keys. It is the one key that is not a user column; `BuildConflictGuard` skips the column check for it.
- **Manual wins by default on raw and time-range archives**: `ResolveConflictPrecedence` prepends `$Origin`
  when the archive does not name it (also with no precedence at all: the guard is then `$Origin` alone, automated vs
  automated stays last-write-wins). Named explicitly, `$Origin` applies at the author's position; `Order: Ignore` on it is
  the opt-out (the key is dropped; with no other key there is no guard). `LowerWins` on `$Origin`, `Ignore` on a column
  and `$Origin` named twice are refused at activation. The skip reason names the key that decided
  (`ConflictOutcome.DecidingReason`: the first key in which the stored row differs from the write; a ranking column the
  write does not send ties): `ManualValueProtected` only
  when `$Origin` decided (origin first, or every key before it tied), `LowerPrecedence` when a column key before it decided,
  also against a manual row; never `ManualValueProtected` when the origin is ignored. Rollups and the import path are unchanged.

- **Reading the origin**: `row_origin`, `manual_written_by`, `manual_written_at`, `manual_reason` are
  selectable/filterable/groupable on raw and time-range archives. Every read-path column reference goes through
  `ArchiveRowOriginColumns.ColumnRef` / `SelectRef`, which reads `row_origin` as `COALESCE(…, 'automated')` (legacy
  rows hold NULL). A new SQL builder that references a column must use them too.

Full write-up: `docs/streamdata-archive-concept.md` → *Row origin*. Tests: `ArchiveRowOriginSqlTests`,
`ArchiveRowOriginQuerySqlTests` (unit), `StreamDataOrigin/ArchiveRowOriginIntegrationTests` and
`ArchiveRowOriginReadIntegrationTests` (real CrateDB).

### Changes an activated archive cannot apply

`ArchiveDefinitionGuard` (engine) refuses them, also on a disabled archive with `FirstActivatedAt` (`SetStatusAsync` stamps it), on the ways an operator or a blueprint writes: the repository's public
`ApplyChanges` and the Upsert import. The stream data stores of this repository write through `UpdateOneRtEntityById`
and are not checked; keep their writes on that path, or a lifecycle step (computed columns, watermark) is refused. Rules and
scope: `docs/streamdata-archive-concept.md` §10. `RollupOrchestrator` records a failed run on the entity
(`MongoRollupArchiveRuntimeStore.RecordRunFailureAsync`, `ClearRunFailureAsync`). Tests: `ArchiveDefinitionGuardIntegrationTests`
(real MongoDB, blueprint `TestBlueprints/.../ArchiveGuardBp`).

Adding a column to an activated archive: never write it into `Columns` without the storage change. The direct path is `ArchiveLifecycleService.AddArchiveColumnAsync` (storage via `CrateDbStreamDataRepository.AddIngestedColumnStorageAsync`, then the definition); a seed's additions are taken out of the import and applied after the commit through `IArchiveLifecycleServiceProvider` (`TenantArchiveLifecycleServiceProvider`, resolves `ISystemContext` lazily to avoid a DI cycle). Rules: `docs/streamdata-archive-concept.md` §10, *Adding a column to an active archive*.

Archives a blueprint seed creates: the blueprint engine activates them after the import and queues the backfill of new rollups
through `IStreamDataTenantServicesProvider`, implemented here by `TenantStreamDataServicesProvider` (registered next to the lifecycle provider,
resolves `ISystemContext` lazily for the same DI-cycle reason; returns `null` without a stream-data backend, `IsStreamDataEnabled` from the tenant
flag). Rules: engine `docs/blueprints.md`, "Archives and rollups in a blueprint". Test: `BlueprintArchiveActivationIntegrationTests` (fixture
`BlueprintArchiveActivationFixture`: MongoDB plus a real CrateDB container wired into the tenant context; test blueprints `ArchiveActivationBp`,
`ArchiveBackfillBp`; drives the backfill with `IRecomputeOrchestrator.TickAsync` and checks the rollup of a rollup converges).

### Rollups report their manual values

Every rollup table carries `manual_count` (`BIGINT`) and `last_manual_at` (`TIMESTAMP WITH TIME ZONE`), platform columns
like `row_origin` on raw rows: not in `Aggregations[]`, not in `Columns`, no definition or blueprint changes. Rules to keep:

- **Computed in the aggregation statement** (`RollupAggregationSqlBuilder`, `PlatformColumnsFor`): from a base archive
  `COUNT(CASE WHEN "row_origin" = 'manual' THEN 1 END)` and `MAX("manual_written_at")`, from a rollup source
  `SUM("manual_count")` and `MAX("last_manual_at")` (`sourceIsRollup`). `SUM`/`MAX` skip the `NULL` of a source row
  aggregated before the column existed, so a bucket over a mix is right. The carry row of the LOCF statement is excluded.
- **Nullable without a DEFAULT, always named**. `NULL` means "no manual values" and reads as 0
  (`ArchiveRowOriginColumns.ColumnRef`, also in filters and `RollupQueryAggregationResolver.ResolveManualCount`). A new read
  path must use `ColumnRef` too.
- **`ArchivePlatformColumnGuard` adds what a table lacks**, once per table and process (memo `EnsuredOriginColumns`, shared by
  the two kinds; a table has one kind): before a statement names them (`AggregateBucketAsync` and the recompute executor, for the source
  **and** the target — a raw or time-range source may not have the origin columns yet), at activation, on import, and from the orchestrator
  tick (`EnsureRollupPlatformColumnsAsync`, before anything can skip the rollup), so readers get the columns without
  a due bucket. The reconciliation for declared columns stays as it is: `ArchiveColumnReconciliation` still skips rollups.
- **The staging table has the shape of `GenerateCreateWindowedTable(includeGeneration: true)`**, the copy names the columns
  (`columnNames`), so they survive the generation flip.
- **Reserved names**: `RollupValidator` (generated target names too, `Count` over `Manual` is `manual_count`) and
  `ComputedColumnValidator` (every column of every archive) throw `ArchiveReservedColumnNameException`. A rollup from before the reservation
  that stores such a column keeps it, the statement leaves the platform value out (`PlatformColumnsFor`) instead of failing.
- **Dump/restore**: the row path carries them with the user columns (`ImportRowsAsync`), the native copy copies the table; a dump
  from before they existed loads with `NULL`.

Tests: `StreamDataOrigin/RollupManualColumnsIntegrationTests` (real CrateDB), `ArchivePlatformColumnGuardTests`. Check whether a tenant
needs a backfill: `scripts/rollup-manual-rows-check.sh`. Write-up: `docs/streamdata-archive-concept.md`, *Rollups report their manual values*.

### Write Result — stored and skipped rows

The repository inserts return `StreamDataWriteResult` (`Stored`, `Skipped` with reason `CkTypeMismatch` /
`LowerPrecedence` / `ManualValueProtected`). A guarded statement ends with `RETURNING` key + ranking columns +
`row_origin`; `ConflictOutcome.Decide` decides stored ⇔ the returned values equal the sent ones (exact because
the last key admits equality); the reason of a skipped row is the key that decided (
**Behaviour note:** an automated write that loses on a column key placed before `$Origin` was reported as
`ManualValueProtected` whenever the stored row was manual, now `LowerPrecedence`; storage is unchanged). Tests:
`StreamDataOrigin/SkipReasonDecidingKeyIntegrationTests` (raw and time-range). The client inserts return the skipped rows as `SkippedArchiveRow(Index, Reason)`;
unguarded statements send no `RETURNING`. Only stored rows feed the retroactive detection. A manual row that is
not stored throws `ManualStreamDataNotAppliedException`. The single-point `InsertAsync` goes through the batch path.
Tests: `ConflictOutcomeTests`, `StreamDataOrigin/ArchiveRowOriginIntegrationTests` (multi-statement exactness on
CrateDB). Write-up: `docs/streamdata-archive-concept.md` → *Write result*.

**A dropped shard request is a failed write.** When CrateDB's memory circuit breaker trips it ends a shard request
of a multi-row INSERT and answers **without an error**. A guarded statement then lacks the `RETURNING` rows of the dropped rows
(`ConflictOutcome.Decide` reports them as `Missing`, never as a skip: an upsert returns a row for every row it processed), an
unguarded statement reports fewer affected rows than rows sent (CrateDB counts a key repeated in one statement once per row, also two points less than 1 ms apart that are one stored row; `ArchiveWriteRepeatedKeyIntegrationTests`). `CrateDatabaseClient.InsertGroupedAsync` writes the
missing rows once more (`MissingRowRetry`) (guarded: those rows, unguarded: the statement) and then throws `StreamDataWriteIncompleteException`
(archive table, rows sent / missing). It is not a contracts `StreamDataException` on purpose: the asset repository maps that to
400. Rules: never turn a missing `RETURNING` row back into a skip reason, never catch the exception into a success. Tests:
`MissingRowRetryTests` (unit), `StreamDataWriteLoss/ArchiveWriteShortfallIntegrationTests` (CI, < 1 s: a `CHECK` constraint makes
CrateDB drop rows of a multi-row INSERT without an error, the same signature), and the stress test (`Category=Stress`, not in the CI)
`StreamDataWriteLoss/ArchiveWriteUnderMemoryPressureIntegrationTests` (own CrateDB container, 512 MB heap, query breaker set per
test: guarded 25 %, unguarded 5 %; skipped with that reason if the breaker never trips; ~15 s; red without the check).
**The sub-batches of one write run four at a time.** `InsertGroupedAsync` hands the rows of each statement shape to
`InsertStatementLanes`: `Plan` splits them into lanes by a stable hash of the row key (at most
`StreamData:MaxConcurrentInsertStatements`, default 4, clamped to 1..the effective pool size (`StreamData:MaxPoolSize`,
see the connection pool section); environment variable
`OCTO_STREAMDATA__MAXCONCURRENTINSERTSTATEMENTS` in the `OCTO_`-prefixed services, `StreamData__MaxConcurrentInsertStatements`
without a prefix; a property of `StreamDataInstanceConfiguration`, which `AddCrateDbStreamDataRepository` binds from the
`StreamData` section like `Enabled` and `SchemaInstancePrefix`, and which the client reads in its constructor; a write
of one statement keeps one lane and the old split), `RunAsync` runs the lanes at the same time and the statements of a lane one after another. Rules: two
statements in flight never carry the same key and the rows of one key keep their order (never split a key across lanes,
never run the statements of a lane concurrently); the shapes (`ArchiveColumnSets` segments) still run one after another;
the skipped rows are sorted by position, so they equal the sequential result; `MissingRowRetry` stays per statement. On a
failure no lane starts another statement after the first failed one, the statements in flight finish, the first failure is
rethrown unchanged (no `AggregateException`) and a later failure of a statement in flight is dropped without a log entry. On
a failed write the set of rows already written can differ from the sequential order. All of them are idempotent upserts
(guarded where the archive has conflict precedence), so a retry of the same insert converges when no other write of the
same keys comes in between; without conflict precedence the last write wins, and a retry can replace a newer value that
another write stored meanwhile. The client plans its lanes in `CrateDatabaseClient.PlanInsertStatements`
(limit from the configuration, key from the row).
Tests: `InsertStatementLanesTests` (unit, fake statement executor: limit reached and never exceeded, no key in two statements,
order per key, failure contract; `PlanInsertStatements` uses the configured limit and keeps a key in one lane),
`StreamDataWriteLoss/ArchiveWriteConcurrencyIntegrationTests` (real CrateDB, limit 1 and 4 compared: 130,000 guarded
time-range rows, and keys written again 1,000 to 3,000 rows later, which the lanes put into the same statement).
**The rollup statements check their rows too.** The forward aggregation, the recompute's staging aggregation (both
through `RollupSourceBucketAggregator`) and the staging-to-live copy are `INSERT … SELECT` statements that CrateDB answers the
same way. Each compares its affected rows with the rows it must write: the aggregation with
`RollupAggregationSqlBuilder.BuildExpectedRowCount` (same source predicate and generations, `GROUP BY rtid` of the key only,
counted **before** the statement, so late source rows can only raise the affected count; on the LOCF path the carry window is
part of it; on a shortfall the source is counted once more, because a source row deleted meanwhile lowers what the statement can
write: rows written >= the recount is no loss, and the repeat is judged against the recount), the copy with the row count of its
slice that sizes it anyway. A shortfall is written once more (the aggregation
upsert again, the copy with `ON CONFLICT DO NOTHING` and then counted in the live table after a refresh); still short, it throws
`StreamDataWriteIncompleteException` naming the table, the bucket or slice and the counts. Before the flip the executor also
checks the generation end to end, independent of the reported counts (`VerifyGenerationCompleteAsync`): staged rows >= the rows
the aggregations reported, rows of the new generation in the live range >= staged rows. The copy fails before the flip, so the
pointer stays on the previous generation, the staging stays until the next run clears it, and the rows already copied are
leftovers the next run's step 0 removes. A shortfall under a source pointer that moved is not a loss: the bucket is aggregated
again (`SamePointers`). A repeated aggregation row that already existed takes the conflict branch and is flagged `was_updated`.
Tests: `StreamDataWriteLoss/RollupShortfallIntegrationTests` (CI, < 1 s each: a wrapped client deletes rows after the statement and
reports the count without them; forward once / always, staging once, copy once / always) and the stress test (`Category=Stress`,
not in the CI) `StreamDataWriteLoss/RollupUnderMemoryPressureIntegrationTests` (same container, query breaker 15 %, 6 rollups of one raw
source with 20,000 entities in parallel; the recompute flipped to an incomplete generation without the check; the forward
aggregation did not lose rows silently in the measured runs, only loud breaker errors). **Not only the query breaker:** CrateDB's
parent breaker (`indices.breaker.total`, 95 % of the heap, compared with the real heap usage) refuses an upsert batch the same way.
With a nearly full heap the loss came in whole blocks of 10,000 rows, the batch size of CrateDB's insert-from-select (measured: exactly 10,000 of 20,000 staged
rows, three times in a row, at the default query limit, heap 471 of 512 MB; the rows were really missing after a refresh, the count
was right). The tests of the `CrateMemoryPressure` collection therefore drop their tables after every round
(`CrateMemoryPressureFixture.DropTenantTablesAsync`); without that the leftover tables of the earlier tests filled the small heap
and the later tests lost rows without any breaker setting. Their set-up steps run without a retry: at the default limits they must
not lose a row.

### Value check on raw and time-range writes (raw path)

**Behaviour change.** `InsertAsync` (raw archives) now
runs the same value check as `InsertTimeRangeAsync`: `ArchiveWriteValueValidator.Normalize` has a `StreamDataPoint`
overload, called first thing after the archive snapshot is loaded. A value that does not fit its column's CK type refuses
the whole batch with an `ArgumentException` (asset repository: 400) naming point (index, rtId, timestamp), column and
expected type; nothing is written, no origin column is added, no dirty window is recorded. Enum names become their keys.
**Behaviour change for automated raw writes too:** the check does not look at the origin, so a pipeline batch with e.g.
text in a Double column is now refused up front with this message instead of being handed to CrateDB. Rules to keep:

- One core (`NormalizeCore`) for both point types; the overloads differ only in how a point is named and rebuilt. Both
  get the points already flattened (`ArchiveRecordFlattening`), so the check reads exactly the keys the write
  maps to columns. Checking a shape the write does not use would refuse or pass the wrong value.
- Manual protection and the rollup recompute of raw manual writes are proven against CrateDB:
  `StreamDataOrigin/RawArchiveManualWriteIntegrationTests` (manual value, writer and reason survive an automated delivery
  of the same rtId + ckTypeId + timestamp; a manual write inside an aggregated window records a `Manual` dirty window and
  the recomputed hourly bucket holds the corrected value, replaced, not added; a mismatched batch stores nothing).
- Unit tests: `ArchiveWriteValueValidatorRawTests`. Write-up: `docs/streamdata-archive-concept.md` → *Value check before the write*.

### Partial delivery: a column the point does not name keeps its value

**Behaviour change.** Raw and time-range writes
update only the columns a point names. A column not named keeps its stored value, a column named with `null` is
cleared, a new row stores NULL for every column not named (decided by Reimar 2026-11-09). **Behaviour change for
automated deliveries:** a partial re-delivery no longer clears the columns it did not send. Rules to keep:

- "Named" = the key is in the point's attribute dictionary. Never fill missing keys with `null` on the way to
  `CrateDatabaseClient` (that is exactly the old defect); the import path fills every column on purpose.
- `ArchiveColumnSets.Group` splits a batch by named column set (one statement shape per set, order kept per row key);
  `BuildRawInsertSql` / `BuildTimeRangeInsertSql` take the INSERT list and `updatedColumnNames` separately.
- **Ranking columns** (the column keys of `ConflictPrecedence`; `$Origin` is no column) follow the rule, but cannot be
  null (decided by Reimar 2026-11-09). Not sent: the stored value stays **and is the value the guard compares** — the
  guard reads `COALESCE(EXCLUDED."k", "k")` for the keys the statement's rows do not send
  (`BuildConflictGuard(precedence, insertColumns, sentColumnNames)`), so the next key decides; a new row stores NULL.
  `ConflictOutcome.Holds` skips an unsent ranking column, so a point that wins without sending it is reported stored
  (a manual one never gets a 409). Sent with a value: compared and written as before. Sent as explicit `null`:
  `ArchiveRankingColumnValidator` refuses the whole batch with an `ArgumentException` before anything is read or
  written (asset repository: 400), raw and time-range, automated and manual. Ranking columns are always in the INSERT
  list (bound NULL when not sent) and only replaced when sent. Two earlier variants are withdrawn: refusing points
  without every ranking column (C) and writing an unsent ranking column as NULL (D).
- No column of an upsert relies on a constant DEFAULT: user columns have none, `was_updated` stays a literal.
- Computed columns at ingest are evaluated on the **merged row** (decided by Reimar 2026-11-09): left out when the
  point names none of the columns the formula reads (`ComputedColumnPlanItem.ReferencedColumns`,
  `ApplyComputedColumns(..., ingest: true)`); when it names only some, the others are the stored values of the row
  (NULL on a new row); an input named with `null` stays null. `ApplyIngestComputedColumnsAsync` reads the missing
  inputs once per batch by primary key (`IStreamDataDatabaseClient.ReadStoredRowsAsync`, `StoredRowLookup`, real-time
  in CrateDB, no refresh needed) and only for such points; the stored values feed the formula and are not written back.
  The formula stays in C# (mXparser has no SQL form). Read and write are two statements, so a concurrent delivery of the
  same key in between can be missed. Backfill, repair and rollup evaluation are unchanged.

Write-up: `docs/streamdata-archive-concept.md` → *Partial delivery*. Tests: `PartialDeliverySqlTests`, `StoredRowLookupTests`,
`ArchiveRankingColumnValidatorTests`, `ConflictOutcomeTests`, `StreamDataOrigin/PartialDeliveryOriginPrecedenceIntegrationTests`
(EC case, merged-row ranking, explicit null refused, mixed batches),
`CrateDbStreamDataComputedColumnIngestTests` (cases), `StreamDataOrigin/PartialDeliveryIntegrationTests`,
`StreamDataOrigin/ComputedColumnPartialDeliveryIntegrationTests` (merged row on raw and time-range, rollup over the column).

### Record values become their member columns on both write paths

**Behaviour change.** `InsertAsync` (raw archives)
dropped every record value a point carried. A pipeline (`CreateUpdateInfo@1` → `SaveStreamDataInArchive@1`) delivers the
entity's attributes as they are, so a column on a record sub-path (`NamePlate.SerialNumber`) arrived as one key
`NamePlate` → `RtRecord`, which is no column: the member was neither written nor cleared, so the stored
value simply stayed. The time-range path expanded records (`FlattenRecordValues`); the raw path did not. The REST raw
`insert` was not affected because the asset repository accepts only dotted column paths (`ArchivePointValidation`
refuses a record key with 400), so its points carry no record values. **Behaviour change for raw writes from pipelines:**
record members are now written (value), cleared (`null`) or kept (member not in the record), like every other column.
Rules to keep:

- **One place.** `ArchiveRecordFlattening.Flatten` runs first in both insert paths, right after the snapshot is loaded and
  before the value check, the ranking-column check, the computed columns and the row mapping; every later step reads the
  same dotted keys. The validators no longer take a flatten function. Do not flatten again further down, and do not add a
  second expansion elsewhere.
- **Members, not records.** A member with a value is named with it, a member set to `null` is named with null (clears the
  column), a member the record does not contain is not named (keeps the stored value). Nested records expand
  recursively; a record without members names nothing. A whole record sent as `null` (`NamePlate: null`) names the key
  `NamePlate`, which is no column, so the members keep their values (unchanged, same on both paths).
- **No double work for REST.** Points without a record value pass through as the same instance (same list when no point
  carries one), so dotted REST keys are not copied or re-keyed. The import path (`ImportRowsAsync`) maps exported physical
  column names and is unchanged.
- Ranking columns on a record member are found by the null refusal and compared on the merged row; computed columns that
  read a record member see it; manual protection applies as for any column.

Tests: `ArchiveRecordFlatteningTests`, `ArchiveRankingColumnValidatorTests` (a null ranking member inside a record, both
paths), `StreamDataOrigin/RecordMemberWriteIntegrationTests` (real CrateDB, raw and time-range: member value over a value
the REST shape stored, explicit null, member left out and no record at all, new row, computed column over a record member,
ranking column on a record member compared and its null refused, manual protection). The pipeline chain against the real
repository: `octo-mesh-adapter` `RawArchiveRecordMemberPipelineIntegrationTests`.
