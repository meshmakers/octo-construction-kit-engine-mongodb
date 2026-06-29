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

### Slow-Query Buffer (AB#4212)

In addition to metrics, slow-log, and the per-request scope, `MongoCommandObservability`
also captures every slow command (above `SlowQueryThresholdMs`) into a process-wide
**ring buffer** so the Refinery Studio Diagnostics surface can show recent slow queries
without scraping logs:

- `SlowQueryEntry` POCO — Timestamp, CommandName, Target, Database, DurationMs, RequestId,
  CommandBsonPreview, Success, ErrorCode
- `SlowQueriesBuffer` public class — thread-safe FIFO ring backed by `ConcurrentQueue<T>`;
  default capacity 1000 (~3 MB resident at ~3 KB per entry), configurable via
  `OctoSystemConfiguration.SlowQueryBufferSize` (0 disables)
- Registered as a DI singleton from `AddMongoDbRuntimeRepository()` — one buffer per
  service process, shared between admin and user MongoDB connections
- Read API: `GetSnapshot(predicate, limit)` returns entries newest-first, point-in-time
  consistent under concurrent writers
- Both successful and failed slow commands are captured (failures distinguished via
  `Success` field and `ErrorCode`)
- Heartbeat commands are filtered before reaching the buffer (same `IgnoredCommands` list)

In OctoMesh today this buffer is consumed by `DiagnosticsController` in `octo-asset-repo-services`,
which exposes `GET /{tenantId}/v1/Diagnostics/slow-mongo-queries` and filters entries by
`Database == tenantId` so each tenant only sees its own queries. The Refinery Studio
**Diagnostics → Slow Queries** page renders the result.

### Async Explain Capture (AB#4216)

For every slow query that lands in the buffer with a fingerprint, `MongoCommandObservability`
asynchronously schedules `db.runCommand({explain: {<original command>}, verbosity: "queryPlanner"})`
against the originating database and stores the parsed plan in `SlowQueryExplainCache`. The
buffer's read APIs (`GetSnapshot`, `GetGroupedSnapshot`) join entries with the cache at read
time so the Refinery Studio Diagnostics surface sees the explain stamped alongside the BSON
preview without any extra round-trip.

| Type | Purpose |
|---|---|
| `SlowQueryExplain` | Parsed result: `WinningStage`, `HasCollScan`, `IndexNames`, optional `RawExplainPreview`, status (`Success` / `Unsupported` / `Failed`) + optional `ErrorMessage`. |
| `SlowQueryExplainKey` | Composite key `(Fingerprint, CommandName, Target, Database)` — same shape the grouped snapshot uses, so explain is per-tenant-per-target even when the fingerprint alone would collide. |
| `SlowQueryExplainParser` | Static parser. Handles `find`/`count`/`distinct`/`findAndModify`/`update`/`delete`/`mapReduce` (top-level `queryPlanner.winningPlan`) and `aggregate` (descends into the first `$cursor` stage). Recursive walk through `inputStage` / `inputStages` captures every IXSCAN's index name and flips `HasCollScan` on first COLLSCAN. |
| `SlowQueryExplainCache` | Thread-safe per-process cache. `ShouldCapture(key)` enforces cooldown (no probe within `SlowQueryExplainCooldownSeconds` of a successful capture for the same key). FIFO-evicts beyond `SlowQueryExplainCacheCapacity`. |

**Dispatch (`MongoCommandObservability.DispatchExplain`):** runs as a fire-and-forget
`Task.Run` from the driver's command-event callback. Guards execute synchronously *before*
the task:

1. Skip if `SlowQueryExplainEnabled = false` or no live `IMongoClient` wired
2. Skip if the command failed (no winning plan worth fetching)
3. Skip if the cache says `ShouldCapture = false` (cooldown active)
4. If the command type is not in the explainable set, stamp the cache with
   `SlowQueryExplainStatus.Unsupported` and skip the driver round-trip — this keeps the
   cooldown ticking so we don't re-walk this branch on every fire of the same shape

When the task does run, it deep-clones the BSON command (the driver may still retry the
original on its own connection), wraps it in
`{explain: <clone>, verbosity: "queryPlanner"}`, runs against
`client.GetDatabase(database)`, parses, stores. Cancellation token derived from
`SlowQueryExplainTimeoutSeconds`; on timeout we store `Status = Failed` /
`ErrorMessage = "timeout"`. All exceptions are caught and logged via the same `SafeLogError`
path the rest of the listener uses — a broken sink must never poison the driver pipeline.

**Configuration (`OctoSystemConfiguration`):**

| Field | Default | Purpose |
|---|---|---|
| `SlowQueryExplainEnabled` | `true` | Master switch. When `false`, the cache is constructed with capacity 0 so `ShouldCapture` always returns false and nothing is dispatched. |
| `SlowQueryExplainCooldownSeconds` | `300` | Minimum seconds between captures for the same key. |
| `SlowQueryExplainCacheCapacity` | `5000` | Distinct keys retained before FIFO eviction. |
| `SlowQueryExplainTimeoutSeconds` | `5` | Per-explain wall-clock budget. |
| `SlowQueryExplainPreviewBytes` | `4096` | UTF-8 byte cap on the truncated `queryPlanner` JSON stored on each result. |

**Surface:** `SlowQueryEntry` and `SlowQueryGroup` carry a nullable `Explain` field; the
buffer's read methods join from the cache before returning, so REST callers
(`DiagnosticsController`) and the Studio surface see a single enriched view. `null` means
no probe has finished yet for this key.

**Note for write commands.** `update`, `delete`, and `findAndModify` are *explainable* at
`verbosity: "queryPlanner"` and do **not** execute the write at that verbosity — the MongoDB
docs are explicit. We include them in `IsExplainable` because they're frequently the
slowest commands and the plan reveals whether the operation was anchored by an index.
`executionStats` verbosity (which *would* execute writes) is deliberately out of scope.

### Unused-Index Analysis via $indexStats (AB#4224 / Stage 3)

Closes the Performance Advisor's add-and-remove loop: Stage 2C/2D ADD indexes when a slow
query needs them; Stage 3 IDENTIFIES indexes that aren't earning their keep so they can be
removed.

| Component | Purpose |
|---|---|
| `IndexUsageEntry` (record) | One row per `(collection, index)` after aggregating MongoDB's `$indexStats` across replica-set hosts. Carries `OpsCount` (sum across hosts), `SinceUtc` (earliest across hosts — worst-case observation window), `AgeDays`, `IsBuiltin`, paste-ready `DropShellCommand`, and a pre-classified `Status`. |
| `IndexUsageStatus` | `Builtin` (e.g. `_id_` — never droppable), `Unused` (0 ops, ≥ minAgeDays old), `LowUsage` (some ops but below threshold, ≥ minAgeDays old), `Used` (otherwise — including indexes too young to judge). |
| `IndexUsageClassifier.Classify` | Pure function — Builtin overrides, then age guard (anything younger than `minAgeDays` is always `Used`), then ops thresholds. No clock reads inside; deterministic. |
| `IndexUsageCollector.CollectAsync` | Lists non-system collections, runs `$indexStats` per collection, projects via `BuildEntries`. Live-query design — called on demand from the asset-repo REST endpoint, no background polling. |
| `IndexUsageCollector.BuildEntries` | Pure (testable) projection step. Groups raw `$indexStats` docs by index name, folds per-host figures, builds drop command with JS-string escape (same defensive pattern as Stage 2C `createIndex`), classifies. |
| `IIndexUsageService` / `IndexUsageService` | DI entry point asset-repo consumes. Takes a `tenantId`, resolves the tenant's `IMongoDatabase` via `ISystemContext.FindTenantContextAsync` + `IAdminRepositoryAccess`, delegates to `IndexUsageCollector.CollectAsync`. Internal-impl-behind-public-interface — the engine keeps freedom to swap the resolution path (caching, tenant-pool client) without breaking consumers. Registered as singleton in `RuntimeEngineBuilderExtensions`. |
| `IRepositoryInternal.Database` | Engine-internal accessor on `MongoRepository`. Exposes the underlying `IMongoDatabase` so observability paths (Stage 3 `$indexStats`) can issue driver-level aggregations without going through the dynamic CK-typed collection wrappers. Kept on the internal interface — the Mongo-driver type does not leak into the public engine API. |

**System collections skipped:** any name starting with `system.` or `__`. Tenant-data
collections (RtEntity, associations, blueprint history, configuration, …) are in scope.

**Replica-set aggregation:** `$indexStats` returns one document per host. We sum
`accesses.ops` across hosts (any host's hit counts) and take the EARLIEST
`accesses.since` (longest observation window — if an index was added recently on a secondary,
the primary's older `since` is what the operator should reason about).

**`accesses.since` reset on `mongod` restart.** A fresh process means every index reports
0 ops with `since = now`. The default `MinAgeDays = 7` filter makes this safe: right after
a restart, nothing is older than 7 days, the page is effectively empty until enough time has
passed for the signal to be meaningful.

**Out of scope:**

- Background polling / history tracking (Stage 3B if production needs it)
- Automatic `dropIndex` execution (footgun across tenants — copy-paste only)
- Reverse-mapping index → CK-YAML source (operator can find it by name)
- Sharded-cluster aggregation (OctoMesh tenants are replica sets)
- `$collStats` size analysis (separate concern)

### CK-YAML Index Suggestions for COLLSCAN (AB#4222 / Stage 2D)

For every Stage 2C suggestion that targets a known CK type, the suggester additionally
emits a **CK-YAML snippet** the operator can paste into the CK type's source YAML under
its `indexes:` array. Subsequent model imports re-create the corresponding MongoDB index
via the existing `CkTypeIndexDto` → `MongoDbRepositoryDataSource.PrepareAndCreateIndex`
machinery, so the index survives re-imports and cross-tenant migration.

| Component | Purpose |
|---|---|
| `MongoDbAttributePathResolver.TryReverseToCkPath` | Inverse of `ResolveToMongoDbFieldPath`. Strips `attributes.` prefix and `.value` suffix, walks even-indexed camelCase segments and odd-indexed `attributes` separators back into PascalCase CK attribute names. Returns null when the path isn't a CK attribute (e.g. `ckTypeId.fullName`, `_id`) or fails to resolve in the provider. |
| `CkYamlIndexSnippetWriter.Write` | Hand-formatted YAML matching the shape used by real CK types — `indexes: - indexType: Ascending - fields: - attributePaths: [...]`. Leading comment carries the audit trail (AB#4222 + CK type full name). |

**Suggester wiring.** `SlowQueryIndexSuggester.TrySuggest` now accepts an optional
`(tenantId, ICkCacheService)` pair. When both supplied AND the filter carries a top-level
`ckTypeId.fullName` equality predicate AND the cache knows that type AND every Mongo field
in the suggestion reverse-maps to a CK attribute path, the result carries
`CkYamlSnippet` and `CkTypeFullName`. Any failure along the way leaves them null — the
mongosh shell command still ships as Stage 2C.

**`ckTypeId.fullName` extraction.** Walks the top level and direct `$and` branches.
`$or`/`$nor` branches with differing type values short-circuit (we don't pick arbitrarily
and emit a snippet against the wrong type).

**Dispatcher wiring.** `MongoCommandObservability` takes an optional `ICkCacheService`
constructor dependency; `MongoRepositoryClient.Client` getter resolves it via
`IServiceProvider.GetService` (null for hosts without the engine attached). Snapshot into
a local before the `Task.Run` closure so a concurrent field replacement can't race the
in-flight explain.

**Out of scope:**

- New `Indexed: true` flag on `CkAttributeDto` (would need Compiler + schema bump in
  `octo-construction-kit-engine` — deferred to a future stage if/when attribute-level
  index hints prove useful beyond the suggester).
- Direct CK-YAML emission for non-RtEntity collections (`ck_types`, `rt_associations`,
  `_users`, …). Those don't have a single CK type to attribute the index to.
- Auto-apply the snippet to a CK source file. Operator pastes manually so they can review
  placement.

### MongoDB Index Suggestions for COLLSCAN (AB#4220)

When `SlowQueryExplain.HasCollScan` flips, `SlowQueryIndexSuggester.TrySuggest` analyses
the original BSON command and emits a ready-to-run mongosh `createIndex(...)` command.
The suggestion is attached to the explain (`SlowQueryExplain.IndexSuggestion`) and surfaces
in the Refinery Studio Diagnostics expand row with a copy-to-clipboard button.

| Command type | Filter source |
|---|---|
| `find` / `count` | `filter` or `query` |
| `distinct` | `query` + the `key` field appended as equality |
| `aggregate` | First pipeline stage (must be `$match`; otherwise no suggestion) plus the immediately-following `$sort` stage if present |
| `update` / `delete` / `findAndModify` | `updates[0].q` / `deletes[0].q` / `q` / `query` |

**ESR ordering.** Compound-index keys are emitted per Mongo's Equality → Sort → Range rule.
Within each category the original BSON element order is preserved, so an operator can reason
about "first equality field" deterministically.

**Filter walking.** Top-level `$and` branches contribute fields as a union. `$or` / `$nor`
also contribute the union but the suggestion gets a Notes caveat that per-branch indexes
may be more selective. Operator-prefixed keys (`$gt`, `$lt`, `$in`, `$ne`, …) are not
field paths — they're classified as ranges / equalities on their parent field. Special
operators (`$text`, `$near`, `$elemMatch`, `$regex`) downgrade confidence to Low and emit
a Notes caveat that a different index type is required.

**Confidence:**

| Rating | When |
|---|---|
| `High` | Single field, equality only, no $or, no special operators. |
| `Medium` | 2-3 fields, equality + at most one range / sort. |
| `Low` | 4+ fields, contains $or / $nor, contains text / geo / regex / elemMatch. Still emitted as a starting point. |

**Out of scope:**

- `getIndexes` introspection to suppress duplicates — adds DB load; a duplicate `createIndex`
  is a no-op anyway.
- Auto-execute button — footgun across N tenants. Manual copy-paste is the right ergonomic
  for production data.
- CK-attribute reverse mapping (Stage 2D). Today the suggestion targets the raw MongoDB
  field path (`attributes.name.value`); the future CK-YAML emission will write
  `Indexed: true` on the CK attribute so the index survives model re-imports.
- Per-branch indexes for `$or`. One compound covering the union with a Notes caveat.
- Index-name length cap at 127 bytes (Mongo's hard limit) with SHA-256 short-hash suffix
  for truncated names so similar shapes don't collide.

### Pipeline Fingerprinting (AB#4213)

`SlowQueryFingerprinter.Fingerprint(BsonDocument)` produces a stable 16-char hex hash of
a command's structural shape — semantically-identical queries that differ only in literal
values (e.g. `{find: 'ck_types', filter: {name: 'Asset'}}` vs `… {name: 'Device'}`) get
the same fingerprint. Algorithm: walk the BSON recursively, replace every primitive value
with `"?"`, preserve field/stage order, collapse primitive arrays to one placeholder element,
recurse into document arrays (so aggregation pipelines keep stage count + order), serialise to
canonical JSON, SHA-256, first 16 hex chars.

Every `SlowQueryEntry` in the buffer carries a `Fingerprint`. `SlowQueriesBuffer` also exposes
`GetGroupedSnapshot(predicate, limit)` which aggregates by fingerprint and returns
`SlowQueryGroup` records carrying Count, FirstSeen, LastSeen, Min/Max/Avg duration and the
most-recent representative entry.

The REST endpoint accepts `?groupBy=fingerprint` to return `SlowQueryGroupDto[]` instead of
the per-call entries. The Refinery Studio page exposes this as a **Group similar** toggle.

The fingerprint is also the planned dedup key for Stage 2B's `explain()` capture (one explain
per fingerprint per time window, to avoid replay storms when a hot endpoint produces hundreds
of structurally-identical slow queries).

### Roadmap

- Stage 1: **AB#4206** (merged) — slow-log + OTel histograms
- Per-request surface: **AB#4210** (merged) — GraphQL extension + REST headers
- Studio surface: **AB#4212** (merged) — ring buffer + Diagnostics page
- Stage 2A: **AB#4213** (merged) — pipeline fingerprinting + grouped view
- Stage 2B: **AB#4216** (merged) — async `explain()` capture + COLLSCAN detection
- Stage 2C: **AB#4220** (merged) — MongoDB index suggestions for COLLSCAN
- Stage 2D: **AB#4222** (merged) — CK-attribute reverse mapping + CK-YAML emission
- Stage 3: **AB#4224** — `$indexStats` unused-index analysis (this section)

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
  (1) `BuildInsertFromStagingWithGeneration` copies staged rows into the live table stamped with the
  next generation `N+1` (the previous generation stays visible); (2) `RefreshArchiveTableAsync`;
  (3) `GenerationMapSqlBuilder.BuildUpsertPointer` flips the pointer to `N+1` — the **atomic commit**;
  (4) `BuildSweepSupersededGenerations` deletes the now-superseded generations in the range; (5) drop
  staging. A crash before the flip leaves readers on the previous generation; a crash after the flip
  but before the sweep just leaves dead rows the next sweep/activation reclaims.
- **Read path** — the four windowed query methods call `LoadGenerationRangesAsync` (reads the genmap)
  and pass the ranges to `CrateQueryBuilder.WithGenerationRanges`; `CrateQueryCompiler` emits
  `"generation" = CASE WHEN <range> THEN <gen> … ELSE 0 END` (ranges ordered newest-generation-first
  so an overlapping re-recompute wins). Empty genmap ⇒ no predicate ⇒ all (generation-0) rows.
- **Integration test:** `RollupRecomputeGenerationPointerTests` (in `octo-asset-repo-services`,
  reusing its CrateDB+Mongo `StreamDataFixture`) drives the real executor end-to-end against a CrateDB
  Testcontainer and asserts the generation flip, the no-mixed-read filter (an injected uncommitted
  generation stays hidden), and the post-flip sweep. This is the automated replacement for the
  previously-manual live validation; it caught both the staging-refresh bug above and the empty-genmap
  baseline-filter bug.
- **Upgrade self-heal:** `EnsureWindowedTableShapeAsync(requireGenerationColumn: isRollup)` drops an
  existing rollup table that is on the windowed shape but lacks the `generation` column (provisioned
  before Phase 6) so the subsequent `CREATE TABLE IF NOT EXISTS` re-adds it with the generation-keyed
  PK; the orchestrator re-aggregates on the next watermark advance (the same lossy-but-self-healing
  trade-off as the pre-Phase-7 single-timestamp migration). No-op once the column is present.
- **Caveats:** rollup tables provisioned *before* Phase 6 lack the generation column/PK — handled by
  the upgrade self-heal above (dropped + recreated); `LoadGenerationRangesAsync` also tolerates a
  missing genmap table on the read side. Per-rtId scoped recompute is still `NotSupported` in the
  executor (genmap `rtid_scope` is always `''`). `rewindRollupWatermark` over a recomputed range is
  not reconciled with the genmap yet.

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
