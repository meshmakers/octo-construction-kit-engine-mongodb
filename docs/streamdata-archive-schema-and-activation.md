---
description: "StreamData archive schema: column reconciliation, storage layout, snapshot mapping, activation DDL, computed columns"
applies_to: src/Runtime.Engine.CrateDb/**, src/Runtime.Engine.MongoDb/StreamData/**
---

### Declared-vs-physical Column Reconciliation

Archive tables are provisioned with `CREATE TABLE IF NOT EXISTS`, so on an **already-activated**
archive a newly declared column never reached CrateDB: everything written to it was dropped as
unknown, and once anything referenced it in SQL — which an opt-in `ConflictPrecedence` key does —
**every** write to the archive failed. The only remedy was dropping the table, i.e. losing the
archive's history, which made the opt-in unusable on exactly the archives that have the ordering
defect.

`CrateDbStreamDataRepository.ReconcileDeclaredColumnsAsync` now runs after the `CREATE TABLE IF NOT
EXISTS` and issues `ALTER TABLE … ADD COLUMN` for declared-but-missing columns. The plan is computed
by the pure `ArchiveColumnReconciliation.Plan`, which is deliberately narrow:

- **Add-only.** Nothing is ever dropped or retyped — that would destroy data.
- **Ingested columns only.** Computed columns have their own versioned backfill path.
- **A `Required` column is added nullable**, because existing rows cannot retroactively have a value.

`ArchiveLifecycleService.ActivateAsync` used to return early for an already-`Activated` archive;
it now reaches provisioning (idempotent by contract, one catalogue query when there is nothing to
do), which is what makes the reconciliation reachable at all. Verified against CrateDB 5.10.10 on a
populated table: the ALTER succeeds, existing rows carry NULL, and 316,268 rows stayed untouched.

### Storage Layout

Per-tenant CrateDB schemas hold one table per `CkArchive` (and per `CkRollupArchive`). The Mongo
side carries:
- `RtCkArchive` entities (raw archives) — `Columns[]` paths are CK-type attribute paths.
- `RtCkRollupArchive` entities (rollup archives) — inherit `CkArchive`. The `Aggregations[]`
  list is the authoritative spec; `Columns[]` is a derived projection produced by
  `RollupColumnGenerator.Generate` so the inherited mandatory-attribute validation passes.

### Snapshot Mapping (`MongoCkArchiveRuntimeStore.MapToSnapshot`)

The shared `CkArchiveSnapshot` covers both subtypes. When the loaded entity is an
`RtCkRollupArchive`, the mapper:
1. Projects the runtime `Aggregations` to `CkRollupAggregationSpec` and re-runs
   `RollupColumnGenerator.Generate` to fill `CkArchiveSnapshot.Columns` (the on-disk Columns
   slot is treated as a dehydrated cache; the spec list is authoritative).
2. Sets `CkArchiveSnapshot.RollupAggregations` so the activation / DDL path can branch.

### Tenant-level Disable is a verified precondition

`TenantContext.DisableStreamDataAsync` enumerates the tenant's archives (`GetArchiveRuntimeStore().EnumerateAsync()`,
gated on `System.StreamData/Archive` being in the CK cache — no model, no archives, no check) and
throws `StreamDataDisableBlockedException` (`Runtime.Contracts.MongoDb`, a `StreamDataException`)
while any archive is **Activated**: an Activated archive still accepts ingest and is still ticked by
the rollup/recompute orchestrators, which gate on archive status, not on the tenant flag. The message
names every blocking archive as `Kind 'Name' (Activated)` in a deterministic order; the asset
repository maps the exception to HTTP 409 and appends the operator verbs. Disabled/Failed/Created
archives never block, the flag flip keeps the model, the entities and the tables, and the check runs
regardless of the current flag value. Read failures propagate — an unreadable state never reads as
"nothing is activated".

### Activation DDL Branch (`CrateDbStreamDataRepository.EnsureArchiveCreatedAsync`)

- Raw archive snapshots → `ArchivePathTypeResolver` walks the CK type tree to resolve each
  attribute path to a `CrateColumnType`.
- Rollup snapshots → `RollupColumnTypeResolver` derives the SQL type from the aggregation
  function:
  - `COUNT` → `BIGINT`
  - `AVG` → `{base}_sum DOUBLE PRECISION`, `{base}_count BIGINT`
  - `SUM` / `MIN` / `MAX` → `DOUBLE PRECISION`
  - `First` / `Last` → a single `DOUBLE PRECISION` column holding the value at the
    earliest / latest observation in the bucket. CrateDB has no `arg_min`/`arg_max` and `MIN`/`MAX`
    reject arrays, so `RollupAggregationSqlBuilder` wraps the source in a `ROW_NUMBER()` sub-select
    (`AppendArgSourceSubquery`) ranked by the raw `timestamp` (raw source) or child `window_end`
    (windowed / cascade source), and the outer `GROUP BY` picks `MAX(CASE WHEN rn = 1 THEN value END)`
    — a rollup-of-rollup thus re-picks the earliest / latest child bucket. Numeric source columns
    only. In the LOCF path (a co-occurring TWA) the rank sorts the carry-in row last and the pick is
    `AND NOT is_carry`-guarded. The stored column is read back directly as its own series
    (`First`/`Last` are not ad-hoc query aggregates).
  - `TimeWeightedAvg` → `{base}_integral DOUBLE PRECISION` (Σ value × Δt in value·ms),
    `{base}_duration BIGINT` (covered ms); default base name uses the short token `twavg`.
    Forward aggregation over a raw source uses a LOCF statement (carry-in row per rtId bounded
    by `RollupArchive.CarryLookbackMs`, default 35 d; interval weighting via `LEAD`) built by
    `RollupAggregationSqlBuilder.BuildWithLocfCarry`; windowed sources weight by window length.
    Read path recombines `SUM(_integral) / NULLIF(SUM(_duration), 0)` (alias suffix `_twavg`);
    cascades chain the pair via SUM specs. Direct TWA over raw archive queries is guarded with
    a `NotSupportedException` in `QueryVariable` (follow-up: concept-time-weighted §6.2).

  Rollup column names are storage identifiers (e.g. `temperature_avg_sum`), not CK-type
  attribute paths — the path-resolver would fail to resolve them.

### Computed Columns — Formula Names

A computed-column formula is evaluated in .NET via mXparser at ingest, **never in SQL** — it appears
in no DDL. Operands are bound as mXparser `Argument`s whose names cannot contain a dot, and the row
dictionaries the evaluator reads are keyed by the **physical** column name (`ColumnNameMapper`:
dot-stripped, lower-cased). So the stored formula is always physical: `amountvalue / 1000`.

Callers may nevertheless write the **logical** vocabulary the Studio lists and the query surface uses
(`Amount.Value`, `ObisCode`). `ComputedColumnFormulaRewriter` translates it, reached through
`IStreamDataRepository.NormalizeComputedFormulaAsync` because the naming rule is `ColumnNameMapper`'s
and that is internal to this layer — the same reason `ValidateComputedColumnsAsync` sits on that
contract. `ArchiveLifecycleService` calls it at exactly two points, `AddComputedColumnAsync` and
`UpdateComputedColumnFormulaAsync`; **everything downstream is unchanged** and still sees only
physical names, which is why no stored formula needed migrating.

Three properties worth keeping when touching the rewriter:

- It replaces an identifier run **only when the whole run matches** a column name. A prefix match
  would turn `Amount.Foo` into `amount.Foo` — broken output instead of an honest unknown-column
  error — and whole-run matching makes longest-match fall out for free.
- Numeric literals are skipped wholesale, exponent included. Runs start only on a letter or `_`, so
  `1.5` is safe by construction, but `1.5e3` would otherwise start a run at `e` and a column named
  `e3` would land inside a number.
- A run that matches nothing is left as written: that is what keeps physical names (and mXparser
  function names) working, and it lets the validator reject an unknown name by the caller's spelling.

In `UpdateComputedColumnFormulaAsync` the rewrite happens **before** the no-op check. The stored form
is physical, so comparing the caller's logical spelling against it would always look like a change —
re-saving the identical formula from the UI would version the column (`power__v1`, `__v2`, …),
backfill it and swap the pointer, every time.

**Logical names in results.** The versioned physical column (`{base}__v{N}`) is SQL-internal.
`StreamDataFieldResolver.RegisterComputedColumn` registers a computed column with `CrateDbName` = the
unversioned base (the key of every result alias, `StreamDataRow.Values` entry and GraphQL wire mapping)
and `PhysicalName` = the active versioned column. All SQL (SELECT, WHERE field filters, ORDER BY, aggregate
arguments, LOCF/TWA/Integral/StateDuration columns, downsampling group-by) uses `PhysicalName`; every alias
is built from `CrateDbName`. A rollup whose source is a computed column reads the source's active version
(`RollupAggregationColumns.BindDeclared(spec, source)`), while its own target column names stay logical.
Never put `PhysicalName` into an alias — a second formula change would rename it again and break saved
queries and MeshBoard widgets. Tests: `CrateDbStreamDataComputedColumnLogicalNamingTests` (unit, SQL),
`ComputedColumnLogicalNameQueryIntegrationTests` (real CrateDB, formula changed 0/1/2 times).

### Computed Columns — No NULL for an aborted evaluation, repair

`ApplyComputedColumns` no longer stores NULL when the formula engine throws (`FormulaEvaluationException`, out of memory): ingest
fails the write, the backfill (`EvaluateBackfillPage`) evaluates the page once more and then throws a contracts
`StreamDataException` naming the column, the time slice and the last written key (nothing of the page is written). A formula that
yields no value (NaN / null) still stores NULL. `RepairComputedColumnAsync` walks `[from, to)` in the backfill's slices
with the extra page filter `ComputedColumnBackfillSqlBuilder.BuildRepairFilter` (target NULL and an operand the formula references
not NULL) and writes the active column. Tests: `CrateDbComputedColumnEvaluationFailureTests` (incl. a 2,000,000-row synthetic
backfill: ~4.6 KB allocated per row, live heap flat).
