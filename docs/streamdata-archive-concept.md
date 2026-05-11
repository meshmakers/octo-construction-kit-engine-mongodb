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

Primary key `(timestamp, rtId, ckTypeId)` may collide with existing rows. Behaviour on conflict differs per column class:

```sql
INSERT INTO {schema}.{table} (rtId, timestamp, ckTypeId, ...)
VALUES (...)
ON CONFLICT (timestamp, rtId, ckTypeId) DO UPDATE SET
  -- required columns: full overwrite (incoming value is always present per §3 / D7)
  required_col_1   = EXCLUDED.required_col_1,
  ...
  -- optional columns: preserve existing if incoming is NULL (multi-source merge)
  optional_col_1   = COALESCE(EXCLUDED.optional_col_1, optional_col_1),
  ...
  -- standard columns
  rtWellKnownName    = COALESCE(EXCLUDED.rtWellKnownName, rtWellKnownName),
  rtChangedDateTime  = CURRENT_TIMESTAMP,
  rtCreationDateTime = rtCreationDateTime;     -- never modified
```

Implications:

- **Multi-source friendly**: source A may write `voltage`, source B later writes `current` for the same timestamp without overwriting each other (assuming both required fields are present in both inserts).
- **No explicit NULL on optional**: setting an optional column back to NULL via re-insert is not supported in this iteration. If the use-case appears, add an explicit `clearOptionalColumns: [path[]]` parameter on the insert API later.
- **Required-validation runs app-side** (§7) before the SQL is built; the DB `NOT NULL` constraint is the backstop.
- **Idempotent re-inserts** are safe: identical payload only bumps `rtChangedDateTime`.
- **Bulk inserts**: the same `ON CONFLICT` clause is generated once per archive for all rows in the batch.

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

Existing fixture (`StreamDataFixture`) is the starting point — adapt to the archive model and split per scope. The fixture already uses `crate:5.10.10` Testcontainers; that stays.

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
