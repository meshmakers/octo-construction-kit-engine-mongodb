# Claude Code Project Context

This file contains knowledge and context for Claude Code to assist with development.

## Project Overview

This is the MongoDB implementation of the OctoMesh Runtime Engine. It provides:
- MongoDB-based persistence for runtime entities
- Query engine with support for field filters, navigation properties, and aggregations
- Integration with the Construction Kit (CK) model system

## Build Commands

```bash
# Build with local NuGet packages (development)
dotnet build -c DebugL

# Build for release
dotnet build -c Release

# Run integration tests
dotnet test tests/Runtime.Engine.MongoDb.IntegrationTests -c DebugL
```

## Test Configuration

### Switching between Testcontainers and Local MongoDB

Tests can run against either Testcontainers (default) or a local MongoDB instance.

**Option 1: Environment Variable**
```bash
USE_LOCAL_MONGODB=true dotnet test -c DebugL
```

**Option 2: appsettings.test.json**
```json
{
  "systemTest": {
    "useLocalDatabase": true,
    "localDatabaseHost": "localhost:27017"
  }
}
```

## Observability — MongoDB Command Profiling

`MongoCommandObservability` (in `Repositories/MongoDb/Generic/`) subscribes to
`CommandStartedEvent` / `CommandSucceededEvent` / `CommandFailedEvent` on the singleton
`MongoClient` and acts as the Community Edition replacement for the Atlas / Enterprise
Performance Advisor. It emits OpenTelemetry instruments and structured slow-query logs
without changing any application code path.

### Metrics

Meter name: **`Meshmakers.Octo.MongoDb`** (registered in
`octo-common-services/Observability/ObservabilityBuilder.cs` — every service that calls
`builder.AddObservability()` exposes the metrics over Prometheus automatically).

| Instrument | Type | Tags | Purpose |
|------------|------|------|---------|
| `octo.mongodb.command.duration` | Histogram (ms) | `command_name`, `database`, `status` | Latency distribution per command per tenant DB |
| `octo.mongodb.command.errors` | Counter | `command_name`, `database`, `error_code` | Failure counts, tagged with the Mongo error code (e.g. `112` for WriteConflict) |

The `tenantId` is deliberately **not** a tag — `database` is used instead as the
low-cardinality attribution dimension (it equals the tenant database name).

### Slow-Query Logging

Two thresholds on `OctoSystemConfiguration`:

| Property | Default | Behaviour |
|----------|---------|-----------|
| `SlowQueryThresholdMs` | `100` | Above this, the command is logged at **WARN** as: `Slow MongoDB command: <name> <target> on <db> took <ms>ms (requestId=<id>)`. `0` disables slow-query logging (metrics still emitted). |
| `SlowQueryFullCommandLogMs` | `1000` | Above this **additional** threshold, the truncated BSON command body is included in the log line — useful to capture the exact filter/pipeline for very slow operations. |
| `SlowQueryCommandPreviewBytes` | `2048` | Truncation limit for the BSON preview. |

The `<target>` is extracted from the first BSON element value of the command (e.g.
`aggregate=rt_entities`, `find=ck_types`) — enough to identify the affected collection
without dumping the full pipeline.

Env-var override example: `OCTO_System__SlowQueryThresholdMs=50`.

### Suppressed Commands

Heartbeats and handshakes are excluded from both metrics and logs to keep histogram
cardinality bounded:
`isMaster`, `hello`, `ping`, `buildInfo`, `saslStart`, `saslContinue`, `saslContinueOrFinish`,
`endSessions`, `getMore`.

### Correlation

`CommandSucceededEvent` and `CommandFailedEvent` do not carry the database name nor the
command body. A small `ConcurrentDictionary<int, PendingCommand>` keyed by `RequestId`
bridges from `CommandStartedEvent` to the finish event. The map is capped at **10 000**
in-flight entries — on overflow the map is cleared (in normal operation the driver fires
matching success/failed events for every started event, so growth is bounded by concurrency).
When the lookup misses (started event lost), the listener falls back to `database="unknown"`
rather than throwing.

### Exception Safety

Driver command events fire on the driver's own thread pool. Every callback in
`MongoCommandObservability` is wrapped in try/catch and the inner `SafeLogError` swallows
any logger throws — a broken sink must never poison the driver's event pipeline.

### Per-Request Accumulator (AB#4210)

In addition to the always-on metrics and slow-log paths, an HTTP middleware or GraphQL
listener can open a **request scope** that aggregates the cost of every Mongo command issued
on the same async flow:

```csharp
using var _ = MongoRequestScope.Begin(out var stats);
// ... do work ...
// stats.CommandCount, stats.TotalMs, stats.SlowestMs, stats.SlowestCommand
```

- `MongoRequestScope` is the public façade — consumers in `octo-common-services` and
  `octo-asset-repo-services` use it, not the internal `MongoCommandObservability` listener.
- `MongoRequestScope.Current` reads the active scope's stats from any code on the same async
  flow — useful for a GraphQL execution listener that runs inside a request whose scope was
  opened by upstream middleware.
- The scope is carried via `AsyncLocal<RequestMongoStats>`. The MongoDB driver's command
  events fire on its own thread pool, but `ExecutionContext` propagation through Task
  continuations preserves the AsyncLocal value — verified by `AsyncLocalDriverFlowSpike`.
- Out-of-scope work (background jobs, Mesh-Adapter pipelines) sees `null` and the listener
  silently no-ops — only metrics and slow-log fire there, identical to pre-AB#4210 behaviour.
- Heartbeat commands are still filtered before reaching the accumulator, matching the listener's
  ignored-commands set.

In OctoMesh today the scope is opened automatically by `MongoCommandSurfaceMiddleware`
(`octo-common-services` Observability), and consumed by `MongoStatsListener`
(`octo-asset-repo-services` GraphQL pipeline). REST responses get headers
`X-Octo-MongoDb-Duration-Ms` / `X-Octo-MongoDb-Command-Count`; GraphQL responses get
`extensions.mongoDb = { totalMs, commandCount, slowestMs, slowestCommand }`.

### Roadmap

This is **Stage 1** of a three-stage Performance Advisor. Stage 2 (explain-based COLLSCAN
detection) and Stage 3 (`$indexStats` unused-index analysis) are tracked separately —
- Stage 1: **AB#4206** (merged)
- Per-request surface: **AB#4210**
- Stage 2 / Stage 3: not yet scheduled

## BSON Serialization Conventions

### CamelCase Convention

A global `CamelCaseElementNameConvention` is registered in `MongoRepositoryClient.cs`:

```csharp
ConventionRegistry.Register(OctoConventionCamelCase,
    new ConventionPack { new CamelCaseElementNameConvention() }, _ => true);
```

This means all C# properties are serialized to camelCase MongoDB field names:
- `RtAssociationRoleId` → `rtAssociationRoleId`
- `TargetRtCkTypeId` → `targetRtCkTypeId`
- `NavigationPropertyName` → `navigationPropertyName`
- `Attributes` → `attributes`

### Explicit Field Mappings

Some classes have explicit BSON mappings that override the convention:

**NavigationEnd** (`MongoRepositoryClient.cs`):
```csharp
BsonClassMap.RegisterClassMap<NavigationEnd>(cm =>
{
    cm.SetIgnoreExtraElements(true);
    cm.MapIdMember(c => c.AssociationId).SetElementName("_id");  // Explicit!
    cm.AutoMap();
});
```

**RtEntityGraphItem**:
```csharp
BsonClassMap.RegisterClassMap<RtEntityGraphItem>(cm =>
{
    cm.SetIgnoreExtraElements(true);
    cm.AutoMap();
    cm.MapMember(c => c.Associations).SetElementName(Constants.AssociationName);  // "_associations"
});
```

### Important for Aggregation Pipelines

When writing MongoDB aggregation pipelines with projections or AddFields:
1. Use camelCase for field names (matching the convention)
2. Check for explicit mappings that override the convention (e.g., `_id` for `AssociationId`)
3. Use `$fieldName` syntax when renaming fields in projections

## Navigation Property Syntax

Navigation properties use the following syntax:
```
navigationPropertyName.targetTypeName->attributeName
```

Example:
- `parent.testStateOrProvince->name` - Navigate via "Parent" association to StateOrProvince and get its Name attribute

### N:M Association Meta Properties

N:M associations use `::` separator for meta-properties (count/existence) to avoid collision with `->` attribute navigation:
```
navigationPropertyName.targetTypeName::totalCount    → count of associations
navigationPropertyName.targetTypeName::exists        → true if any association exists
```

Implementation in `SingleOriginRtQuery.CreateAssociationCountNavigation`:
- Uses `$lookup` + `$addFields($size)` + `$match` to count and filter by association count
- Triggered when `NavigationPair.AssociationCountFilter` is set
- Runs pre-pagination in `_associationStageDefinitions`, then enriches post-pagination via `CreateInnerNavigation`

## Key Classes

- `TenantRepository` - Main repository for tenant-specific data operations
- `SingleOriginRtQuery<T>` - Query engine for single-origin queries with field filters and navigation
- `MultipleOriginHierarchicalDeepRtGraphQuery` - Deep graph queries following parent-child hierarchy
- `RtPathEvaluator` - Tokenizes and evaluates attribute paths including navigation properties
- `NavigationEnd` - Represents the end of a navigation (association target)
- `MongoRepositoryClient` - Base class that registers BSON conventions and class maps
- `MongoRuntimeRepositoryProvider` - Provides tenant repositories for CK model migrations
- `TenantContext` - Per-tenant context managing CK model imports and migration triggers

## CK Model Migration Support

The MongoDB layer provides `MongoRuntimeRepositoryProvider` for CK model migrations.
This is automatically registered when calling `AddMongoDbRuntimeRepository()`:

```csharp
// Migration support is automatically included
services.AddRuntimeEngine()
    .AddMongoDbRuntimeRepository();  // Automatically registers MongoRuntimeRepositoryProvider
```

This allows `ICkModelMigrationService` to access tenant repositories via `ISystemContext.TryFindTenantRepositoryAsync()`.
When CK models are updated (e.g., System CK model), migrations are automatically detected and executed.

### Automatic Migration on Import

When a CK model is imported (via `ImportCkModelAsync` in `TenantContext`), the system:
1. Captures current schema versions before import (`GetSchemaVersionsDirectAsync`)
2. Performs the import
3. Compares versions — if changed, runs migrations via `ICkModelUpgradeService`

This works for **any** CK model, not just the System model.

**Embedded migrations:** CK models can carry migration scripts inline via `CkCompiledModelRoot.Migrations`. These are surfaced to `CompiledModelCkMigrationContentProvider` during import, eliminating the need for NuGet package dependencies on source CK models.

**Design note:** `GetSchemaVersionsDirectAsync` queries the database directly (not through `IRuntimeRepositoryProvider`) to avoid recursion, since `TryFindTenantContextAsync` itself calls `UpdateSystemCkModelAsync`.

### Key Components

| Class | Description |
|-------|-------------|
| `MongoRuntimeRepositoryProvider` | Implements `IRuntimeRepositoryProvider` using `ISystemContext` |
| `MongoTenantBlueprintHistory` | MongoDB-based blueprint history storage |
| `MongoBlueprintBackupService` | MongoDB-specific backup implementation |

## StreamData: Archives and Rollups

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

### Activation DDL Branch (`CrateDbStreamDataRepository.EnsureArchiveCreatedAsync`)

- Raw archive snapshots → `ArchivePathTypeResolver` walks the CK type tree to resolve each
  attribute path to a `CrateColumnType`.
- Rollup snapshots → `RollupColumnTypeResolver` derives the SQL type from the aggregation
  function:
  - `COUNT` → `BIGINT`
  - `AVG` → `{base}_sum DOUBLE PRECISION`, `{base}_count BIGINT`
  - `SUM` / `MIN` / `MAX` → `DOUBLE PRECISION`

  Rollup column names are storage identifiers (e.g. `temperature_avg_sum`), not CK-type
  attribute paths — the path-resolver would fail to resolve them.

### Rollup Lifecycle

- `MongoCkRollupArchiveRuntimeStore` extends the runtime-store contract with:
  - `InsertAsync(...)` — builds the `RtCkRollupArchive` entity (Created status,
    `Columns` + `Aggregations` via `AttributeRecordValueList<T>`) and persists.
  - `AdvanceWatermarkAsync` / `SetFrozenUntilAsync` — orchestrator + lifecycle writes.
  - `EnumerateAsync` / `CountActiveRollupsForSourceAsync` — orchestrator tick + source-delete guard.
- `TenantContext.GetRollupArchiveLifecycleService` wires both the rollup store and the shared
  archive store into `RollupArchiveLifecycleService`; the archive store is needed by
  `CreateAsync` to look up the source archive's `TargetCkTypeId`.

### Extensible Enum Preservation on Import (WI #3324)

`DatabaseCkModelRepository.PreserveExtensibleEnumValues` runs inside `ExecuteImport`
**before** `DeletePreviousVersion` so custom enum extensions (`CkEnumValue.IsExtension == true`)
survive a model upgrade:

1. Load all `CkEnum` rows for the current model where `IsExtensible == true`.
2. For each extensible enum in the new compiled model, copy back every preserved extension
   value.
3. If a preserved extension value's `Key` collides with a CK-defined value, the extension
   value wins (CK-defined value is removed first). The collision is reported via
   `ICkModelImportAuditTrail.RecordExtensibleEnumValueOverrideAsync`. The default audit-trail
   implementation logs a warning; `EventRepositoryCkModelImportAuditTrail` in
   `octo-common-services` bridges the call to `IEventRepository.StoreWarningEvent` so it
   surfaces in the tenant event log (`AddOctoNotification` registers this adapter).

`TenantDatabaseSourceIdentifier` carries the `TenantId` (nullable; `null` = system tenant) so
the audit trail can route notifications to the correct tenant.

### Auto-import Downgrade Guard

`TenantContext.EnsureStreamDataCkModelImportedAsync` checks the currently-installed
`System.StreamData` version before importing the descriptor's version. If the installed
version is **strictly greater** than the descriptor's target, the import is skipped — this
prevents a service that ships an older `IStreamDataCkModelDescriptor` (or the bare 1.0.0
fallback for services that register no descriptor) from overwriting a higher version that a
sibling service already installed. Without this guard `DeletePreviousVersion` would strip the
newer model's CK records and the `CkCache` reload would lose the newer types.

## Test Data Structure

The test CK model includes this hierarchy:
```
Europe (Continent)
└── Österreich (Country)
    ├── Salzburg (StateOrProvince)
    │   ├── Pinzgau / Zell am See (District) → Fusch (Municipality)
    │   ├── Tennengau, Pongau, Lungau, Flachgau (Districts)
    │   └── Salzburg Stadt (District) → Leopoldskron-Moos (Municipality)
    └── Tirol (StateOrProvince)
        ├── Lienz, Landeck (Districts - active)
        └── Imst, Kitzbühel (Districts - Archived)
```

### Migration Test Data

`TestCkModelV2` provides a v2.0.0 variant of the test CK model with:
- A migration script (`1.0.0-to-2.0.0.yaml`) that renames `Name` → `DisplayName`
- Migration metadata (`migration-meta.yaml`)

Used by `CkModelImportMigrationTests` to verify automatic migration on import.
