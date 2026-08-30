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

## Connection ApplicationName is clamped (AB#4762)

Both repository clients label their connection
`OctoMesh-{database}-{instanceId:guid}-{user}` so it is identifiable in MongoDB's `currentOp` and
logs. The driver rejects an ApplicationName longer than **128 bytes**, and the database name appears
in that string **twice** — once directly and once inside the per-tenant user
(`string.Format(DatabaseUser, database)`). A database name beyond roughly 30 characters therefore
threw `ArgumentException` from `MongoUrlBuilder.set_ApplicationName` deep inside tenant provisioning:
the tenant came up half-built and then failed on every background tick forever, and a service that
iterates all tenants at startup (identity's `DynamicAuthSchemeServiceInitializer`) refused to boot at
all.

`MongoRepositoryClient.BuildApplicationName` truncates it on a UTF-8 character boundary instead.
Clamping a diagnostic label is strictly better than refusing to serve the tenant, so **do not** turn
this back into a validation rule on the database name — MongoDB's own 63-byte limit is the only one
callers should have to satisfy.

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

**Raw-BSON retention (AB#4368, refined by AB#4374).** `CommandStartedEvent.Command` is only
valid while the started-event handlers run: for bulk-write commands (insert/update/delete with
OP_MSG payload-type-1 sections) it is a `RawBsonDocument` — or contains
`RawBsonDocument`/`RawBsonArray` slices — over the connection's send buffer, which the driver
returns to its pool right after the event. `MaterializeForRetention` therefore snapshots the
command before it enters the `_pending` map: raw arrays under the **bulk payload fields only**
(`documents`, `updates`, `deletes`) are replaced with a `"<N raw documents elided>"` placeholder
(no megabyte bodies retained or fingerprinted, fingerprint stable across batch sizes); every
other raw array — above all an aggregate's `pipeline` — is materialized element-wise into an
independently-owned `BsonArray`, and small raw sub-documents (lsid, writeConcern) are
deep-cloned. AB#4374 background: the original AB#4368 fix elided *every* raw array, so a raw
`aggregate` command lost its pipeline and the explain probe sent
`{explain: {..., pipeline: "<7 raw documents elided>"}}` — server error 14 (`'pipeline' option
must be specified as an array`), a WARN+ERROR pair per cooldown window, and a shape-blind
fingerprint. `DispatchExplain` additionally guards via `ContainsElidedPlaceholder`: a command
still carrying a placeholder (raw bulk update/delete) is stamped `Unsupported` in the explain
cache instead of round-tripping a guaranteed failure. Defense in depth: `TruncateBson`,
`SlowQueryFingerprinter.Fingerprint`, and the explain-dispatch `DeepClone` all catch
`ObjectDisposedException` and degrade gracefully — before AB#4368, identity-services startup
(bulk seeding, everything above the slow threshold) flooded the log with ERROR entries and lost
exactly those entries from the slow-query buffer.

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
original on its own connection), **strips the wire envelope** via `StripWireEnvelope`, wraps
the result in `{explain: <clone>, verbosity: "queryPlanner"}`, runs against
`client.GetDatabase(database)`, parses, stores. Cancellation token derived from
`SlowQueryExplainTimeoutSeconds`; on timeout we store `Status = Failed` /
`ErrorMessage = "timeout"`. All exceptions are caught and logged via the same `SafeLogError`
path the rest of the listener uses — a broken sink must never poison the driver pipeline.

**Wire envelope must be stripped before wrapping (AB#4958).** `CommandStartedEvent.Command` is
the command *as it goes over the wire*, so it carries `$db`, `$readPreference`, `$clusterTime`,
`lsid`, … `RunCommandAsync` attaches `$db` itself, so wrapping the capture as-is made the server
reject **every** dispatch with `BSON field 'aggregate.$db' is a duplicate field` — the probe
never produced a plan from AB#4216 (2026-06-22) until this fix, which also means no COLLSCAN
detection and no index suggestions (AB#4220 / AB#4222) for any shape. It only became visible
after AB#4374 stopped the earlier error-14 failure on the elided pipeline array; before that the
dispatch died one step sooner. `StripWireEnvelope` removes **every `$`-prefixed top-level field**
(prefix-based on purpose — the driver may attach further ones in a future version) plus the
session/transaction and concern fields in `WireEnvelopeFields`; the query itself (filter, sort,
pipeline, hint, collation) is untouched, or the explained plan would not be the one that was
measured. The strip happens on the explain clone only — the buffer preview and the fingerprint
are computed from `ctx.Command` and are meant to show the command as it actually went over the
wire. `Explain_DispatchedCommand_CarriesNoWireEnvelope` pins the shape of the **dispatched**
document, which no test covered before: that gap is why two consecutive defects (AB#4374,
AB#4958) shipped unnoticed in the same three lines.

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

### TimeSpan Attributes — Canonical Int64 Ticks (AB#4259)

`TimeSpan` attribute values are stored as BSON **Int64 ticks**. The dedicated
`TimeSpanSerializer` (`Serialization/`) writes `value.Ticks` and reads Int64/Int32/Double
ticks plus string shapes. Strings are tolerated because CK attribute values live in a
`Dictionary<string, object?>` that round-trips through `OctoObjectSerializer` (dispatches on
BSON type, not the consumer's CLR type), so the per-type serializer does not always apply.

The accepted string shapes are, in order:

1. **Bare-integer ticks string** (e.g. `"9000000000"`) — the shape the **ImportRt
   export/import JSON round-trip** produces for a `TimeSpan` attribute. It must be parsed as
   ticks, **not** handed to `TimeSpan.Parse`, which reads `"9000000000"` as 9-billion *days*
   and overflows to a parse failure.
2. `.NET` literal (`"00:15:00"`).
3. ISO-8601 duration (`"PT15M"`).

The same three-way coercion is mirrored in two engine-layer places that handle the dict's
`object?` values directly (the serializer is bypassed there):

- `AttributeValueConverter.ConvertAttributeValue` (`Runtime.Contracts`) — the **import
  normalization** point (`ImportRtModelCommand.AssignAttributes` →
  `RtTypeWithAttributes.SetAttributeValue`). Converting the ticks string to a real `TimeSpan`
  on import means the next Mongo write persists the canonical Int64.
- `RtTypeWithAttributes.TryCoerceTimeSpan` (`Runtime.Contracts`) — the **read** point behind
  the generated `GetAttributeValueOrDefault<TimeSpan>` accessor. Without it, a corrupted
  ticks-string value fell through to `Convert.ChangeType(string, TimeSpan)` and threw
  `InvalidCastException`, surfacing as the generic ASSET1002 "An error occurred" on
  `enableArchive` for an imported `TimeRangeArchive` (its `Period` is the only TimeSpan
  attribute in the StreamData model).

When adding a new place that coerces an attribute value to `TimeSpan`, accept the
bare-integer ticks string too, or it will reject already-imported data.

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

## StreamData: Archives and Rollups

### Schema Instance Prefix (AB#4946 / Epic AB#4944)

`TenantSchema.SchemaName` derives the per-tenant CrateDB schema from the tenant id alone, so two
OctoMesh instances sharing one CrateDB cluster collide on identical tenant ids.
`StreamDataInstanceConfiguration.SchemaInstancePrefix` (optional, **default empty**, root
`StreamData` config section ⇒ uniform env var `OCTO_STREAMDATA__SCHEMAINSTANCEPREFIX` across all
services) prepends an instance prefix: `{prefix}_{tenant}`. Rules:

- **Empty prefix ⇒ byte-identical legacy naming** — pinned by
  `TenantSchemaInstancePrefixTests.SchemaName_WithoutPrefix_IsByteIdenticalToLegacyNaming`.
  Existing instances must never set it, or their tenants' schemas would move. Deliberately a
  separate setting, NOT derived from the RabbitMQ `instancePrefix` (test-2 main already runs
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
- Tests mutating the process-wide prefix forced `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
  on `StreamData.UnitTests` (pure-logic assembly, serialization costs ~nothing); the naming
  matrix itself tests the pure `SchemaName(tenantId, prefix)` core.

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

### Tenant-level Disable is a verified precondition (AB#4255)

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
  - `First` / `Last` (AB#4188) → a single `DOUBLE PRECISION` column holding the value at the
    earliest / latest observation in the bucket. CrateDB has no `arg_min`/`arg_max` and `MIN`/`MAX`
    reject arrays, so `RollupAggregationSqlBuilder` wraps the source in a `ROW_NUMBER()` sub-select
    (`AppendArgSourceSubquery`) ranked by the raw `timestamp` (raw source) or child `window_end`
    (windowed / cascade source), and the outer `GROUP BY` picks `MAX(CASE WHEN rn = 1 THEN value END)`
    — a rollup-of-rollup thus re-picks the earliest / latest child bucket. Numeric source columns
    only. In the LOCF path (a co-occurring TWA) the rank sorts the carry-in row last and the pick is
    `AND NOT is_carry`-guarded. The stored column is read back directly as its own series
    (`First`/`Last` are not ad-hoc query aggregates).
  - `TimeWeightedAvg` (AB#4336) → `{base}_integral DOUBLE PRECISION` (Σ value × Δt in value·ms),
    `{base}_duration BIGINT` (covered ms); default base name uses the short token `twavg`.
    Forward aggregation over a raw source uses a LOCF statement (carry-in row per rtId bounded
    by `RollupArchive.CarryLookbackMs`, default 35 d; interval weighting via `LEAD`) built by
    `RollupAggregationSqlBuilder.BuildWithLocfCarry`; windowed sources weight by window length.
    Read path recombines `SUM(_integral) / NULLIF(SUM(_duration), 0)` (alias suffix `_twavg`);
    cascades chain the pair via SUM specs. Direct TWA over raw archive queries is guarded with
    a `NotSupportedException` in `QueryVariable` (follow-up: concept-time-weighted §6.2).

  Rollup column names are storage identifiers (e.g. `temperature_avg_sum`), not CK-type
  attribute paths — the path-resolver would fail to resolve them.

### Computed Columns — Formula Names (AB#4779)

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

### Open-Bucket Refresh + Recompute Cap (AB#4306)

The forward `RollupOrchestrator` optionally re-aggregates the **current open (in-progress) bucket**
on every tick at `generation 0`, without advancing the watermark, so a coarse partial period (*this
month / this year so far*) stays live instead of showing nothing until the period closes. Controlled
by `RollupOrchestratorOptions.RefreshOpenBucket` (`StreamData:Rollup:RefreshOpenBucket`, default
**true**, wired in `TenantContext.GetRollupOrchestrator`); a frozen bucket is skipped.

The generation pointer makes this fragile: if a **recompute** also claimed the open bucket, its
`BuildUpsertPointer` flip would move that window to generation `N+1`, and the highest-generation-wins
read path would then **mask** the refresh's `generation 0` write — freezing the partial-period total.
So `RecomputeArchiveInternalAsync` **caps `to` at `BucketBoundary.AlignDown(now, …)`** (the start of
the bucket containing `now`), leaving only closed buckets in range; an all-open range becomes a
`Completed` no-op. This keeps the open bucket on the always-fresh generation-0 lane. In a cascade the
open bucket of each level is refreshed cheaply from the level below, so the current period stays live
all the way up (see also the voest cascade Design Decision in `voest-app/CLAUDE.md`).

### Recompute-Work Purge + Fail-Fast on Non-Activated Rollups (AB#4300)

The periodic drain (`RecomputeOrchestrator.TickAsync`) skips any rollup that is not `Activated`, so
queued work on a non-activated rollup would otherwise sit at `Pending` forever. Two guards, both
reachable from `TenantContext` DI:

- **Fail-fast at the enqueue/manual entry points** — `EnqueueBackfillFromSourceAsync` and the manual
  `RecomputeArchiveInternalAsync` path return a `Failed` job immediately when `rollup.Status !=
  Activated`, instead of pre-creating a `Pending` job + range the drain would skip.
- **Purge on disable/delete** — `ArchiveLifecycleService.DisableAsync`/`DeleteAsync` clear
  `PendingRecomputeRanges` and terminate the single active (`Pending`/`Running`/`Swapping`) job as
  `Failed`. `TenantContext` wires `GetArchiveRecomputeStateStore()` + `GetRecomputeJobStore()` into
  the lifecycle service (both optional ctor params, so the backward-compatible construction still
  works). Prevents a delete+re-import that reuses the same rtId from inheriting stale ranges/jobs.

### Bounded Retro Reach — Automatic-Recompute Cap (AB#4196)

Automatic recompute is driven by `RetroactiveWriteDetector.TryBuildDirtyWindow`, called from every
`CrateDbStreamDataRepository` insert path. AB#4196 adds a cap on how far *before* the consumed
watermark a single very-late write may drag that automatic recompute — otherwise one stray old
timestamp schedules a recompute of years of history.

- **Config:** `Archive.MaxRetroactiveReachMs` (per source archive, `System.StreamData` 1.6.8, config
  not runtime-state, `null` = unbounded) + the host `StreamData:Recompute:MaxRetroactiveReachHardLimitMs`
  fleet ceiling on `StreamDataConfiguration`. `CrateDbStreamDataRepository.ResolveEffectiveRetroReach`
  computes `min(perArchive, hardLimit)`.
- **Primary enforcement (detection):** the detector floors the automatic dirty window at
  `consumedWatermark - effectiveCap`; a fully-out-of-reach batch records no window. When any
  retroactive timestamp is dropped it sets `reachCapped`, and the repo logs a `WARN` so the operator
  can run an unbounded manual `recomputeArchive` for the deeper tail.
- **Belt-and-suspenders (propagation):** `RecomputeOrchestrator.PropagateDirtyWindowsAsync` loads the
  source's per-archive cap and passes it to `EnqueueOnDirectDependentsAsync`, which floors each
  dependent's stale-range start at `dependentWatermark - cap` (bucket-aligned). This bounds any dirty
  window recorded before the cap existed. Chain propagation after a committed recompute passes a
  `null` cap — manual/chained recompute stays unbounded, matching `recomputeArchive` /
  `rewindRollupWatermark`.

### Query Time Filter — One-sided Ranges (AB#4617)

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
generation predicate) — never a `{ From: not null, To: not null }` pattern. Before AB#4617 both
boundaries were required at every level, and a one-sided range was dropped **silently**: a
persisted SD-query or a `GetQueryById@1` pipeline node configured with only a start returned the
entire archive. `CompileCountQuery` shares `AppendWhereClause`, so `TotalCount` is scoped
identically to the page.

**Still closed-range-only:** downsampling (needs both to derive the bin width — validated in
`ExecuteDownsamplingQueryAsync`, so `AppendDownsamplingSourceFilters` never sees a one-sided range)
and `TimeWeightedAverage` / `StateDuration` over a raw archive (the LOCF carry-in is defined
relative to the window — explicit `InvalidQueryParameters` guard).

### Downsampling Query — Single Grouped Scan, Bin Axis Built Caller-Side (AB#4713)

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
31-day, 31-series chart (energydemo): 50 buckets 0.5 s, 200 buckets 3.9 s, 670 buckets past the
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
- **Per-series group columns (AB#4233):** grouped and ordered in SQL; the caller's gap-fill emits
  a single null row per empty bin, as the old LEFT JOIN did.
- **Bin geometry — declared grain for rollups, distinct-bin clamp otherwise (AB#4246 / AB#4714 /
  AB#4817):** §7 keys the bin on `window_start` and drops any window whose `window_end` overruns
  the bin, so for windowed archives the bin width must be an *integer multiple of the grain*.
  **Rollup archives** get that by construction:
  `DownsamplingBinQuantizer.QuantizeToGrain(requestedLimit, range, snapshot.Period)` derives
  `merge = round((range/limit)/grain)`, `interval = merge × grain`,
  `effectiveLimit = ceil(range/interval)` and passes the interval *explicitly* into
  `WithDownsampling(limit, from, to, intervalSeconds)` — no probe, no data dependence. The former
  AB#4714 route (probe `COUNT(DISTINCT window_start)` → `Quantize` → re-derive the width as
  `round(range/effectiveLimit)`) is wrong in two ways (AB#4817): the count is of windows *with
  data*, so a single empty grain slot (event-driven series) makes the re-derived width drift off
  the grain (288 five-minute slots with 3 empty → 303 s bins → all but ~5 buckets read null,
  observed on prod-1 as "sensor data stopped hours ago"); and even a complete count only yields a
  grain multiple when the merge divides it evenly (720 windows at merge 7 → 103 bins → 25 165 s
  ≠ 7 h). **Raw archives** keep the probe + clamp-down (AB#4246: a request finer than the data
  only yields sparse bins). **Time-range archives** also stay on the probe route — their `Period`
  is advisory and their windows may be irregular, so a declared grain is no basis for the axis.
  Both quantizer routes assume the query origin (`From`) sits on a grain boundary — the
  resolver-driven path guarantees this because `SeriesResolutionPlanner` returns a grain-multiple
  `EffectiveBucketMs` (see octo-construction-kit-engine) that the frontend aligns the window to.
  `DownsamplingBinQuantizer` is a pure, unit-tested helper (`DownsamplingBinQuantizerTests`).
- **Generation filter (AB#4184):** `AppendDownsamplingSourceFilters` also emits the
  active-generation predicate when `GenerationTracked` — previously the downsampling path missed
  it entirely, so a read during a recompute double-counted the swapped windows.

### CkType.ownerAttributePath Round-Trip + OwnedOnly Owner Attribute (AB#4978)

`CkTypeDto.OwnerAttributePath` (the CK-model-declared owner attribute for owned-only data
permissions — a top-level String attribute compared against the caller's subject id instead of the
server-stamped `rtCreatedBy`) follows the same three-place Mongo round-trip rule as
`isRuntimeState` below: the `CkType` persistence entity declares the property, the import mapping
(`ExecuteImport` → `new CkType {...}`) and the read-back mapping (`TryLookupCkModelAsync`) both
copy it. Inheritance (nearest declared name wins along `derivedFromCkTypeId`) is resolved when the
dependency graph is rebuilt from the read-back DTOs — only the declaring type persists the value.
Pinned by `CkTypeOwnerAttributePersistenceTests`.

Consumers in this layer:

- `DataSecurityFilterRenderer` renders one Or-branch per distinct owner attribute path:
  `In(ckTypeId, <types>) AND Eq(<mongo path>, subjectId)`. `ToMongoFieldPath` translates the CK
  path — scalar values are stored directly at `attributes.{camelCase}`
  (`RtAttributeDictionarySerializer` — no `.value` wrapper) and each Record hop nests another
  `attributes` document (`Owner.UserId` → `attributes.owner.attributes.userId`, mirroring
  `MongoDbAttributePathResolver`). The CK compiler guarantees the shape: single-valued Record
  segments (RecordArray rejected) with a String terminal. Types without a declaration stay on the
  `rtCreatedBy` Eq-branch.
- `MongoDbRepositoryDataSource.AnalyseIndex` synthesizes an implicit ascending index on the owner
  attribute at the declaring type (appended after declared indexes so their uniqueIndexNumber-based
  names stay stable; `PrepareAndCreateIndex` prepends `ckTypeId`/appends `rtState` as usual).
- End-to-end enforcement (read filter, write-guard ownership via the attribute, inheritance to
  derived types) is pinned by `DataPermissionEnforcementTests.OwnerAttribute_ReplacesCreatedByForOwnedOnly`
  using `Test/Ticket` (`ownerAttributePath: AssigneeId`), `Test/EscalationTicket` and the
  record-path case `Test/ReviewTask` (`ownerAttributePath: Owner.UserId`).

### CkAttribute.isRuntimeState Round-Trip (AB#4589)

The runtime CK cache is rebuilt by reading each model **back out of MongoDB**
(`RepositoryDependencyResolver` → `IModelRepository.TryLookupCkModelAsync` → the
reconstructed `CkAttributeDto` → `CkAttributeGraph.IsRuntimeState`), NOT from the compiled
catalog JSON. So any `CkAttributeDto` field that must reach the runtime cache has to survive
the full Mongo round-trip, in **three** places (same pattern as `CkEnum.IsExtensible`):

1. the persistence entity `Repositories/Entities/CkAttribute.cs` must declare the property;
2. `DatabaseCkModelRepository.ProcessCkAttributes` (import, `CkAttributeDto → CkAttribute`) must copy it;
3. `DatabaseCkModelRepository.TryLookupCkModelAsync` (read-back, `CkAttribute → CkAttributeDto`) must copy it.

`isRuntimeState` (AB#4582/AB#4589 runtime-state preservation) originally shipped with the DTO +
compiler emitting it but **all three** Mongo steps missing, so the stored `CkAttribute` doc had no
`isRuntimeState` field and the cache always read `false` — the preservation
(`ImportRtModelCommand.PreserveRuntimeStateAttributesAsync`) silently never fired at runtime even
though its unit tests (which build the graph directly) were green. Fixed by adding the property +
both mappings. **Operational:** a tenant that imported a model before the fix keeps flag-less
`CkAttribute` docs (BSON deserialises the missing bool to `false`); a model **re-import** is
required to repopulate the flag — which is why the fix is paired with a `System.StreamData`
patch bump (`ImportCkModelAsync` short-circuits on an already-installed version). Field name in
Mongo is camelCase `isRuntimeState` (global `CamelCaseElementNameConvention`).

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

### Service-Managed CK Model Auto-import (AB#4294)

`TenantContext.EnsureServiceManagedCkModelsImportedAsync` runs on every tenant-resolve
(`TryGetChildTenantContextAsync`) and imports each host-registered
`IServiceManagedCkModelDescriptor` (e.g. `System.UI`, registered by platform-services) at its
embedded version via the same downgrade-guarded import. A per-process `ConcurrentDictionary`
guard (`_serviceManagedCkModelsAttempted`, keyed `{TenantId}:{modelName}`) makes the import run
at most once per (tenant, model) — it breaks the `ImportCkModelAsync → RetryPendingMigrations →
tenant-resolve → here` recursion. `_streamDataAutoImportAttempted` is the StreamData analogue.

**Guard invalidation on tenant delete/update (AB#4294 fix).** Because the guard is per-process
and keyed only by tenant, a **delete+recreate of a tenant within one process lifetime** (e.g.
`om_initialize_tenant` re-provisioning) would hit the still-armed guard and skip the auto-import,
leaving the fresh tenant without its service-managed model. `ISystemContext.InvalidateTenantResolveImportGuards(tenantId)`
clears both guards for a tenant; the Pre-delete / Pre-update tenant lifecycle consumer
(`PreUpdatePreDeleteTenantConsumer` in `octo-common-services`) calls it next to the CK-cache
unload, so the next resolve re-imports. Regression test:
`ServiceManagedCkModelDescriptorTests.InvalidateTenantResolveImportGuards_ReenablesImportAfterTenantRecreate`.

**Blueprint floor → descriptor version redirect (AB#4294 fix).** The cockpit blueprints declare
`ckModelDependencies: System.UI-[2.2.0,3.0)` as a *pure satisfiability floor* — the actual install
target is the descriptor's embedded version, NOT the floor's lower bound. But `BlueprintService`
still passes the range's `MinVersion` (2.2.0) to `EnsureCkModelInstalledAsync` when the dependency
is unsatisfied, and that version may no longer be published/embedded (only 2.3.0 is). So
`MongoRuntimeRepositoryProvider.EnsureCkModelInstalledAsync` redirects the requested model id to the
matching `IServiceManagedCkModelDescriptor` version when one is registered and is ≥ the requested
version — otherwise a blueprint apply on a tenant that hadn't yet been auto-imported failed with
`Model 'System.UI-2.2.0' not found in one of the registered catalogs`. Regression test:
`MongoRuntimeRepositoryProviderTests.EnsureCkModelInstalledAsync_ServiceManagedModel_RedirectsFloorToDescriptorVersion`.

### Race-safe Tenant Delete — Two-phase Drop

A tenant delete must **delete the tenant metadata record and commit it before dropping the physical
database**. The physical `dropDatabase` is a DDL op that runs *outside* the caller's MongoDB
transaction (Mongo can't drop a database inside a multi-document transaction), so it takes effect
immediately, whereas the `RtTenant` record deletion only becomes visible to other sessions on commit.
The original order (drop DB at `DropChildTenantAsync` → delete record → caller commits) left a window
— measured at ~180 ms live — in which the tenant record was still committed-visible while the
database was already gone. Any concurrent tenant-resolve in that window
(`TryGetChildTenantContextAsync` from a nightly consumer, a health check, or the pre-notification of
an immediately following Create) finds the record, resolves the context, and the **auto-import on
resolve** (`UpdateSystemCkModelAsync` / `EnsureServiceManagedCkModelsImportedAsync` /
`EnsureStreamDataCkModelImportedAsync`) writes `CkModel` into the tenant DB — MongoDB auto-creates the
database (`CkModel` + `SysLock` skeleton). The just-dropped database is resurrected, and the next
tenant `Create` aborts on `IsDatabaseExistingAsync` with *"Tenant database '…' does already exist"*,
rolls back, and leaves an orphan DB with no tenant record → every re-run of `om_initialize_tenant`
deadlocks identically.

`DropChildTenantAsync` is therefore split into two composable phases on `ITenantContext`:

| Method | Runs in caller's txn? | Does |
|---|---|---|
| `DeleteChildTenantMetadataAsync(session, tenantId)` | yes | Pre-delete notification + delete `RtTenant` from current & system repos. Returns a `TenantDeletionHandle(DatabaseName, CorrelationId)`. |
| `DropTenantDatabaseAsync(handle, tenantId)` | no (DDL) | Physical `dropDatabase` + post-delete notification (reusing the handle's correlation id). |

The tenant delete REST endpoint (`TenantsController.Delete`) now calls
`DeleteChildTenantMetadataAsync` → `CommitTransactionAsync` → `DropTenantDatabaseAsync`, so the record
is durably gone before the drop and no resolve can resurrect the DB. `DropChildTenantAsync` is kept as
a single-call convenience (`DeleteChildTenantMetadataAsync` + `DropTenantDatabaseAsync`, record-delete
now ordered before the drop) for callers with no concurrent resolver: create-rollback
(`CreateChildTenantAsync` blueprint path), `ClearChildTenantAsync`, `TenantBackupService` temp cleanup,
and the integration tests. Complements the AB#4294 guard invalidation above (which handles the
*same-process delete+recreate* import-guard, a different facet of the same resurrection mechanism).

`DropTenantDatabaseAsync` additionally **drops the tenant's MongoDB user** and drops the database
under **both spellings** — normalized and as stored in the record (AB#4762). Both matter:
`dropDatabase` does *not* remove the account — it lives in the authentication database — so every
delete used to leave a live credential behind that a database re-created under the same name would
silently inherit; and `dropDatabase` is case-sensitive while `IsRepositoryExistingAsync` compares
case-insensitively, so dropping only one spelling missed either the normalized physical database of a
mixed-case record or the mixed-case physical database a legacy attach adopted. MongoDB forbids two
databases differing only in case, so at most one spelling exists. The user drop is best-effort: it is
logged, never allowed to fail the delete.

**Dropping a tenant for good also drops the CrateDB tables of its archives** (AB#4255) — but only
when the caller says so. `DeleteChildTenantMetadataAsync` / `DropChildTenantAsync` take
`dropStreamData` (default **false**): with `true` the archives of the child are collected into the
`TenantDeletionHandle.StreamDataArchives` *before* the record is deleted (the last moment the child
resolves — the entities are gone with the database), and `DropTenantDatabaseAsync` drops exactly those
archives' tables (data table + `__genmap` side-table, each `DROP TABLE IF EXISTS`, all statuses) through
`IStreamDataRepositoryFactory.DeleteArchiveTablesAsync(tenantId, rtIds)` after the Mongo drop. The
guard in `DisableStreamDataAsync` (see *StreamData: Archives and Rollups*) only ensures nothing is
*live* by then. Who passes `true`: the tenant delete REST endpoint and `ClearChildTenantAsync` (Clear
empties the tenant; its archive entities go with the database, keeping their tables would only orphan
them). Who keeps the default: `TenantBackupService.RestoreTenantAsync` (restore over an existing
tenant is a *database swap* — the same archives exist afterwards and find their tables again, so a
Mongo-only restore keeps the stream data), the blueprint create-rollback, the deleting-settle sweep in
octo-common-services (builds its handle from the lifecycle record, no archives) and the test cleanups.
`DetachChildTenantAsync` keeps everything for a later attach.

Two rules of that drop are deliberate. **Per-archive, never "everything in the tenant's schema"**:
`TenantSchema.SchemaName` strips `-` and `_` from the tenant id, so `acme-corp`, `acme_corp` and
`acmecorp` share one CrateDB schema — a schema-wide drop (the previously unused
`DeleteStreamDataDatabaseAsync`) would take a neighbour's tables with it. **Best-effort like the user
drop**: skipped (with a warning naming the tables) when no factory is registered or `StreamData:Enabled`
is false at instance level; a failure is an ERROR log listing the `archive_<rtId>` tables to drop by hand
(every statement is idempotent). It runs only after the Mongo drop succeeded — when `dropDatabase`
itself throws, the user drop and the table drop are both skipped and the exception propagates — and it
goes through the CrateDB resilience pipeline: the factory stops at the first failure, so with CrateDB
unreachable a Delete/Clear of a tenant *with archives* blocks for up to ~2 min (timeout × retries) before
logging the error; it never fails the drop, and tenants without archives never touch CrateDB.

**The resurrection window is not fully closed, and since AB#4762 it bites harder.** Background work
that outlives the delete — above all a `tenant_setup_retry` entry, whose retry loop keeps calling
`SetupAsync` for a tenant that no longer exists — re-creates the database as an empty `CkModel` +
`SysLock` shell seconds after the drop. The create path no longer reclaims such a shell (it used to,
by dropping it — that was the AB#4762 data-loss bug), so the leftover permanently blocks its own
database name behind a deliberately reason-free conflict. `TenantsController.Delete` therefore clears
the tenant's setup-retry entries via `ITenantSetupRetryStore.ClearAllForTenantAsync`. A peer service
still racing its own CK-model import into the tenant can produce the same shell; when a database name
is refused and nothing seems to own it, look for that shell and remove it.

### Tenant Namespace Gate — one authority for tenant ids and database names (AB#4762 / AB#4763)

`EnsureTenantNamespaceAvailableAsync` is the single gate for both platform-wide namespaces, shared by
`CreateChildTenantAsync` and `AttachChildTenantAsync`. It runs **before any side effect** — including
the pre-create notification — because everything after it sits inside the `try` whose `catch` rolls the
tenant back, and that rollback used to drop whatever database the name pointed at.

Checks, in order: the configured system tenant id (reserved — the system tenant has no `RtTenant`
self-record, so a registry lookup alone cannot cover it), the configured system database name,
MongoDB's own `admin` / `local` / `config`, the platform-wide registry by tenant id, the registry by
database name, and finally physical existence — required for attach, forbidden for create.

Two rules to preserve when touching it:

- **Both conflicts are uniform and reason-free.** Exactly two conflict messages exist —
  `Tenant ID '<x>' is already in use.` and `Database name '<x>' is not available.` — and every reason
  collapses into one of them, including "does not exist" on the attach path. A distinguishable answer
  turns the endpoint into a cluster-wide existence oracle for callers who cannot see the colliding
  resource. The real reason goes to the log, never to the response, and rejections are **logged, not
  audited**: an audit event per rejected attempt would be an unbounded write amplifier into the system
  database, since nothing rate-limits the callers. (Format validation is different: a tenant id or
  database name that is syntactically invalid throws a descriptive `ArgumentException` before the
  conflict checks — that reason is about the caller's own input, leaks nothing about other tenants,
  and both REST endpoints map it to 400, not 409.)
- **The rollback is gated on `databaseCreated`**, a local set only after `CreateTenantInternalAsync`
  returns. The exists-check inside that method is kept as a TOCTOU net and deliberately throws while
  the flag is still `false`, so a racer's database is never dropped.

Registry lookups (`GetRtSystemTenantAsync`, `GetRtSystemTenantByDatabaseNameAsync`) query the
**platform-wide** registry via `GetSystemTenantRepositoryAsAdmin()`; the subtree-local
`GetRtTenantAsync` is a different thing and the two must never be swapped — they were byte-identical
copies, which is what made the "global" uniqueness check blind (AB#4763). Neither imposes a sort
order: which row wins decides which database a tenant resolves to and which one a delete drops, so
ordering already-ambiguous rows could move a live tenant. Ambiguity is **logged as a warning**
instead, and the registry deletes in `DetachChildTenantAsync` / `DeleteChildTenantMetadataAsync` are
qualified with the database name so a leftover duplicate cannot make them unregister a different
parent's tenant. The qualification matches the name **as stored in the registry being deleted from**
(verbatim for the subtree-local record — the pre-AB#4763 attach wrote the operator's raw casing
there — and normalized for the system registry, which has always been written normalized); a
normalized filter against the local registry silently missed every legacy mixed-case record. Note the
gate prevents new duplicates but nothing at the database level enforces uniqueness (the Tenant CK
type's `TenantId` index is not unique), so two creates racing the gate in separate transactions can
still both commit — `FirstAndWarnOnDuplicates` surfaces the result.

Detach removes the platform-wide row too, so detach and attach are exact inverses. Without that the
now-global id check would reject every re-attach, and a "detached" tenant stayed fully resolvable from
the system context.

### Tenant Database Ownership Marker — cross-instance attach guard (AB#4945 / Epic AB#4944)

The namespace gate's registry lookups only cover the OWN instance's system database. On a shared
MongoDB server a **second OctoMesh instance** (different `SystemDatabaseName` — the instance
separator per Epic AB#4944) could therefore attach a tenant database this instance still owns:
two controllers, trigger schedulers and lifecycle watchdogs on one tenant DB — split-brain.

`TenantOwnershipStore` (`Repositories/TenantOwnership/`, non-CK collection `tenant_ownership` in
the **tenant database itself**, one document with fixed id `owner`) closes that gap. The marker
travels with the database, so every instance that can physically reach it can read who owns it.
Instance identity = the normalized `SystemDatabaseName` (ordinal compare).

Lifecycle:

| Point | Effect |
|---|---|
| `CreateChildTenantAsync` | Stamps right after the physical create — owned from birth; a stamp failure fails the create and the rollback drops the database we provably created. |
| `AttachChildTenantAsync` | The gate consults the marker (attach mode, after the existence check): foreign owner → **uniform, reason-free rejection** per the AB#4763 rule (the owner is logged, never returned — no cross-instance oracle). **STRICT by decision: no force override** — the owning instance must detach first. On pass, the attach stamps first thing in the try; a marker THIS call created is removed again in the catch (best-effort) so a failed attach never leaves the database locked for other instances. |
| `DetachChildTenantAsync` | Removes the marker LAST inside the try — the one sanctioned ownership handover. Deliberately not best-effort: a removal failure aborts the caller's transaction, the registry rows are restored, and the database stays consistently attached here. |
| Tenant-resolve (`TryGetChildTenantContextAsync`) | Lazy claim: stamps insert-if-absent (once per process per tenant, guard cleared by `ClearTenantResolveImportGuards`), so the existing fleet becomes owned without a migration script. Insert-if-absent means a pre-guard double attachment across two instances is won by the first writer and never flaps. |
| `ClearChildTenantAsync` / delete | Clear re-stamps via its internal `CreateChildTenantAsync`; delete drops the database and the marker with it. |

The collection is deliberately NOT in `InfrastructureCollections` — that registry is the
system-database shell allowlist (AB#4854), and the marker is never written into a system database
(the gate reserves the system database name before any marker access). Reads never materialize a
database: every caller operates on a database whose existence the gate has already established.

Known residual: `TenantBackupService.RestoreTenantAsync` restores whatever marker the backup
carries (own-instance backups restore their own marker — correct); restoring another instance's
backup into this instance requires a detach-style marker removal first, which is intentional.

Tests: `TenantOwnershipGuardTests` (create stamps; detach removes + re-attach re-stamps;
foreign-owned attach conflicts uniformly with the foreign claim untouched; unstamped legacy DB is
adopted and stamped; tenant-resolve lazily stamps an unmarked DB).

### Tenant Lifecycle Store — `EnsureCreatingAsync` is an atomic branch on the stored state (AB#4690)

`TenantLifecycleStore` (`Repositories/TenantLifecycle/`, non-CK collection `tenant_lifecycle` in the
SYSTEM database) is written from two directions at once: `SetupTenantAsync` calls
`EnsureCreatingAsync` on **every** tenant setup run, while the reconciler in `octo-common-services`
concurrently claims tenants with `TryClaimForReconcileAsync` (single-flight via a 2-minute lease plus
an `AttemptCount` retry budget). In a live cluster `PosCreateTenant` / `PosUpdateTenant` events arrive
continuously, so those two writers overlap constantly.

`EnsureCreatingAsync` is therefore a **single `UpdateOneAsync` with an aggregation-pipeline update**
(`$set` + `$cond`, upsert), not the former `Find` → build record → `ReplaceOneAsync` round trip. Three
branches, decided on the state that is already stored:

| Stored state | Effect |
|---|---|
| `Active` | Metadata refresh only (`LastTransitionUtc`, `DatabaseName`) — a healthy tenant re-running setup is never downgraded. |
| `Creating` | Re-opens the phase (`SetupStarted`, `LastError` cleared) but **leaves `AttemptCount`, `LeaseOwner`, `LeaseUntil` untouched** — that bookkeeping belongs to whoever is currently driving the tenant. |
| missing / `Deleting` / `Failed` | New creation cycle: attempt budget, lease and last error are reset; `CreatedUtc` is preserved via `$ifNull`. |

Why it matters (AB#4690): the old read-modify-write cleared `LeaseOwner`/`LeaseUntil` on every setup
run and could revert a concurrent claim's `AttemptCount` increment (lost update). A tenant stuck in
`Creating` therefore stayed at `AttemptCount = 0` with no lease forever — it never exhausted the retry
budget, never reached `Failed`, and never recorded a diagnosable `LastError`, which made the reconciler
look dead when it was in fact running.

The pipeline addresses fields by their **stored element name** (the typed builders cannot express
`$cond`), so the names live in `TenantLifecycleStore.Fields` and `TenantLifecycleStoreTests`
pins them against the class map — a property rename or a change to the camelCase convention would
otherwise make the update write to fields nobody reads, silently and without error.

### Repository-Client Cache Invalidation on Tenant Delete (AB#4690)

`UserRepositoryAccess` / `AdminRepositoryAccess` cache one `MongoRepositoryClient` — and therefore one
`MongoClient` with its own connection pool — **per database name**, for the lifetime of the process.

Dropping a tenant also drops its database user. MongoDB then invalidates the authentication of every
connection already open in those pools, and **the driver never re-authenticates an existing
connection**: each one keeps failing with error 13 (`"... requires authentication"`) even after the
tenant is re-created and the user exists again. Connections are only retired when the server or the pool
happens to close them, so a re-created tenant could be unusable for hours — this is the root cause of
AB#4690, where a delete + recreate under the same database name left Identity unable to read the new
tenant database, so its `SetupTenantAsync` aborted and the tenant ended up with no roles.

Both accessors therefore expose `Invalidate(databaseName)`, which **evicts** the cached client so the
next resolve builds a fresh, freshly-authenticated one. Reached through
`ISystemContext.InvalidateTenantRepositoryClientsAsync(tenantId, databaseName?)`, which resolves
the database name from the tenant record when it is not supplied.

**Eviction only — never dispose.** The first AB#4690 iteration disposed the evicted client
(`Dispose` → `Cluster.Dispose`). That was a regression: handed-out clients are captured by live
`TenantContext` / `MongoDbRepositoryDataSource` instances beyond the cache, and disposing tears the
cluster down underneath them — every in-flight operation then fails with
`ObjectDisposedException('CoreServerSessionPool')`. Observable as: a sequential CK batch import
(LibraryStatus **FixAll**) whose per-import `PosUpdateTenant` event disposed the tenant's client
between two batch steps, so each FixAll run imported exactly one model and then failed at the next
`AcquireModelImportLockAsync` (staging-1 meshtest, test-2 tenant-setup storms, 2026-08-05). The evicted
client is collected once its holders let go; its stale connections are closed by server/pool idle
handling.

Called from two places:

| Where | Why |
|---|---|
| `TenantContext.DropTenantDatabaseAsync` (engine, explicit database name) | The process performing the drop, independent of any event delivery. |
| `PreUpdatePreDeleteTenantConsumer` on `PreDeleteTenant` + `PosCreatePosUpdateTenantConsumer` on `PosCreateTenant` (`octo-common-services`) | Every other service: pre-delete while the tenant record still exists so the name resolves; post-create closes the window where a resolve between the pre-delete event and the physical drop re-populated the cache. NOT called on `PosUpdateTenant` — that event fires on every CK model import and eviction there would churn a fresh client per import for no benefit (an update drops no database user). |

### Tenant Setup Retry Store — durable per-service retry (AB#4690)

`TenantSetupRetryStore` (`Repositories/TenantLifecycle/`, non-CK collection `tenant_setup_retry` in the
SYSTEM database) holds one document per **(service, tenant)** whose default-configuration setup threw.

Deliberately separate from `tenant_lifecycle`: that record describes the tenant and has a single writer
(the asset repository), whereas *every* service runs its own `SetupTenantAsync` and needs its own retry
bookkeeping. The `serviceId` (the creator's assembly name by default) keys them apart, so two services
can have an independent pending entry for the same tenant.

| Method | Purpose |
|---|---|
| `RecordFailureAsync` | Upsert on failure: `$inc` attempt count, store the error, stamp `lastAttemptUtc`, release the lease so the entry is claimable again after the retry interval. |
| `ClearAsync` | Delete after a successful setup. |
| `TryClaimAsync` | Atomic find-and-update claim of the longest-waiting entry that is inside its attempt budget and whose last attempt is older than the retry interval. |
| `ReleaseLeaseAsync` / `ListAsync` | Lease release and diagnostics. |

Consumed by `DefaultConfigurationCreatorServiceBase` in `octo-common-services`: `SetupAsync` records on
failure and clears on success, and `RetryFailedTenantsAsync` drains the queue from the
`FailedTenantRetryBackgroundService` timer that every service already runs. Entries that exhaust the
attempt budget stay in the collection for operators but are no longer handed out.

Background: before this, a tenant setup that failed once was logged and forgotten — services on the base
creator had no retry at all. A tenant whose database was briefly unreachable right after a delete +
recreate under the same name (Mongo `errorCode 13`) therefore stayed half-provisioned until the pod was
restarted; for Identity, which owns the roles/groups seed, that meant no administrator could be
provisioned for that tenant at all.

### Infrastructure-Only Shell of the System Database (AB#4854)

The engine's own plumbing writes non-CK bookkeeping into the SYSTEM database — `tenant_lifecycle`,
`tenant_setup_retry`, `display_rule_sweep`, `SysLock` (registry: `InfrastructureCollections`). On a
virgin server such a write may materialize the system database BEFORE the system-tenant bootstrap
runs; in r3.4.93 the bootstrap then refused over the "existing" database and the datasource user was
never created — every fresh install wedged permanently on a MongoDB authentication error.

Rules that keep this closed:

- **Reads and update-only claims never materialize the database.** The stores ensure their indexes
  only on document-creating operations (`GetCollectionAsync(ensureIndexes: ...)`).
- **A database containing nothing but infrastructure collections is a "shell"** —
  `IsDatabaseMaterializedOnlyByInfrastructureAsync`. `IsSystemTenantExistingAsync` reports a shell
  as not existing (checked BEFORE the CK-model read, which would otherwise fail with an
  authentication error on the missing datasource user); `ISystemContext.IsSystemDatabaseBootstrappableAsync`
  answers absent-or-shell; the system bootstrap's refusal guard (AB#4762) exempts shells.
- **The seed-decision guard lives in `UpdateSystemCkModelAsync`, not in callers.** Seeding the model
  (as admin) into a shell would make the shell look like a real system database and skip the
  bootstrap — the only creator of the datasource user — forever. The check sits immediately before
  the seed so a shell that materializes after a caller's earlier probe cannot slip through a
  check-then-act window; `EnsureSystemCkModelAsync` deliberately has no guard of its own.
- **The bootstrap rollback drops only what it created from nothing.** `CreateSystemTenantAsync`
  gates `CleanupFailedTenantCreationAsync(dropDatabaseAndUser: ...)` on
  `databaseCreated && !databaseExisted` (mirrors `CreateChildTenantAsync`, AB#4762): a failed
  attempt that started over a pre-existing shell keeps the shell (it holds other services' durable
  bookkeeping — setup-retry queue, lifecycle records, locks), and a racing replica whose create
  throws inside the try can never drop the winner's database.
- **The allowlist is closed.** Any new pre-bootstrap writer into the system database must either use
  a collection registered in `InfrastructureCollections` or run after the bootstrap — anything else
  re-arms the fresh-install wedge. Renames are compile-protected through the shared constants;
  additions are not, so review them explicitly.

Pinned by `SystemTenantVirginBootstrapTests` (isolated `VirginSystemFixture` container).

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
