# StreamData Archive — Concept & Implementation Plan

**Status:** Implemented (T1–T17 of the plan + UI/Authz/Observability follow-ups, see §20)
**Created:** 2026-04-28
**Last update:** 2026-05-08
**Owner:** TBD
**Scope:** Redesign of the CrateDB-backed StreamData subsystem across `octo-construction-kit-engine`, `octo-construction-kit-engine-mongodb`, `octo-asset-repo-services`, `octo-mesh-adapter`, `octo-sdk`.

---

## 1. Motivation

The current StreamData implementation stores all time-series data in a single tenant-wide CrateDB table using `OBJECT(DYNAMIC)`. This makes the schema implicit, queries less efficient, retention impossible to scope, and entangles CrateDB code with MongoDB code in the same .NET project.

Goals of the redesign:

1. **Typed, predictable storage** — each archive maps to a CrateDB table with real columns, derived from CK-model attribute types.
2. **Per-CkType configurability via runtime Archives** — multiple archives per CkType, each defining which attribute paths to capture.
3. **Strict schema lifecycle** — once an archive contains data its schema is frozen; new versions require a new archive.
4. **Clean separation** — move CrateDB-independent contracts to `octo-construction-kit-engine`, isolate CrateDB into its own project within `octo-construction-kit-engine-mongodb`.
5. **Two-tier activation** — instance-level and tenant-level enable flags.

---

## 2. Design Decisions (settled)

| # | Topic | Decision |
|---|---|---|
| D1 | Tenant isolation in CrateDB | One cluster, **one schema per tenant**. Schema name = `RtCkId.SemanticVersionedFullName` cleaned (same helper as MongoDB collection names). |
| D2 | Column casing | **camelCase**, consistent with MongoDB. Standard columns: `rtId`, `timestamp`, `ckTypeId`, `rtCreationDateTime`, `rtChangedDateTime`, `rtWellKnownName`. |
| D3 | Multiple archives per CkType | **Yes.** |
| D4 | Inheritance | Archive defined on CkType A applies to all derived types; **separate table per concrete type**. |
| D5 | Path → column mapping | Scalar path → flat column. Record path → `OBJECT(STRICT)` column. Array path with `[*]` → `ARRAY(<elem>)` column. |
| D6 | Column name for nested paths | **camelCase concatenation** via `ColumnNameMapper.PathToColumnName`: `sensor.reading.value` → `sensorReadingValue`. (Original draft kept the dotted form quoted, but during T17 implementation we switched to camelCase to avoid CrateDB OBJECT-subscript collisions and to match the BSON convention used in MongoDB.) |
| D7 | Required scalar missing on insert | Throw exception. |
| D8 | Required array element missing path value | `null` allowed in resulting array (no exception). |
| D9 | Archive evolution | Archive is immutable once activated. New version = new archive (new RtId / new well-known-name). |
| D10 | Mesh-adapter ingestion | Archive ID configured **per `SaveStreamDataInArchive` pipeline node**. No auto-fan-out. |
| D11 | Bestand migration | **Hard cut.** No automated migration of legacy data. |
| D12 | Archive identifier in API | `OctoObjectId` (the `RtId` of the `CkArchive` instance). |
| D13 | Activation layers | **Three levels, AND-coupled:** instance-flag (appsettings) → tenant-flag (existing) → archive-status. All three must be active. |
| D14 | StreamData model location | **Separate `StreamData` CK model**, loaded only when StreamData is enabled. |
| D15 | Disabled archive | Blocks **both reads and writes**. Data is preserved. |
| D16 | Archive deletion | DROP CrateDB table + set `rtState = Archived` on the `CkArchive` RtEntity (soft delete via standard mechanism). |

Open question to revisit during implementation: D6 (verify quoted-dotted columns don't conflict with OBJECT subscript syntax in real queries).

---

## 3. Domain Model

New CK model `StreamData` (separate from System CK model):

```yaml
ckEnum:
  CkArchiveStatus:
    values: [Created, Activated, Disabled, Failed]

ckRecord:
  CkArchiveColumn:
    attributes:
      - id: path
        type: string
        required: true
      - id: required
        type: bool
        required: true
      - id: indexed
        type: bool
        required: false
        default: true        # CrateDB default — opt-out only when justified

ckType:
  CkArchive:
    attributes:
      - id: targetCkTypeId
        type: CkId<CkTypeId>
        required: true
      - id: columns
        type: array<CkArchiveColumn>
        required: true   # min 1
      - id: status
        type: CkArchiveStatus
        default: Created
```

### Status state machine

```
                  ┌─────────┐
                  │ Created │
                  └────┬────┘
                       │ activate
              ┌────────▼────────┐
   ┌─────────►│   Activated     │
   │          └───┬─────────┬───┘
   │ enable      │         │ disable
   │   ┌─────────▼┐    ┌────▼─────┐
   │   │  Failed  │    │ Disabled │
   │   └─────┬────┘    └────┬─────┘
   └────retryActivate───────┘ enable

  Delete from any state ─▶ DROP TABLE + rtState = Archived
```

- **Created** — definition only, no table. Inserts/queries throw.
- **Activated** — table exists, schema frozen, reads + writes accepted.
- **Disabled** — table exists, reads + writes both blocked. Schema unchanged.
- **Failed** — activation DDL failed; retry via `activate` again.
- **Delete** — orthogonal: drops the table and soft-archives the `CkArchive` RtEntity.

Schema is immutable while in Activated/Disabled/Failed (after first successful activation). Updating `targetCkTypeId` or `columns` after activation must be rejected by validation hook on `CkArchive` updates.

---

## 4. CrateDB Storage Layout

### Schema and table naming

| Element | Convention | Length limit |
|---|---|---|
| Schema | `clean(tenantRtCkId.SemanticVersionedFullName)` | 63 chars |
| Table | `clean(targetCkType.SemanticVersionedFullName) + "_" + clean(archiveRtCkId.SemanticVersionedFullName)` | 200 chars |
| Column | camelCase from CK attribute id; nested paths kept quoted with dots | — |

`clean()` = strip non-alphanumerics; reuse helper currently in `CkIdExtensions.GetCkTypeCollectionName` (to be moved to a shared namespace).

CrateDB's identifier limit is 255 bytes (UTF-8). Schema (63) plus dot plus table (200) leaves headroom.

**Hash-truncation on overflow.** When a generated name exceeds its limit, the helper truncates and appends a deterministic suffix:

```
{truncated_to_(limit - 17)}_{sha256_hex_first_16_chars(original)}
```

- Hash: SHA-256 of the original (untruncated) name, first 16 hex characters.
- The hash is computed from the full original input so different originals collide only with negligible probability (~1 in 2^64).
- Truncation event is logged at `Information` with both original and hashed name (for traceability when reading Crate logs).

Schema-name overflow is rare in practice (tenant IDs are typically short); the rule applies symmetrically.

### Standard columns (every archive table)

| Column | Type | Notes |
|---|---|---|
| `rtId` | TEXT | Entity instance id |
| `timestamp` | TIMESTAMP WITH TIME ZONE | Datapoint timestamp |
| `ckTypeId` | TEXT | Concrete type (relevant for inheritance) |
| `rtCreationDateTime` | TIMESTAMP WITH TIME ZONE | DEFAULT CURRENT_TIMESTAMP |
| `rtChangedDateTime` | TIMESTAMP WITH TIME ZONE | Updated on conflict |
| `rtWellKnownName` | TEXT | Optional |

Primary key: `(timestamp, rtId, ckTypeId)`.

### Generated archive columns

For each `CkArchiveColumn`:

| Path shape | CrateDB column type | Nullability |
|---|---|---|
| Scalar (e.g. `voltage`) | mapped from CK attribute primitive type | `NOT NULL` if `required`, else nullable |
| Record (e.g. `sensor`) | `OBJECT(STRICT)` with subfields from `CkRecord` | as above |
| Array of scalars (`readings[*].value`) | `ARRAY(<scalar type>)` | `NOT NULL` if `required` (empty/null array allowed; element gaps are `null`) |
| Array of records (`readings[*]`) | `ARRAY(OBJECT(STRICT))` | as above |

**Indexing.** Each generated column is indexed by CrateDB's default rules unless `CkArchiveColumn.indexed = false`, in which case the DDL emits `INDEX OFF` for that column. `indexed` defaults to `true` to match CrateDB's standard behaviour. Indexed-off columns reduce storage and insert cost but force full scans on filters / aggregations against them — set to `false` only for columns that are read but never filtered. PK columns (`timestamp`, `rtId`, `ckTypeId`) are always indexed.

### Upsert semantics

Primary key `(timestamp, rtid, cktypeid)` — on a windowed archive `(window_start, window_end, rtid, cktypeid)`, plus `generation` on a generation-tracked rollup — may collide with existing rows. A batch is written as multi-row statements of up to 1,000 rows each (fewer for an archive so wide that 1,000 rows would exceed the protocol's 65,535 bind parameters); the conflict clause appears once per statement and applies to every row in it:

```sql
INSERT INTO {schema}.{table} ("rtid", "cktypeid", "timestamp", "rtwellknownname", <user columns>)
VALUES ($1, $2, ...), ($n+1, $n+2, ...), ...
ON CONFLICT ("timestamp", "rtid", "cktypeid") DO UPDATE SET
  "rtchangeddatetime"  = CURRENT_TIMESTAMP,
  "rtcreationdatetime" = "rtcreationdatetime",   -- never modified
  "user_col_1"         = EXCLUDED."user_col_1",  -- every user column, required or not
  ...
```

The windowed variant differs in the conflict target, in carrying `"was_updated" = TRUE` instead of the `rtcreationdatetime` no-op (§5: an "ever updated" signal, set on any conflict, not value-change detection), and in writing the flag of a fresh row explicitly:

```sql
INSERT INTO {schema}.{table} ("window_start", "window_end", "rtid", "cktypeid", "rtwellknownname", <user columns>, "was_updated")
VALUES ($1, $2, ..., FALSE), ($n+1, $n+2, ..., FALSE), ...
```

**No column of an upsert relies on a deterministic column `DEFAULT`.** CrateDB before 5.10.12 (5.9.10, 5.9.13 and 5.10.10 were measured) stores the *new* rows of an `INSERT … ON CONFLICT DO UPDATE` that also hits an existing key in the same shard without such defaults — on the **replica** copy only. A flag left to `DEFAULT FALSE` is then missing there: the `NOT NULL` column reads as NULL, `IS NULL` does not find the rows, `GROUP BY` does, and a read returns one answer or the other depending on the copy that serves it. A column named in the INSERT list is not affected, and neither are `CURRENT_TIMESTAMP` defaults (their value is shipped from the primary). The rollup aggregation statement follows the same rule: over a raw source, which has no flag to inherit, it selects `FALSE AS "was_updated"`.

Implications:

- **Last write wins, per row, by default.** Every user column is overwritten unconditionally, so a re-delivery replaces the stored values whatever they were. See *Conflict precedence* below for the opt-in that makes this conditional.
- **`rtwellknownname` is not updated on conflict** — it is written on insert and then left alone. Renaming the source entity does not propagate into existing archive rows.
- **An incoming NULL clears the stored value.** The upsert does not `COALESCE`, so partial multi-source merges (source A writes `voltage`, source B later writes `current` for the same key) do **not** work as a merge: B's insert nulls `voltage` unless it carries it. A source must write the full row it owns.
- **Required-validation runs app-side** (§7) before the SQL is built; the DB `NOT NULL` constraint is the backstop.
- **Idempotent re-inserts** are safe: an identical payload only bumps `rtchangeddatetime` (and `was_updated`).
- **One statement per sub-batch, not one per row.** Every value stays its own typed bind parameter, but CrateDB analyses the statement once instead of once per row. That matters as soon as *Conflict precedence* is on: its guard makes each statement several KB, and as single-row commands CrateDB spent ~0.7 ms per row analysing it — 10,000 rows took 7.4 s, against 0.8 s as multi-row statements (CrateDB 5.10). Two rows with the same key in one statement resolve exactly as two separate writes would: the guard picks the winner, and without a guard the later row wins (verified on CrateDB 5.10).

### Conflict precedence — opt-in ordering (System.StreamData 1.13.0)

Unconditional last-write-wins means the stored value reflects **arrival order**, not the data. Wherever the same key can be written more than once — a corrected meter reading, a re-sent document, a backfill next to a live feed — that is a correctness defect and not just a race: replaying the same set of messages in a different order produces a different archive.

`Archive.ConflictPrecedence` (on the abstract `Archive` base, so raw and time-range archives alike) makes the update conditional. It is an **ordered list of keys**, each naming one of the archive's own user columns plus which direction of it wins:

```yaml
ConflictPrecedence:
  - { Column: DataQuality,        Order: LowerWins }    # rank codes numbered best-first
  - { Column: SourceDocumentDate, Order: HigherWins }   # timestamps, sequence numbers
```

An **empty list is the default and preserves the behaviour above exactly**, so this is a real opt-in — nothing changes for an archive that does not declare it.

**Lexicographic, never conjunctive.** The first key decides; a later one only breaks a tie in every key before it. That is what makes the keys a total order over the data, so the surviving value is its maximum — the same value whichever write lands first. Read as a conjunction ("better rank AND newer"), the result would still depend on arrival order: an older but better-ranked value would lose to a newer worse-ranked one that happened to arrive first, which is exactly the defect being removed. The guarantee covers writes the keys tell apart. Two writes equal in every key are not ordered by them: the last key admits equality, so the later write replaces the stored row and such writes stay arrival-order dependent. That is deliberate — an identical re-delivery stays an idempotent upsert, and an archive whose rows carry no keys behaves as it did before opting in. Declare enough keys to tell apart every pair of writes that can carry different values.

The clause becomes a guard wrapped around every assignment, including the standard ones:

```sql
ON CONFLICT (...) DO UPDATE SET
  "rtchangeddatetime" = CASE WHEN <guard> THEN CURRENT_TIMESTAMP ELSE "rtchangeddatetime" END,
  "was_updated"       = CASE WHEN <guard> THEN TRUE ELSE COALESCE("was_updated", FALSE) END,
  "user_col_1"        = CASE WHEN <guard> THEN EXCLUDED."user_col_1" ELSE "user_col_1" END,
  ...
```

so a losing write leaves **no trace at all** — not even a changed timestamp. The `COALESCE` only matters for a row that was stored without its flag (see *Upsert semantics*): a losing write leaves it as `FALSE` instead of keeping the NULL. `<guard>` for keys `k1 … kn` is

```
(better(k1) OR (equal(k1) AND (better(k2) OR (equal(k2) AND … (better(kn) OR equal(kn))))))
```

Null semantics, which are load-bearing:

| Stored | Incoming | Result |
|---|---|---|
| `NULL` | value | incoming wins — a row that carries no ranking must stay replaceable, or the first write freezes the archive's history |
| value | `NULL` | stored wins — a write that cannot prove it is better must not displace one that can |
| `NULL` | `NULL` | tie, falls through to the next key |

The **last** key admits equality, which is what makes re-delivering an identical document idempotent rather than a no-op that leaves the row looking untouched.

Two constraints:

- Every key must be one of the archive's own user columns, except the reserved key `$Origin` (see *Row origin* below). The guard reads `EXCLUDED."k"`, so a name that is not in the INSERT column list would produce SQL that fails on every single write; `BuildConflictGuard` refuses to build it and names the valid columns instead.
- **Ingest paths only.** `ImportRowsAsync` deliberately does not apply the guard: an operator restoring a snapshot must get back what they gave, not a merge against whatever is in the table.

Adopting this on an **already-activated** archive needs the declared/physical column reconciliation described under *Tenant schema lifecycle* — a new precedence key is usually a newly declared column, and until AB#5247 `CREATE TABLE IF NOT EXISTS` meant such a column never reached CrateDB, which turns the guard into SQL that fails on every write.

### Row origin — manual or automated (AB#5356)

Whether a value was entered by a person or delivered by a system is a fact about the **row**, for every archive and independent of the data model. A domain quality code (the metering data quality L1–L3) then only has to say how good a value is, not where it came from — before, `Basic.Energy/DataQuality` carried `Manual` as a "quality", so a correction could not say "estimated, but entered by hand", and with quality as the first precedence key it ranked last and could not replace anything.

Every raw and time-range table carries the platform column `row_origin TEXT`:

| Stored | Meaning |
|---|---|
| `automated` | delivered by an adapter, a pipeline, an import — the default; a writer that declares nothing writes this |
| `manual` | entered or corrected by a person |
| `NULL` | a row written before the origin was recorded; reads as automated |

A manual row also records who wrote it, when and why (AB#7593):

| Column | Type | Set by |
|---|---|---|
| `manual_written_by` | `TEXT` | the platform, from the authenticated caller (`ManualWrittenBy` on the data point, filled by the asset repository from the token and by the pipeline from its execution identity — never from the payload). A manual point without a writer is refused. |
| `manual_written_at` | `TIMESTAMP WITH TIME ZONE` | the platform clock of the writing repository (`TimeProvider`), one value per write call |
| `manual_reason` | `TEXT` | the writer, optional, stored as given (a solution may require it on top) |

An automated row carries `NULL` in all three, also when the writer sent values, and a write that replaces a manual row with an automated one clears them together with the values. All four columns are guarded together, so the row always describes the write that won.

The writer declares the origin (`StreamDataPoint.Origin` / `TimeRangeStreamDataPoint.Origin`, enum `ArchiveRowOrigin`). The column is nullable without a DEFAULT and always named in the INSERT (AB#6647). Rollup tables do not carry it: their rows are derived. Tables provisioned before AB#5356 get the column through `ALTER TABLE … ADD COLUMN` — at activation, and on the first write or import of each process (`RecomputeProcessState.EnsuredOriginColumns` remembers the table, so `information_schema` is read once per table and process; a concurrent add by another process is the benign "already has a column" case). Archive export reads the columns like every physical column, the import carries them back (an export taken before AB#5356 imports as automated), and the native dump/restore copies the table as it is. The stream data query API selects them by their physical names (`row_origin`, `manual_written_by`, `manual_written_at`, `manual_reason`) on raw and time-range archives; they are not part of the default projection, so existing queries return the same columns as before. They can be filtered, grouped and sorted on like any column (AB#7616); a stored `NULL` origin reads as `automated` on every read path — projection, field filters, grouping, downsampling and time-weighted aggregation all read `row_origin` as `COALESCE("row_origin", 'automated')` (`ArchiveRowOriginColumns.ColumnRef`), because `ALTER TABLE … ADD COLUMN` does not fill existing rows, not even with a DEFAULT. A consumer counts manual and automated intervals per series in one grouped aggregation, e.g. groupBy `[rtId, DataQuality, row_origin]` with `Sum` and `Count`. Rollup archives have no origin: naming `row_origin` on a rollup is refused with the list of valid columns.

**Ranking by origin — the reserved key `$Origin`.** A manual correction wins and stays protected by default (AB#7656): a raw or time-range archive whose `ConflictPrecedence` does not name `$Origin` ranks by it first — with no precedence at all the guard is `$Origin` alone, so automated writes keep last-write-wins between them. Named explicitly, `$Origin` applies at the position the author gave it (keys before it decide first) with `HigherWins` (manual ranks above automated); `Ignore` is the opt-out — the key takes no part, and an archive with no other key has no guard; `LowerWins`, `Ignore` on a column key and naming `$Origin` twice are refused at activation. The `$` cannot occur in an attribute path, so the key never collides with a user column, and it needs no declared column. Rollup archives are derived, carry no origin and use their precedence as configured. Unlike a column key it does not fall through to the next key when both writes are manual:

| Incoming | Stored | Result |
|---|---|---|
| manual | automated (or `NULL`) | the incoming write wins, whatever the following keys say — also against a newer recency key |
| automated | manual | the stored value stays — **a manual value is permanent** |
| manual | manual | the incoming write wins by arrival: a wrong manual value is fixed with another manual write; no quality, recency column or `manual_written_at` is compared |
| automated | automated | the following keys decide, exactly as without `$Origin` |

Rendered for keys `$Origin, k2 … kn`:

```
(EXCLUDED."row_origin" = 'manual' OR (NOT COALESCE("row_origin", 'automated') = 'manual' AND <guard of k2 … kn, or TRUE>))
```

The origin column itself is guarded like every value, so a losing write leaves the stored origin alone. Between automated writes an archive without configured keys still behaves as before: the last write wins, and the row carries the origin of the write that won. The replaced value is not kept; rows stay single-valued. The energy community measurements archive ranks `[$Origin, DataQuality LowerWins, SourceDocumentDate HigherWins]`.

**Rollups over the origin (AB#7727).** A rollup over a raw or time-range archive can aggregate the origin fields although the archive does not declare them (`RollupSourceColumnResolver` rule 1b, `RollupSourceColumnResolutionKind.OriginColumn`; a declared column of the same name still wins). Only these aggregations are accepted; everything else is refused at activation with `RollupOriginAggregationNotSupportedException`, which names the supported ones:

| Aggregation | Bucket value | Column type |
|---|---|---|
| `Count` over `manual_written_at`, `manual_written_by` or `manual_reason` | number of manual rows (`0` when there are none) | `BIGINT` |
| `Min` / `Max` over `manual_written_at` | earliest / latest manual entry, `NULL` without one | `TIMESTAMP WITH TIME ZONE` |

`Count` over `row_origin` is refused: the column is set on every row written since AB#5356, so it would count all rows. Automated rows and rows written before origin tracking carry `NULL` in the manual fields and therefore count as delivered. A rollup over such a rollup names the same logical path (`SourcePath: manual_written_at`) and reads the child through rule 2: counts are summed, `Max` stays the maximum, `Min` the minimum, and `Min`/`Max` stay timestamps at every level. Naming the child's stored timestamp column physically (`manual_written_at_max`) is refused (`RollupOriginTimestampChainException`), because the parent would store it in a numeric column; a physical chain over a stored count stays allowed. A row is stored once per window (the upsert key), so a manual correction replaces the delivered row and is counted once, also after a second correction or a later automated delivery; manual writes reach the rollups through the manual dirty windows (AB#7594), and an existing rollup gets the history of a new origin aggregation through the usual backfill. Computed columns still cannot reference the origin fields (`ComputedColumnValidator` accepts declared columns only). Tests: `RollupValidatorTests`, `RollupSourceColumnResolverTests` (engine), `RollupAggregationPerSourceResolutionTests`, `RollupColumnTypeResolverTests`, `StreamDataOrigin/RollupOriginAggregationIntegrationTests` (real CrateDB).

### Write result — stored and skipped rows (AB#7598)

`IStreamDataRepository.InsertAsync` / `InsertTimeRangeAsync` return a `StreamDataWriteResult`: `Stored` (rows the database holds after the write: new, won, or an identical re-delivery) and `Skipped` (one `StreamDataSkippedRow` per row not stored: entity, window, declared origin, reason) plus `SkippedByReason`. Reasons:

| Reason | When |
|---|---|
| `CkTypeMismatch` | the row's CK type is not the archive's target (was a silent drop before) |
| `LowerPrecedence` | the conflict precedence ranks the stored row above it |
| `ManualValueProtected` | the stored row is manual, the write automated, and the guard ranks by `$Origin` (the default) |

**How the count is exact.** A guarded upsert always "updates" the conflicting row (the `CASE` keeps the stored values), so the affected-row count cannot tell a win from a loss. A guarded statement therefore ends with `RETURNING` the row key, the ranking columns and `row_origin`, and `ConflictOutcome.FindSkipped` compares what CrateDB holds afterwards with what was sent: equal in every ranking column (and the origin) ⇔ stored. That is exact because the last key admits equality — a write equal in every key always wins — and a lost write leaves a stored row that differs in the deciding column or in the origin. Timestamps compare at millisecond precision, numbers by value, text ordinally; two rows for one key in one statement are matched one to one. It holds per multi-row statement and across the sub-batches of a large write. An archive **without** precedence sends no `RETURNING` and reports every row of its target type as stored, as before.

Skipped automated rows are a normal outcome and only reported (and not marked dirty for the rollups: they changed nothing). A **manual** row that cannot be stored — possible only when a column key ranks before `$Origin` or the archive ignores the origin — throws `ManualStreamDataNotAppliedException` after the rest of the batch was written; its `Result` says what was stored. The import path (`ImportRowsAsync`) is unguarded and ignores the result.

### Time semantics

- **Canonical timezone is UTC.** Incoming timestamps with offset are normalized to UTC before insert. Naive `DateTime` values (no offset) are interpreted as UTC, never as local time.
- **API responses** serialize timestamps as ISO-8601 with explicit `Z`, e.g. `2026-04-28T07:30:00Z`.
- **Late-arriving data is allowed.** A point with `timestamp` older than existing rows for the same `(rtId, ckTypeId)` is accepted via the standard upsert path. The application must not assume monotonically increasing timestamps.
- **Future timestamps are allowed.** No validation rejects timestamps `> now()`. Use cases include forecasts, predictions, and tolerated clock drift. Drift detection belongs in the pipeline / monitoring layer, not in StreamData.
- The CrateDB column type is `TIMESTAMP WITH TIME ZONE`; storage is UTC.

### Tenant schema lifecycle

- **Tenant created** → `CREATE SCHEMA IF NOT EXISTS {tenantSchema}`.
- **Tenant detached** (Mongo DB unmounted) → **no Crate operation**. Schema stays in CrateDB so data survives a re-attach. Inserts/queries fail naturally because the tenant context is unavailable.
- **Tenant attached** (Mongo DB re-mounted) → `CREATE SCHEMA IF NOT EXISTS …` is run defensively (idempotent). Existing schema and tables are reused.
- **Tenant hard-deleted** → cascade in this order:
  1. `DROP SCHEMA {tenantSchema} CASCADE` (Crate, idempotent via `IF EXISTS`)
  2. drop the Mongo tenant database (existing flow)
  Caller sees an exception if step 1 fails; step 2 is not attempted, retry is safe.
- Hooked into the existing tenant cleanup path (single point of extension; no new plugin mechanism).
- Startup reconciliation (§11) flags any orphan schema in Crate without a matching Mongo tenant — logged, no automatic drop.
- All archive operations use fully-qualified `{schema}.{table}` names.

---

## 5. Activation Layers

```
Instance enabled?  ── no ──▶ DI does not register CrateDB stack;
        │                   any tenant enable call throws.
       yes
        ▼
Tenant enabled?  ── no ──▶ Archive activate calls throw.
        │
       yes
        ▼
Archive status = Activated?  ── no ──▶ Insert/query call throws.
        │
       yes
        ▼
       OK
```

- **Instance flag**: new appsettings section `StreamData:Enabled` (default `false`).
- **Tenant flag**: existing `StreamDataGlobalSettings.IsEnabled`.
- **Archive status**: per-instance via `CkArchive.status`.

### Authorization roles

Three new roles, added to the existing `Roles` enum (backend + studio):

| Role | Allowed operations |
|---|---|
| `StreamDataAdmin` | List/Get/Create/Update/Delete CkArchive; Activate / Disable / Enable / RetryActivation; Tenant-Enable/Disable; `GET /streamdata/status`. |
| `StreamDataWriter` | Insert into activated archives (`InsertAsync`); `GET /streamdata/status`; List/Get CkArchive (metadata read needed to resolve `archiveRtId`). **No CRUD on CkArchive, no lifecycle.** |
| `StreamDataReader` | Time-series queries (`ExecuteQueryAsync`, aggregation, grouped, downsampling); `GET /streamdata/status`; List/Get CkArchive (metadata). **No insert, no lifecycle, no CRUD.** |
| `AdminPanelManagement` | Implies all three (backwards-compat / global admin role). |

Mapping to API surface:

- **GraphQL custom mutations** (T19): `[Authorize(Roles = nameof(Roles.StreamDataAdmin) + "," + nameof(Roles.AdminPanelManagement))]`.
- **GraphQL CkArchive CRUD**: `Create/Update/Delete` → admin roles; `List/Get` → all three.
- **GraphQL time-series query** (`StreamDataQuery`/`StreamDataTransientQuery`): reader+admin.
- **GraphQL `availableArchivePaths`**: admin (used by archive create/edit flow).
- **REST `/streamdata/enable|disable`**: admin only.
- **REST `/streamdata/status`**: any authenticated role (reader+writer+admin).
- **`SaveStreamDataInArchive` pipeline node**: runs under a service account that holds `StreamDataWriter`.

Authorization is enforced **server-side**. The studio uses route-level guards only (no component-level role checks in this iteration — see §17). Buttons may appear for users without write rights; if clicked, the server returns the corresponding `ArchiveNotActivatedException`/`Forbidden` and the studio surfaces it as a notification.

---

## 6. API Changes

### `IStreamDataRepository`

The repository combines the data plane (insert/query) and the per-archive control plane (table provisioning). This mirrors the existing `EnsureDatabaseCreatedAsync` / `DeleteDatabaseAsync` pattern and keeps the abstraction count low (no separate `IArchiveBackend`).

```csharp
// Data plane
Task InsertAsync(OctoObjectId archiveRtId, StreamDataPoint point);
Task InsertAsync(OctoObjectId archiveRtId, IEnumerable<StreamDataPoint> points);
Task<StreamDataQueryResult> ExecuteQueryAsync(
    OctoObjectId archiveRtId, StreamDataQueryOptions options);
Task<StreamDataQueryResult> ExecuteAggregationQueryAsync(
    OctoObjectId archiveRtId, StreamDataAggregationQueryOptions options);
Task<StreamDataQueryResult> ExecuteGroupedAggregationQueryAsync(
    OctoObjectId archiveRtId, StreamDataGroupedAggregationQueryOptions options);
Task<StreamDataQueryResult> ExecuteDownsamplingQueryAsync(
    OctoObjectId archiveRtId, StreamDataDownsamplingQueryOptions options);

// Per-archive control plane (called by IArchiveLifecycleService)
Task EnsureArchiveCreatedAsync(CkArchive archive);
Task DeleteArchiveAsync(CkArchive archive);
```

The repository resolves `archiveRtId` → `CkArchive` entity → table; verifies `Status == Activated`; otherwise throws `ArchiveNotActivatedException`.

#### Column-name validation (AB#4765)

Every one of the four query methods validates its column names against the archive's
`StreamDataFieldResolver` **before any SQL is built**, and rejects the query naming each offending
name together with the valid ones — projection, aggregation, group-by, sort and filter alike.

**Rollups widen the aggregation universe.** On a rollup an aggregation column may name a *logical* CK
source path that is deliberately not a column of the rollup table — a TWA rollup stores
`{col}_twavg_integral` / `{col}_twavg_duration` while the query asks to aggregate `Voltage`, and the
chain-aware resolver rewrites that into an expression over the stored pair, in a branch that runs
*before* the field resolver is consulted. The three aggregating paths therefore validate against a
resolver widened with the rollup's logical paths (`RollupLogicalPathResolver`), mirroring what the
GraphQL layer already does. It is a **separate** resolver from the one that builds SQL: widening that
one would let a chain path the chain resolver *declined* resolve to a physical column that does not
exist, turning a clear rejection into a CrateDB column-not-found error. Where the rollup store is not
wired, the narrow resolver is used — the same degradation the chain resolver itself takes.

Only names, not values: a filter whose operator needs a comparison value but carries none is still
skipped further down. That is deliberate — the GraphQL layer excludes exactly those filters from its
own validation, which reads as a tolerated no-op for half-filled filter rows coming from the query
builder, so rejecting them here would turn an unfinished row in the UI into an error.

This used to be the opposite: an unresolvable name was dropped without a trace, and what that cost
depended on where it sat. A mistyped sort returned rows in storage order, so the sort looked broken.
A mistyped filter returned **more** rows than requested — a typo silently turned a narrow query into
a full read. A dropped group-by column merged groups that should have stayed apart, which reads as
fewer rows with larger numbers rather than as an error. A dropped aggregation left its key figure
missing altogether. All four produce a plausible-looking result, which is what made the silence
dangerous.

Names are matched case-insensitively but **not** separator-insensitively, so the standard columns
must be written in their physical spelling: `window_start`, not `WindowStart`. Computed columns are
addressed by their logical `Name` — their physical column is versioned after a formula change and is
registered on the resolver rather than derived (AB#4764).

### Bulk insert semantics

`InsertAsync(OctoObjectId archiveRtId, IEnumerable<StreamDataPoint> points)` follows an **all-or-nothing pre-validation** model:

1. Resolve archive (single lookup), status check.
2. **Pre-validate every point in the batch** before any SQL is sent: required-path coverage, type compatibility, path-resolution. Iteration stops at the first violation.
3. On any violation → throw `RequiredAttributeMissingException` (or appropriate §12 exception) carrying the **index of the offending point**. No row is written.
4. On full validation success → build a single multi-row `INSERT … VALUES (...), (...) … ON CONFLICT …` and execute.

Rationale:
- Matches D7 (required violations are exceptions, not silent skips).
- Pipeline callers see batch success or full failure — no silent partial commits.
- CrateDB's multi-row insert is not transactionally atomic; pre-validation guarantees no partial commit can come from validation problems. A DB-level failure after pre-validation (e.g. transient connection error) is surfaced as exception; partial server-state is theoretically possible but is treated as an operational issue (see §11 reconciliation, T13 resilience).

Future extension (not in this iteration): `BulkInsertOptions { ContinueOnError: bool }` to opt into best-effort mode that returns `{ successCount, errors[] }` instead of throwing.

### Pipeline node

`SaveStreamDataInArchive` gains a required property `ArchiveRtId : OctoObjectId`.

### GraphQL

**Time-series queries** — both `StreamDataQuery` and `StreamDataTransientQuery` gain a required `archiveRtId: ID!` argument.

**CkArchive CRUD** — auto-generated from the CkArchive CK type (entity API). No custom resolvers needed. The validation hook (§10) enforces immutability on `update`. `delete` is a soft-delete that triggers `IArchiveLifecycleService.DeleteAsync` (drops the table).

**State transitions** — custom mutations (CRUD cannot express these cleanly):

```graphql
mutation activateArchive(rtId: ID!): CkArchive!
mutation disableArchive(rtId: ID!): CkArchive!     # Activated → Disabled
mutation enableArchive(rtId: ID!): CkArchive!      # Disabled → Activated
mutation retryArchiveActivation(rtId: ID!): CkArchive!   # Failed → Activated
```

Each returns the updated CkArchive (for studio optimistic updates). Auth: `Roles.AdminPanelManagement`. Errors mapped from §12 exceptions to GraphQL error extensions with stable error codes.

**Path enumeration** — query for the studio's path picker:

```graphql
query availableArchivePaths(ckTypeId: ID!, maxDepth: Int = 5): [ArchivePathInfo!]!

type ArchivePathInfo {
  path: String!                  # "sensor.reading.value", "readings[*].value"
  primitiveType: String          # null when isRecord = true
  isRecord: Boolean!
  isArray: Boolean!
  recordTypeId: String           # populated when isRecord = true
  inheritedFromCkTypeId: String  # null if attribute belongs to ckTypeId itself
}
```

Implementation: new resolver in `octo-asset-repo-services`, walks the CK model (record traversal up to `maxDepth`).

### REST

`StreamDataController` keeps the existing tenant-level enable/disable endpoints (`POST /streamdata/enable`, `POST /streamdata/disable`). They are kept on REST for bootstrap reasons: a tenant must be able to opt into StreamData before any GraphQL schema is exposed.

**New endpoint** — status query the studio uses to decide whether to render the archives feature at all:

```
GET /streamdata/status
```

Response:

```json
{
  "instanceEnabled": true,    // from appsettings StreamData:Enabled
  "tenantEnabled": true       // from StreamDataGlobalSettings.IsEnabled for current tenant
}
```

If `instanceEnabled` is false the studio hides the archives navigation entirely. If `instanceEnabled` is true but `tenantEnabled` is false the studio shows an empty state with an enable action (using the existing `POST /streamdata/enable`).

Archive lifecycle (activate/disable/delete) is **not** exposed via REST — only GraphQL.

---

## 7. Code Organization

### `octo-construction-kit-engine` (DB-agnostic)

Move/add:

- `Runtime.Contracts/StreamData/IStreamDataRepository` (already there; signature + lifecycle methods updated)
- `Runtime.Contracts/StreamData/*` DTOs (already there; `SdEntity` renamed to `StreamDataEntity` for naming consistency)
- New: `Runtime.Contracts/StreamData/IArchiveLifecycleService` — DB-neutral state machine
- New: `Runtime.Contracts/StreamData/IArchiveSchemaResolver` — path → column metadata, DB-neutral
- New: `Runtime.Contracts/StreamData/StreamDataException` and subclasses (see §11)
- New: `Runtime.Engine/StreamData/ArchiveLifecycleService` — implementation
- New: `Runtime.Contracts/CkIdExtensions` (or equivalent) — promote the `RtCkId.SemanticVersionedFullName` cleaning helper from `internal` (mongodb repo) to a shared, public helper

### `octo-construction-kit-engine/src/StreamDataCkModel/` — new project

Parallel to the existing `SystemCkModel/` project:

```
src/StreamDataCkModel/
└── ConstructionKit/
    ├── enums/
    │   └── ck-archive-status.yaml
    ├── records/
    │   └── ck-archive-column.yaml
    ├── types/
    │   └── ck-archive.yaml
    └── migrations/
        └── migration-meta.yaml
```

YAML files use kebab-case; `typeId` PascalCase. The model is loaded only when `StreamData:Enabled` (instance flag) is true.

### `octo-construction-kit-engine-mongodb` — new project layout

```
src/
├── Runtime.Engine.MongoDb/        (unchanged: only MongoDB)
├── Runtime.Engine.CrateDb/        (NEW)
│   ├── Schema/                    (DDL generation, tenant-schema management)
│   ├── Lifecycle/                 (CkArchive backend hooks)
│   ├── Client/                    (CrateDatabaseClient, ClientAccess — moved)
│   ├── QueryBuilder/              (CrateQueryBuilder, Compiler — moved)
│   ├── Dapper/                    (TypeHandlers, possibly slimmed down)
│   ├── CrateDbStreamDataRepository.cs
│   └── Configuration/             (DI extensions)
└── Runtime.Engine.Composition/    (NEW, thin: TenantContext binding both)
```

`Runtime.Engine.MongoDb.csproj` keeps no CrateDB references.

---

## 8. Implementation Tasks

Tasks are ordered for sequential execution. Each task is self-contained with acceptance criteria.

### Phase 1 — Foundations

#### [x] T1. Define `StreamData` CK model
- Create new CK model project `StreamData` (YAML + generated services).
- Define `CkArchiveStatus` enum, `CkArchiveColumn` record, `CkArchive` type as in §3.
- Embed migration metadata (initial v1.0.0).
- **Acceptance**: model compiles via existing CK compiler, source-generated service classes available, model can be imported into a tenant.

#### [x] T2. Move DB-agnostic StreamData artifacts to `octo-construction-kit-engine`
- Move `RtCkId` cleaning helper (today `internal` in `CkIdExtensions`) to a public namespace in `Runtime.Contracts` (or similar).
- Update `IStreamDataRepository` signatures to accept `OctoObjectId archiveRtId` (will compile-fail on the mongodb repo until T7 lands — acceptable; track via temporary stubs if needed).
- Add new contracts: `IArchiveLifecycleService`, `IArchiveSchemaResolver`, `IArchiveBackend`.
- **Acceptance**: `octo-construction-kit-engine` builds; new contracts in place; existing engine tests green.

#### [x] T3. Extract CrateDB code into `Runtime.Engine.CrateDb` project
- Create new `.csproj` under `octo-construction-kit-engine-mongodb/src/Runtime.Engine.CrateDb`.
- Move `StreamData/` content from `Runtime.Engine.MongoDb` to new project (Client/, QueryBuilder/, Dapper/, repository, configuration).
- Ensure `Runtime.Engine.MongoDb.csproj` no longer references CrateDB code.
- Add new thin `Runtime.Engine.Composition` project (or extend existing TenantContext slice) to compose Mongo + Crate.
- Update solution file.
- **Acceptance**: solution builds; existing integration tests still green; `Runtime.Engine.MongoDb` compiles without CrateDB references.

### Phase 2 — Core infrastructure

#### [x] T4. Schema-per-tenant in CrateDB
- DDL: `CREATE SCHEMA {tenantSchema}` on tenant create; `DROP SCHEMA … CASCADE` on delete.
- All archive operations use `{schema}.{table}` qualified names.
- **Acceptance**: integration test creates two tenants, verifies isolation (each tenant sees only own tables); tenant deletion removes schema.

#### [x] T5. Archive DDL generator
- Implement `IArchiveBackend` for CrateDB: produces `CREATE TABLE` from a `CkArchive` instance + CkType metadata.
- Standard columns + per-`CkArchiveColumn` typed columns, with nullability per `required`.
- Quoted identifier with dots for nested paths; verify against OBJECT subscripting collision.
- Record paths → OBJECT(STRICT) with subfields from `CkRecord`.
- Array paths → `ARRAY(...)`.
- **Acceptance**: unit tests cover scalar, record, array-of-scalar, array-of-record, required vs optional combinations.

#### [x] T6. Archive lifecycle service
- Implement state machine in `IArchiveLifecycleService` (DB-neutral) with hooks into `IArchiveBackend`.
- Transitions: Created→Activated (DDL), Activated↔Disabled (status only), *→Failed on activation error, Failed→Activated (retry DDL), Delete (DROP + rtState=Archived).
- Validation hook on `CkArchive` updates: reject schema changes after activation.
- **Acceptance**: unit tests cover all transitions + immutability rejection; integration test full happy-path.

#### [x] T7. Update `IStreamDataRepository` signatures
- All methods take `OctoObjectId archiveRtId` first.
- Repository resolves `archiveRtId` → CkArchive → tenant schema + table.
- Throws `ArchiveNotActivatedException` if status ≠ Activated.
- Required-validation: scalar missing → exception; array element missing leaf → null in array.
- **Acceptance**: existing integration tests adapted, all green; new tests for unactivated/disabled/required-violation cases.

### Phase 3 — Integration

#### [x] T8. Three-tier activation
- Add `StreamData:Enabled` to appsettings (instance-level).
- DI registration of CrateDB stack guarded by instance flag.
- `EnableStreamDataAsync` (tenant) throws if instance flag false.
- Archive activate throws if tenant flag false.
- **Acceptance**: integration test toggles each level and verifies expected exceptions/successes.

#### [x] T9. `SaveStreamDataInArchive` pipeline node
- Add required `ArchiveRtId : OctoObjectId` property.
- Insert call routes through new repository signature.
- **Acceptance**: pipeline integration test inserts to a specific archive; targeting an inactive archive raises a clear pipeline error.

#### [x] T10. GraphQL & REST API updates
- GraphQL: `archiveRtId: ID!` required on `StreamDataQuery` and `StreamDataTransientQuery`.
- SDK client (`octo-sdk`) updated.
- REST: tenant enable/disable unchanged; CkArchive lifecycle via standard entity CRUD.
- **Acceptance**: SDK contract tests + GraphQL schema test pass; REST happy-path E2E green.

#### [x] T11. Hard cut of legacy StreamData
- Remove old generic `OBJECT(DYNAMIC)` table and codepaths.
- Document in release notes that legacy data is discarded.
- Deployment runbook: drop legacy tables in target environments.
- **Acceptance**: codebase grep shows no `OBJECT(DYNAMIC)` legacy references; release-notes entry committed.

### Phase 4 — Pre-existing pain points (validate against new code)

#### [x] T12. Connection pooling rework
Existing `Pooling=false` + `NoResetOnClose=true` workaround for ROLLBACK incompatibility. Evaluate whether new Npgsql/CrateDB combo allows pooling, or implement own pool with manual lifecycle.

#### [x] T13. Resilience policies
Polly-based pipeline (retry with backoff, circuit breaker, timeout) around `CrateDatabaseClient` operations.

#### [x] T14. Tenant connection cache review
With schema-per-tenant the per-tenant connection cache may simplify or disappear. Decide based on T4/T12 outcome.

#### [x] T15. REFRESH TABLE strategy
Decide between: explicit refresh after bulk-insert in production code, query-side tolerance, or write-through cache. Document chosen pattern.

#### [x] T16. Dapper TypeHandler review
With typed columns, most JSON handlers are unnecessary. Keep only `CkId`/`OctoId` handlers. Evaluate source-generator alternative.

#### [x] T17. Retention & downsampling per archive
`IStreamDataRepository.ExecuteDownsamplingQueryAsync` exists but no retention policy. Define per-archive retention (TTL on data, optional auto-rollup) — design first, implement later.

---

## 9. Naming Alignment

The new code follows existing conventions in `Runtime.Contracts` / `Runtime.Engine`:

| Concept | Convention | Example |
|---|---|---|
| Interface | `I[Name]Service` / `I[Name]Repository` / `I[Name]Resolver` | `IArchiveLifecycleService`, `IArchiveSchemaResolver`, `IStreamDataRepository` |
| Implementation | Interface name without `I`, no `Impl` suffix, often `internal` | `ArchiveLifecycleService` |
| Exceptions | `[Domain]Exception` (no `Octo`/`Mm` prefix) | `StreamDataException`, `ArchiveNotActivatedException` |
| CK DTOs | `Ck[Name]Dto` in `ConstructionKit.Contracts/DataTransferObjects/` | n/a — runtime entities used here |
| Runtime entities | `Rt[Name]` / domain prefix, no `Dto` suffix | `StreamDataPoint`, `StreamDataRow`, `CkArchive` |
| DI extension | `IRuntimeEngineBuilder Add[Subsystem](this IServiceCollection)` returning the builder for fluent chaining | `AddStreamData()` |
| CK model project | `src/[Name]CkModel/ConstructionKit/{types,records,enums,migrations}/` | `src/StreamDataCkModel/` |
| YAML files | kebab-case file names, PascalCase `typeId` inside | `ck-archive.yaml` → `typeId: CkArchive` |

The legacy class `SdEntity` is renamed to `StreamDataEntity` to align with the `StreamData…` family of names.

---

## 10. Validation Hooks (CkArchive immutability)

Schema mutation on an activated archive must be rejected before it reaches storage.

- **Where**: validation hook on the `CkArchive` RtEntity update path. Implementation choice deferred to the standard CK entity validation pipeline (verify in T6 implementation whether a per-type pre-update hook exists; if not, add one in the lifecycle service via the repository's update path).
- **What is rejected** when `status ∈ { Activated, Disabled, Failed }`:
  - any change to `targetCkTypeId`
  - any change to `columns` (count, order, path, required flag)
- **What is allowed** in any state:
  - `status` transitions per the state machine
  - `rtState` transitions (including soft-delete)
- **Bootstrapping**: while `status = Created`, the archive is fully editable.

Validation errors throw `ArchiveSchemaImmutableException` (see §11) and are surfaced via the standard entity-update error path (GraphQL/REST).

### Path validation at activation

Every transition that ends in `Activated` re-validates all `CkArchiveColumn.path` entries against the **current** CK model:

- `Created → Activated`
- `Disabled → Activated`
- `Failed → Activated` (retry)

Validation walks each path against `targetCkTypeId` and any nested `CkRecord`s using the same logic as the DDL generator (T5). Failures throw `ArchivePathInvalidException` (§12) before any DDL runs; status remains in the previous state.

**Activated archives are not re-validated on CK-model migration.** A breaking change to the CK model shows up at insert/query time as a runtime path-resolution error. Comprehensive migration-time validation is deferred — see §17.

---

## 11. Concurrency

Concrete races to handle:

| Scenario | Behavior |
|---|---|
| Two parallel `Activate` calls on the same archive | Serialized via a per-archive distributed lock (reuse `RepositoryDistributedLockService` / equivalent). Second call observes `Activated` and returns idempotently. |
| `Disable` while inserts are in flight | In-flight inserts complete (no abort). New inserts after status flip → `ArchiveNotActivatedException`. Reads behave the same way. No partial-batch handling. |
| `Delete` while inserts are in flight | Same as Disable: in-flight inserts complete, but `DROP TABLE` may fail if a transaction holds the table — retry once after a short backoff, then surface the error. |
| Activate fails mid-DDL | Status moves to `Failed`. Partial table state must be cleaned up before retry: `EnsureArchiveCreatedAsync` is idempotent (`CREATE TABLE IF NOT EXISTS`) so retry is safe. |
| Multiple service instances importing the same CK model | Existing CK migration locking applies; the StreamData model has no special requirements beyond that. |
| Bulk insert on disabled archive | Whole batch rejected on the first lookup (status check happens once per call), `ArchiveNotActivatedException`. |

Status is stored on the `CkArchive` entity; reads use the same caching/consistency rules as other RtEntity reads.

### Cross-store consistency (Mongo ↔ Crate)

CkArchive entities live in MongoDB; archive tables live in CrateDB. There is no shared transaction. The following rules keep the two stores reconcilable without 2PC:

1. **Idempotent Crate DDL** — all DDL uses `IF NOT EXISTS` / `IF EXISTS`, so any retry is safe.
2. **Operation order** — Crate first, Mongo last. Activate: `CREATE TABLE` → `status = Activated`. Delete: `DROP TABLE` → `rtState = Archived`. Mongo is the source of truth for status; only commit the Mongo write once Crate has reached the desired state.
3. **Failure handling** — if step 2 (Mongo) fails after step 1 (Crate) succeeded, the operation throws and the caller retries. Step 1 is a no-op on retry (idempotent). Status remains in its previous state until Mongo confirms.
4. **Status-first check** — every insert/query verifies `status == Activated` before touching Crate. While step 1 has run but step 2 has not, the API still rejects writes/reads with `ArchiveNotActivatedException`. No partial data corruption is possible.
5. **Startup reconciliation** — on engine startup, a one-shot job compares Mongo state to Crate reality:

   | Found | Action |
   |---|---|
   | `status = Activated` in Mongo, table missing in Crate | Set `status = Failed`, log warning. |
   | `status ∈ {Created, Disabled, Failed}` in Mongo, matching table exists in Crate | Log info. Leave table in place (do not drop without explicit user action). |
   | Table in Crate without matching CkArchive entity | Log warning. No automatic drop. |

   Disable / Enable transitions are pure Mongo updates with no Crate side-effect — not subject to drift.

The reconciliation job runs once per service start, scoped per tenant via `ITenantContext`. Periodic runs are not needed; startup catches the vast majority of drift, and idempotent retries handle the rest at request time.

---

## 12. Exception Catalog

All in `Runtime.Contracts/StreamData/`. Common base: `StreamDataException` (extends `RuntimeRepositoryException`).

| Exception | Thrown when |
|---|---|
| `StreamDataException` | Base; generic StreamData failure. |
| `StreamDataNotEnabledException` | Instance or tenant flag is false on an operation that requires it. |
| `ArchiveNotFoundException` | `archiveRtId` does not resolve to a `CkArchive` entity. |
| `ArchiveNotActivatedException` | Archive exists but `status ≠ Activated` on insert/query. |
| `ArchiveSchemaImmutableException` | Update on `CkArchive` after activation tries to change schema-relevant fields. |
| `ArchivePathInvalidException` | A `CkArchiveColumn.path` cannot be resolved against the `targetCkTypeId` (unknown attribute, broken record traversal, illegal array indexing). |
| `ArchiveColumnTypeUnsupportedException` | An attribute type cannot be mapped to a CrateDB column type. |
| `RequiredAttributeMissingException` | Insert lacks a value for a `required: true` scalar path. |
| `ArchiveActivationFailedException` | DDL execution failed during `Created → Activated` (transient or schema error). Wraps the underlying SQL error. |
| `StreamDataException` (base, from `StreamDataQueryColumnValidator`) | A query names a column the archive's field resolver cannot resolve — in projection, aggregation, group-by, sort or filter (AB#4765). Thrown before any SQL runs. Deliberately the **contracts** base type rather than the connection-level `StreamDataException` local to `Runtime.Engine.CrateDb`, so one `catch` covers it together with `ArchiveNotFoundException` / `ArchiveNotActivatedException`, which the same query methods throw. No consumer distinguishes the two types today, so this is about catchability, not the response code. Note the remaining inconsistency: the `InvalidQueryParameters` guards in the same methods (missing `From`/`To`/`Limit`, `StateDuration` without a comparison value) still throw the **local** type, so "invalid query input" cannot be caught uniformly. |

Each exception carries the offending `archiveRtId` (where applicable) and a stable error code string for client-side handling.

---

## 13. Observability

### Metrics (OpenTelemetry counters/histograms)

| Metric | Type | Tags |
|---|---|---|
| `streamdata.archive.count` | Gauge | `tenant`, `status` |
| `streamdata.archive.status_transitions` | Counter | `tenant`, `archive`, `from`, `to` |
| `streamdata.archive.activation_duration_ms` | Histogram | `tenant`, `archive`, `outcome` |
| `streamdata.insert.duration_ms` | Histogram | `tenant`, `archive`, `batch_size_bucket` |
| `streamdata.insert.points` | Counter | `tenant`, `archive` |
| `streamdata.insert.required_violations` | Counter | `tenant`, `archive` |
| `streamdata.query.duration_ms` | Histogram | `tenant`, `archive`, `query_type` |
| `streamdata.query.rows_returned` | Histogram | `tenant`, `archive`, `query_type` |
| `streamdata.crate.connections.open` | Gauge | (instance-wide) |

### Logs

- All exceptions in §12 logged at `Warning` (client errors: missing/invalid archive, required violations) or `Error` (DDL failure, Crate unreachable).
- Status transitions logged at `Information` with archive RtId, tenant, from/to.
- DDL statements logged at `Debug`.

### Traces

Activity sources:
- `Meshmakers.Octo.StreamData` — top-level operations (`Insert`, `Query`, `Activate`, `Delete`).
- `Meshmakers.Octo.StreamData.Crate` — DB calls (`CrateDatabaseClient.ExecuteAsync`, etc.).

Span attributes: `streamdata.archive.rtid`, `streamdata.archive.target_cktype`, `streamdata.tenant`. Required-violation cases set `otel.status_code = ERROR` with the path that failed.

---

## 14. Status-transition Events (Audit Trail)

Status changes are audit-trailed via the existing event infrastructure in `octo-common-services/src/Notifications` rather than an inline history list on `CkArchive`. Reuses what is already in the platform (persistent, tenant-scoped, queryable as RtEntities, cross-cluster-friendly via DistributionEventHub).

### Emission

Every transition emits one event through `IEventRepository`:

| Transition | Level | Source |
|---|---|---|
| Created → Activated | Information | `StreamData` |
| Activated → Disabled | Information | `StreamData` |
| Disabled → Activated | Information | `StreamData` |
| any → Failed | Warning | `StreamData` |
| Failed → Activated (retry) | Information | `StreamData` |
| Delete (any state) | Information | `StreamData` |

```csharp
await eventRepository.StoreInformationEvent(
    tenantId,
    source: RtEventSourcesEnum.StreamData,            // new enum value, see below
    message: $"Archive '{archive.RtWellKnownName}' transitioned from {fromStatus} to {toStatus}",
    associatedRtEntityId: archive.RtId);
```

For `Failed`: include the underlying error code in the message; for retries: include the previous failure reason for context.

### CK-model change required

`RtEventSourcesEnum` (today defined in the `System.Notification.v2` CK model) gains a new value `StreamData`. This is a small, additive migration of an existing CK model — no new CK model needed. Tracked as part of T1 (StreamData CK model definition).

### Triggering user

`triggeredBy` is encoded in the event message: the JWT subject claim from the inbound request is appended as `... by {userId}`. Auto-transitions (e.g. reconciliation flagging an archive as Failed) use `system:reconciliation` instead. No structural change to RtEvent needed.

### Query / Studio access

`RtEvent` entities are standard RtEntities and already queryable via the entity API. The studio's `ArchiveDetailsComponent` (§16) gets a "History" tab that queries:

```graphql
query archiveStatusHistory($archiveRtId: ID!) {
  rtEvents(
    filter: { associatedRtEntityId: $archiveRtId, source: "StreamData" }
    orderBy: { rtCreationDateTime: DESC }
    first: 50
  ) {
    items { rtCreationDateTime, level, message }
  }
}
```

(Exact query shape depends on the existing event GraphQL exposure — verify during implementation.)

### Retention

No StreamData-specific retention. Events are kept per the platform-wide event retention rules.

---

## 15. Test Strategy

| Layer | What is tested | Where |
|---|---|---|
| Unit | DDL generator (T5): each path shape × required-flag combination produces correct SQL. | `Runtime.Engine.CrateDb.UnitTests/` |
| Unit | State machine (T6): all legal transitions, all illegal transitions rejected, immutability validation. | same |
| Unit | Schema resolver: path → column metadata for scalar/record/array shapes. | same |
| Unit | Required-validation: scalar missing → exception, array element missing leaf → null. | same |
| Integration | End-to-end archive lifecycle against a real CrateDB Testcontainer (Created → Activated → Insert → Query → Disable → Activate → Delete). | `Runtime.Engine.CrateDb.IntegrationTests/` |
| Integration | Schema-per-tenant isolation: two tenants with same archive ID don't see each other's data. | same |
| Integration | Three-tier activation: each flag toggled, expected exception class observed. | `AssetRepositoryServices.IntegrationTests/StreamData/` |
| Integration | Bulk insert with mixed valid/required-violation points: whole batch rejected. | same |
| Integration | Concurrency: parallel Activate idempotent; Disable mid-insert; Delete mid-insert. | same |
| E2E | GraphQL `streamData` query and `SaveStreamDataInArchive` pipeline node against a configured asset-repo service. | `AssetRepositoryServices.IntegrationTests/` |

Existing fixture (`StreamDataFixture`) is the starting point — adapt to the archive model and split per scope. The fixture already uses `crate:6.4.6` Testcontainers.

---

## 16. Refinery Studio UI

Lives in `octo-frontend-refinery-studio/src/octo-mesh-refinery-studio`. Follows the existing list/detail pattern (`appMyDataSource` directive + `ListViewComponent`, `*-details.component.ts` for routed detail pages). All backend calls go through Apollo Angular with code generation from the GraphQL schema (see §6).

### Navigation

```
/:tenantId/repository/archives                  → ArchivesListComponent
/:tenantId/repository/archives/create           → ArchiveCreateComponent
/:tenantId/repository/archives/details/:id      → ArchiveDetailsComponent
```

Route guard: `authorizeChildGuard` with `roles: [Roles.AdminPanelManagement]`. The whole `archives` route subtree is registered only when the studio's startup probe (`GET /streamdata/status`) returns `instanceEnabled: true`.

### Components

| Component | Purpose |
|---|---|
| `ArchivesListComponent` | Tabular list of CkArchive entities for the current tenant. Columns: name (`rtWellKnownName`), `targetCkTypeId`, status badge, column count, action menu (Activate / Disable / Enable / Retry / Delete). |
| `ArchiveCreateComponent` | Wizard-style form: pick `targetCkTypeId` → use `AttributePathPickerComponent` to select columns → save (status defaults to `Created`). |
| `ArchiveDetailsComponent` | View/edit CkArchive. Form fields editable only when `status = Created`; otherwise read-only with a banner explaining the immutability rule. State-transition buttons enabled per current status. |
| `ArchiveStatusBadgeComponent` | Reusable: colored badge per `CkArchiveStatus` value. Tooltip shows last transition time and (for `Failed`) the underlying error code. |
| `AttributePathPickerComponent` | New reusable component (see below). |

### `AttributePathPickerComponent`

Tree view, not flat list. Reasons: nested records can produce dozens of paths and a tree mirrors the CK model the user already has in mind.

- **Source**: `availableArchivePaths(ckTypeId)` GraphQL query (§6).
- **Tree nodes**:
  - Leaf node = scalar attribute (selectable, with required toggle).
  - Branch node = record attribute (expandable; selecting the branch itself stores the whole record as `OBJECT(STRICT)`).
  - Array marker `[*]` shown inline on attribute name when `isArray = true`.
- **Per-row controls**: `required` toggle and `indexed` toggle (both default `true`). Tooltip on `indexed`: "Indexes speed up filtering and aggregation on this column at the cost of storage and insert performance. Disable only for columns that are read but never filtered."
- **Output**: `{ path: string, required: boolean, indexed: boolean }[]` bound to the parent form.
- **Validation**: at least one column required (matches CkArchive validation).

### State-transition buttons (per current status)

| Status | Visible actions |
|---|---|
| Created | Activate, Edit, Delete |
| Activated | Disable, Delete (no edit) |
| Disabled | Enable, Delete |
| Failed | Retry Activate, Delete |

Confirmation dialog (existing pattern in studio) for Activate, Delete, Disable. After each action, the response from the GraphQL mutation drives an optimistic update of the cached entity.

### Tenant-level enable

`ArchivesListComponent` queries `GET /streamdata/status` on init. If `tenantEnabled = false`, render an empty state with a single "Enable StreamData for this tenant" button calling `POST /streamdata/enable`. After success, reload the page (or refetch the archive list).

Studio currently has no REST client; introduce a thin one for `/streamdata/*` endpoints under `app/services/streamdata-status.service.ts`.

### Code generation

Add new `*.graphql` files under `src/app/graphQL/`:
- `listArchives.graphql`, `getArchive.graphql`
- `activateArchive.graphql`, `disableArchive.graphql`, `enableArchive.graphql`, `retryArchiveActivation.graphql`
- `availableArchivePaths.graphql`

`graphql-codegen` produces the Angular service methods used by the components.

---

## 17. Out of Scope (this iteration)

- Multi-region/cross-cluster CrateDB deployments.
- Automatic migration of legacy data (hard cut per D11).
- Archive sharing across tenants (each tenant owns its archives).
- **Per-archive ACL** (e.g. "user X may read only archive Y"). Tenant-wide reader role is sufficient for the foreseeable use cases. Revisit if a customer requires it.
- **Component-level auth in the studio** — buttons gate by route guard only; server enforces roles. Revisit when/if read-only studio mode is needed for partner integrations.
- **GraphQL subscriptions** for live time-series streaming.
- **Best-effort bulk insert** with `BulkInsertOptions { ContinueOnError: true }` (only strict mode in this iteration; see §6).

---

## 18. Deferred / Open Items (re-confirm during implementation)

### Migration-time archive validation (deferred)

Comprehensive validation of existing archives against an incoming CK-model migration is **out of scope for this iteration**. When requirements arrive, the following decisions stand:

- **Strict mode only.** No `force` flag. A migration that would invalidate any active archive must fail and require admin action (delete or replace the archive first).
- **No rename detection.** Every renamed attribute is a hard break; archives reference the old path and will not be auto-rewritten.
- **Activated archives only validated lazily** at insert/query time, plus full re-validation at activation (§10). Disabled and Failed archives are not validated during migration.

Until implemented, breakage manifests as runtime exceptions on the data plane.

### Other open items

- D6: verify quoted-dotted column names do not collide with CrateDB OBJECT subscript syntax under real query patterns.
- T17: retention & downsampling design — defer detailed design until after Phase 3 lands.
- Performance benchmarks vs. legacy implementation — define KPIs before T11.

---

## 18a. Native copy to and from a staging location (AB#5967)

`IStreamDataRepository.PlanArchiveCopyAsync`, `CopyToStagingAsync`, `CopyFromStagingAsync`, `DescribeArchiveTableAsync`
and `GetStorageVersionAsync` (engine contract, `Runtime.Contracts/StreamData`) are implemented by `Copy/StagingCopyRunner`
with CrateDB's own `COPY TO DIRECTORY` / `COPY FROM`. The concept is
`octo-construction-kit-engine/docs/concept-tenant-backup-native-archive-dump.md`. `SupportsStagingCopy` is true when the
database client can run a statement once under a timeout of its own (`ICrateCopyClient`, implemented by `CrateDatabaseClient`).
`DumpArchiveToStagingAsync` and `LoadArchiveFromStagingAsync` (slice 2, AB#6968) wrap plan, copy and load of one archive and
add the generation map of a rollup, the repeat rule and the row-count verification (below).

**Rollups: the generation map and the repeat rule.** A rollup is two tables, `archive_<rtId>` and `archive_<rtId>__genmap`, and a
recompute changes both without a transaction: new rows under generation N+1, the pointer flip in the map, the sweep of
generation N. The map is the last slice of a plan (`ArchiveCopySlice.ForGenerationMap`, one folder `<rtId>/genmap/all`; an
empty map has no slice). `CopyToStagingAsync` copies it **first**, the data slices after it; `CopyFromStagingAsync` loads the data
**first** and the map **last**, so a pointer never names a version whose rows are not loaded yet.
`DumpArchiveToStagingAsync` (class `RollupArchiveDumper`) adds the repeat rule: plan, read the map (refresh, then select), copy
map and data, read the map again; when the two reads differ, a recompute flipped a pointer in between and the rollup is copied
again, up to `ArchiveCopyOptions.MaxMapAttempts` (default 3) times, then the dump fails with
`ArchiveCopyRefusedException(GenerationMapKeptMoving)` naming the rollup. The result states `Attempts`, `MapReadAtUtc` (the first
read of the attempt that stands) and `LastDataCopiedAtUtc`, both from a clock that never goes backwards; the caller compares
their difference with the sweep grace period (below). An archive that is not a rollup is copied once and its map is never read.
Files of an earlier attempt with other slice keys stay in the staging area; the manifest names only the slices of the final plan.
Rows of a version above the pointer that a recompute in progress had already written (before its flip) are in the dump; the
validator finds them after the restore as orphans and the repair removes them.

`LoadArchiveFromStagingAsync` returns `ArchiveCopyLoadResult.RowCountVerified`: true when the data table holds exactly the
counted rows and, for a rollup, the map holds the counted pointer entries; false when a slice failed, the counts differ or the
load stopped early. The restore job treats an unverified archive as failed.

**Slice retry (AB#7274).** `StagingCopyRunner.CopySliceFromAsync` loads a slice up to `MaxLoadAttempts` (3) times. The first attempt sends the
statement as before; a repeat follows a *transient* failure only (`CopyFailureClassifier`: a failing statement whose text names the memory
circuit breaker, a rejected execution or a node or shard that is not available, a `StreamDataStatementTimeoutException`, a `RETURN SUMMARY`
with `error_count = 0` and fewer rows than the manifest - CrateDB does not count a shard request the breaker refused on another node as an
error, so a two-node cluster loses rows silently - and a summary whose rejected rows all carry a transient text). It waits 30 s and 60 s
(`TimeProvider`, cancellable), logs the heap of the nodes, then sends `COPY FROM ... overwrite_duplicates = true, bulk_size = 5000` (2500
for the third attempt); `CopyStatement.CopyFrom` owns `bulk_size` only when one is given. A repeat is safe because the target table is
created fresh before the load and the slice files come from a table with the same primary key: a row an earlier attempt already wrote is
replaced by the identical row (the superseded document stays as a deleted document until the `OPTIMIZE` after the load). After a repeat that
reports success the slice's range (`BuildExportSliceCountSql`; for the generation map the whole map) is counted and must equal the manifest.
Not repeated: any other statement error, rejected rows with a non-transient text, more rows than the manifest, a cancellation. A failed slice
ends the archive as before; the message then ends with "(after n attempts)". Tests: `StagingCopyRunnerRetryTests`,
`CopyFailureClassifierTests` (unit, simulated CrateDB) and `SliceRepeatIntegrationTests` (real CrateDB: the statement ran and the error is reported
afterwards, or part of the slice was in the table before; raw slice, rollup slice and generation map). Heap: a restore of the 150 million
row sbeg tenant on one node with a 1 GiB heap is the reference run of the change (see the operator page, "Disk and heap headroom").

**Disk headroom (AB#7274).** `StagingDiskHeadroomCheck` compares the expected size with the room each node has **below the high disk
watermark** (`cluster.routing.allocation.disk.watermark.high`, read from `sys.cluster` together with `threshold_enabled`; percentage and
ratio = share of the disk that may be used, absolute value = free space that must remain), not with `fs['total']['available']`: CrateDB
stops allocating shards to a node above the high watermark and the flood stage (95 %) makes the table read-only. Expected size per node =
staged gzip bytes x `DiskSpaceFactor` (12) x copies / nodes, where copies = `min(1 + replicas, nodes)` for the table's replica setting
(`0-1` gives 2 on a cluster of two or more nodes: the replicas are switched on again before the merge). Per archive in `EnsureDiskSpaceAsync`
(before any table setting changes) and for the whole backup through `IStreamDataRepositoryFactory.CheckStagingDiskHeadroomAsync(stagedBytes)`,
which the restore job asks before the MongoDB phase; both go through `StagingDiskHeadroomCheck.Evaluate`. Tests: `StagingDiskHeadroomTests`
(unit), `ClusterDiskHeadroom_ReadsTheRealNodesAndWatermark_...` (real CrateDB).

**Sweep grace period (configuration reference).** The sweep of the generations a recompute superseded is deferred so that a dump
which copied the map before a flip still finds the rows its copy names. Setting `StreamData:Recompute:SweepGracePeriod`
(env `OCTO_STREAMDATA__RECOMPUTE__SWEEPGRACEPERIOD`, `CrateRecomputeOptions`): time span, default `02:00:00`, `00:00:00` sweeps at
once (the behaviour before AB#6968), a negative value fails the start (`ValidateOnStart`, message names the key). The pending
sweeps are rows in `archive_<rtId>__sweep` next to the generation map, dropped with the archive. Cost of the delay: the superseded
rows occupy the table until the sweep, and reads scan a larger table (measured on a 28.8 million row rollup, see the measurements
in the AB#6968 hand-over). An executor or repository built without options (outside the host's DI) keeps the immediate sweep.
Rule of the concept: `octo-construction-kit-engine/docs/concept-rollup-recompute.md`, "Sweep grace period".

**Where a recompute can hit a dump.** The measured placement matrix (flip between map copy and data copy, with and without sweep, after the
data, after the dump; grace period and repeat rule on or off) is in the engine concept `concept-tenant-backup-native-archive-dump.md`,
section "Placement matrix"; with grace period and repeat rule on every placement restores without defects.

**Rollup consistency validator and the restore reconcile marker (AB#6968).** `ValidateRollupsAsync` (`RollupConsistencyValidator`)
checks every rollup of the tenant, or the requested ones, with counts and MIN/MAX statements only (plus the small pointer table); no row is
read and nothing is changed. A rollup the validator cannot look at is **never reported as consistent**: its entry carries
`NotChecked` (`RollupNotChecked`: `Cause` and an English `Reason`) and `IsOk` is false for the entry and for the report, so "nothing found"
never means "not looked at" (a restore of five rollups whose load failed had been reported as "7 rollups consistent"). Causes:
`NoLiveTable` (no table, status Activated or Failed), `Disabled` (status Disabled, no table: a disabled rollup is neither aggregated nor
read; informational like `NeverActivated`, AB#7274 - a restore whose load of the rollup failed keeps the marker through its own
`ArchiveFailed` entry), `NeverActivated` (status Created, no table; listed
for information only - `RollupNotChecked.IsBlocking` is false, the entry and the report stay ok and the marker is not kept, because a
rollup without data cannot be inconsistent and blocking the tenant's rollups for it would stop aggregation, recompute and reclaim for
good; logged at information level), `SourceMissing` (a source entity is gone, or an
activated source has no table: the watermark check would read "no rows"; the other checks still run and their findings are listed next to
it). A rollup with a table is validated whatever its status (a deliberately disabled rollup is checked and can be ok). A rollup with a
table but without its generation map is a finding, not a gap: the repair can create the map. Four checks (`RollupConsistencyCheck`),
each finding names the rollup and the range:

| Check | Finding | Condition |
|---|---|---|
| (a) `PointerWithoutRows` | one per range | a pointer entry holds **no row at all** of its generation in its range **and a source holds rows in that range** (clipped to the source's validity and to the entry's entity). An entry over a range the source is empty in is a correct empty recompute or backfill - the flip is what makes readers see "empty" instead of an older generation - and is no finding; a source that is itself a rollup counts only the rows live under its own pointer, so rows of a generation it superseded (sweep grace period) do not bring the false alarm back; reported for the parts no entry of a higher generation outranks (an unscoped entry is outranked by higher unscoped entries and by higher entries of its own scope). An entry with rows somewhere is not reported: its range may reach past the data (quiet source) or leave a tail after a later entry outranked a part of it - measured on a 28.8 million row rollup, a per-range check flagged exactly that tail |
| (b) `OrphansAbovePointer` | one per generation | rows of a generation that no entry of that or a higher generation covers (range and, for a scoped entry, entity); not checked while `RecomputeInProgress`; without a pointer table every recomputed row is an orphan |
| (c) `WatermarkAheadOfRows` | one per rollup | the watermark of the metadata is newer than the last rollup row (`MAX(window_end)`) and a source holds rows in between (counted per validity span of the source); a quiet source is no finding |
| (d) `MissingGenerationMap` | one per rollup | the rollup table exists, its `__genmap` table does not |

A recompute that legitimately produced no rows (the source holds nothing there) looks like (a); the repair resolves it for good
(it recomputes, and removes an entry that stays empty). The validator logs a warning per finding. The host runs it once per process
and tenant with the first recompute tick, so an inconsistent rollup is visible in the log at start. Cost: see the measurement in the
hand-over; the statements on a 28.8 million row rollup are counts of the generation column and one pointer read.

*Marker.* `IsRestoreReconcilePendingAsync` / `SetRestoreReconcilePendingAsync`: a row keyed by the tenant id in the schema's table
`__restore_reconcile`, created by the first `Set(true)`. **No row, or no table, means not pending**, so tenants and backups from before
this feature run unchanged. The restore job sets it after the archive tables were loaded and clears it when the validator reports ok
or the repair has run; **never while an entry is blocking `NotChecked`** (`NoLiveTable`, `SourceMissing`): the repair skips a rollup without a table (it must not turn a missing table
into an empty one), says why in the final validation (`failedStep` names the first unchecked rollup) and leaves the marker set until the
rollup is restored or activated and validates. A repair of some rollups only clears the marker only when the whole tenant validates ok.
The bot additionally keeps the marker while an archive of the backup failed to restore (it knows the manifest) and restores every source
before the rollups that read it, leaving a source that was disabled in the backup activated, marker set, until its rollups are back.
Deleting a tenant removes the marker row, the repair queue rows (`IStreamDataRepositoryFactory.DeleteTenantStateAsync`; a table is dropped
only when no other tenant of the shared schema has a row in it) and, with the archive tables, the `__sweep` records, so a tenant created again
under the same id starts without a marker. A process remembers an answer for five seconds (set and clear update it at once), another process sees a change
within that time. What the marker blocks: see "Gate" below.

**Rollup repair.** `RepairRollupsAsync(rollupRtIds, dryRun)` (`RollupRepairer`) validates, repairs, validates again and returns
`RollupRepairReport` (`Before`, the `Actions`, `After`, `FailedStep`). Per rollup, in this order, each step needing the one before:

1. *Create the generation map* when the table is missing (`CreateGenerationMap`).
2. *Lost map with recomputed rows.* When the map is missing while rows of generation 1 or higher remain, nothing says which generation
   is authoritative where, and the generation-0 rows of recomputed ranges were swept: deleting the rows as orphans would leave the
   windows empty, rebuilding pointers from the rows could name an incomplete generation. The repair therefore recomputes the **whole
   extent of the rollup** from its sources in one step (`RecomputePointerRange`); the recompute's sweep removes every other generation.
   Without rows of generation 1 or higher the map is simply created.
2b. *Delete the orphans* (`DeleteOrphans`): the rows of a generation that no pointer entry of that or a higher generation covers. The
   pointed generations are not touched. This runs before any recompute so the generation numbers a recompute is handed are free.
3. *Recompute the pointer gaps* (`RecomputePointerRange`): only the part of an entry that no higher entry outranks and that holds no row
   of its generation, with the entry's scope, once per source validity span. The recompute runs on its own executor with an immediate
   sweep (no grace period: the rows below are the repair's garbage and no dump is waiting). The new generation replaces the gap in one
   pointer write; **rows recomputed later than the pointer lie outside the gap and stay**. A recompute that leaves no row (the source
   holds nothing there) has its entry removed, and so is an entry of the old generation that ends without any row: the window then
   reads empty, which is what the source says, and the validator is ok.
4. *Rewind the watermark* (`RewindWatermark`) to the end of the rollup data (the first source bucket when the rollup is empty) and clear
   the recompute generations behind it, exactly as the `rewindRollupWatermark` mutation does; the forward aggregation fills the gap on
   its next tick. The step decides again when it runs: a recompute before it can have filled the rollup up to the watermark.

A failing step ends the repair of its rollup, its remaining steps are `Skipped`, and `FailedStep` names rollup and step; other rollups go
on. **The marker is cleared only when no step failed and the second validation is ok**, never on a dry run, so a failing repair leaves
the tenant blocked (see "Gate"). A dry run lists the steps as `Planned` and changes nothing (`After` is null). The repair is idempotent:
a second run on a repaired tenant plans nothing. It should run while the tenant is marked pending (or when no recompute runs): it
recomputes ranges and rewinds the watermark, which a running orchestrator would race with.

**Queued repair.** `RepairRollupsAsync` runs inline and belongs inside a job of its own (the restore job). The operator surfaces (REST,
CLI, MCP) use `EnqueueRollupRepairAsync(rollupRtIds)`: it plans (a dry run), records the request in the schema's table
`__rollup_repair` (key: tenant id and request id, every column explicit, state `Pending`) and returns at once with the planned steps and
the request id. The host's recompute tick calls `RunPendingRollupRepairsAsync` before it applies the gate - queued repairs are what
releases a marked tenant - claims the pending requests (`UPDATE … WHERE state = <seen>`; a request left `Running` by a process that died is
claimed again after 30 minutes, the repair being idempotent), runs `RepairRollupsAsync`, and writes `Done` or `Failed` together with the
`RollupRepairReport`. `GetRollupRepairRequestAsync(id)` answers the state. The repair clears the marker when its final validation is ok; a
`Failed` request keeps it and its report names the failing step. The id is the request id of this table, not a `RecomputeJob` id: the
recomputes of the repair run in the repair's own ungated executor, because the pending-range drain of the orchestrator is held back by the marker.

**Gate.** While the marker is set, nothing writes to the rollups of the tenant except the repair: the host's rollup tick
(`RollupOrchestratorHostedService`) and recompute tick (`RecomputeOrchestratorHostedService`, which also runs the deferred sweeps)
skip the tenant and say why once at information level (`RestoreReconcileGate`); a direct call is refused with
`RestoreReconcilePendingException` - `CrateDbArchiveRecomputeExecutor.ExecuteAsync` (and with it the reclaim and the sweep at its start)
and `AggregateBucketAsync` (forward aggregation); `SweepSupersededGenerationsAsync` returns 0. The repair's own executor has no gate.
Validate and repair, and everything that reads, are never held back. A tenant without the marker costs one catalogue statement per
five seconds and process (no statement on the table when it does not exist).

**Dump.** The plan cuts the data table into counted time slices with the planner of the row export (`ExportSlicePlanner`,
target `ArchiveCopyOptions.TargetSliceRows`). Each slice is one statement,
`COPY <table> WHERE <time> >= from AND <time> < to TO DIRECTORY '<location>/<rtId>/data/<slice>' WITH (compression = 'gzip', ...)`,
gzip-compressed line-wise JSON, sent as a maintenance statement with `ArchiveCopyOptions.CommandTimeout`. It reports the rows
copied; a difference to the rows counted is a warning (the tenant stays online), not a failure. A failed slice ends the copy,
earlier slices stay valid, and a cancelled token stops between two slices.

**Load.** Into a freshly created table: the dumped columns are compared with the table (a missing column stops the load
before anything is changed), the free disk space per node is checked against nine times the staged size, then
`number_of_replicas = 0, refresh_interval = 0` are set and the previous values remembered. Each slice is
`COPY <table> FROM '<location>/<rtId>/data/<slice>/*' WITH (compression = 'gzip', ...) RETURN SUMMARY`; a slice is loaded when
`success_count` equals the counted rows and `error_count` is 0. The settings are restored when the enumeration ends, also
after a failed slice, an exception or a cancelled caller. Afterwards the segments are merged (`OPTIMIZE ... max_num_segments = 1`,
switch off with `OptimizeAfterLoad = false`). A slice loaded again after a failure uses `OverwriteDuplicates = true`. A NULL in a
column that has a DEFAULT is not preserved: the default applies.

**Credential.** The text of a `COPY` carries the storage credential (S3 key and secret in the URI, `sas_token` or `key` in
`WITH`). Four things keep it away from logs, exceptions, traces and the server's job log:

- `CopyStatement` keeps the executed text and the redacted text together; only the code that executes it calls `Reveal()`.
  The rules are `ArchiveCopyRedaction` and `ArchiveCopyStagingLocation` of the engine: the user info of the URI is dropped, every
  `WITH` option is masked unless it is on a short list of harmless ones.
- A failing statement raises `StreamDataCopyStatementException` (scrubbed message, redacted statement, no inner exception, no data)
  or `StreamDataStatementTimeoutException` (redacted statement, no inner exception); a copy statement is never retried by the
  resilience pipeline. Failed slices carry the same scrubbed message.
- `COPY` commands and batches are excluded from the Npgsql tracing (`CrateDbConnectionAccess.ConfigureTracing`), so the
  injected OpenTelemetry instrumentation records no span with their text; other commands are traced as before.
- `JobLogFilterGuard` reads `stats.jobs_log_filter` before every dump and load and refuses (`ArchiveCopyRefusedException`,
  `JobLogNotFiltered`) unless the filter excludes `COPY` (`stmt NOT ILIKE 'copy %'`), because CrateDB records the full text of a
  finished statement in `sys.jobs_log`. Setting it belongs to the infrastructure (`SET GLOBAL PERSISTENT`).

**Readiness preflight, two doors (AB#6969).** `StagingCopyReadinessProbe` (`Copy/`) is the one implementation of "can the storage
run the native copy": it reads `stats.jobs_log_filter` (`JobLogFilterGuard`) and the node versions and returns a
`StagingCopyReadiness`. It only reads, never throws for a missing prerequisite (the answer says so, with the statement to run, and no
credential), and its failure to ask is an answer too (`Supported`, not ready, the scrubbed reason). Two members delegate to it:
`IStreamDataRepository.CheckStagingCopyReadinessAsync` (tenant-bound) and `IStreamDataRepositoryFactory.CheckStagingCopyReadinessAsync`
(cluster-level; default implementation answers "not supported"). The factory member needs no tenant, no CK cache and no
`IArchiveRuntimeStore`, which is what the tenant restore needs: it has to know that the archive data can be loaded **before** it replaces the
tenant's database, and a tenant context built at that point would load the CK cache from the old database. The statements run on a
connection labelled `default` (the label the health check uses); no tenant schema is touched. Tests: `StagingCopyReadinessProbeTests`
(simulated CrateDB) and `StagingCopyIntegrationTests.ClusterReadiness_*` (CrateDB container: ready, filter removed -> not ready with the reason,
filter restored, equal to the repository's answer).

**Tests that use the local kind cluster are opt-in.** `StagingCopyLocalS3Tests` and `RollupLocalMeasurementTests` (fixture
`LocalKindStagingFixture`) read the CrateDB and the SeaweedFS of the machine's kind cluster and create tables and objects in
a schema and prefix of their own. They run only with `OCTO_TEST_LOCAL_KIND=1` and are skipped with that reason otherwise, so a
plain `dotnet test` never reaches a developer's long-lived cluster. The two measurements additionally need their own variables
(`OCTO_STAGING_MEASURE_SOURCE_TENANT`; `OCTO_ROLLUP_MEASURE_SOURCE_TENANT` and `OCTO_ROLLUP_MEASURE_ROLLUP`).

Tests: `StreamData.UnitTests/Copy` (statements and redaction with canary values, the runner against a simulated CrateDB) and
`Runtime.Engine.MongoDb.IntegrationTests/StagingCopy` (a CrateDB container copying to `file://` in its own file system: round
trip with the same rows, sums and time bounds per month under another tenant id, failed loads, the guard, the job log,
tracing). `COPY TO DIRECTORY` does not create directories on a file system, so those tests create the slice folders first;
an object store needs none.

## 19. References

- Pain points & current architecture analysis: see ADO Issue (this document is linked).
- Existing types: `CkTypeDto`, `CkAttributeDto`, `CkRecordDto` in `octo-construction-kit-engine/src/ConstructionKit.Contracts/DataTransferObjects/`.
- Path tokenizer: `RtPathEvaluator` in `octo-construction-kit-engine/Runtime.Contracts/`.
- Current Crate stack: `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/StreamData/`.
- Mongo collection naming helper to share: `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/CkIdExtensions.cs`.

---

## 20. Implementation Summary (post-rollout)

The plan in §8 was executed in full (T1–T17). The following follow-ups landed in the same scope but are not part of the original task list:

| Topic | What | Where |
|---|---|---|
| Observability | OpenTelemetry counters/histograms for inserts, query latency, archive lifecycle transitions; ActivitySource `crate.*`. | `Runtime.Engine.CrateDb/CrateDbDiagnostics.cs` |
| Authorization | Roles `StreamDataAdmin`, `StreamDataWriter`, `StreamDataReader` registered in identity service; archive lifecycle endpoints gated on `StreamDataAdmin`. | `octo-identity-services/.../DefaultConfigurationCreatorService.cs`, `octo-asset-repo-services/.../StreamDataMutation.cs` |
| Identity ApiResource | `role` claim added to `octoAPI` `Claims` so the access token actually carries roles (not just the scope). | `octo-identity-services/.../DefaultConfigurationCreatorService.cs` |
| Startup reconciliation | `ArchiveReconciler` enumerates `Activated` archives at startup and (re-)provisions any missing CrateDB tables. | `Runtime.Engine.CrateDb/ArchiveReconciler.cs` |
| Refinery Studio — archive picker | `mm-entity-select-input`-based component used inside the query editor for stream-data queries. | `octo-frontend-refinery-studio/.../components/archive-selector/` |
| Refinery Studio — archive admin | List view with state-aware lifecycle actions (Activate / Enable / Disable / Retry / Copy ID), sidebar entry "Stream Data Archives". | `octo-frontend-refinery-studio/.../tenants/repository/archives/` |
| Refinery Studio — `AttributePathPicker` | Row-based editor (mirrors `mm-field-filter-editor`) consuming `availableArchivePaths` to drive the archive create form. | `octo-frontend-refinery-studio/.../archives/components/attribute-path-picker/` |
| Custom GraphQL resolver | `availableArchivePaths(ckTypeId, maxDepth)` query backing the picker. | `octo-asset-repo-services/.../GraphQL/AvailableArchivePathsResolver.cs` |
| REST status endpoint | `GET /streamData/archives/{rtId}` returns current status without going through GraphQL. | `octo-asset-repo-services/.../StreamDataController.cs` |

### Open / deferred

- Connection-pool tuning per environment (defaults from §8 T12 implementation are conservative).
- Migration tooling for existing legacy data is intentionally absent (D11 — hard cut).
- Retention/downsampling **policy** is implemented in code paths (T17 of original plan) but lacks an admin UI and a per-archive default; tracked separately.
