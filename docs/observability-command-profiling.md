---
description: "MongoDB command profiling: metrics, slow-query log, redaction, explain capture, index suggestions, fingerprinting"
applies_to: src/Runtime.Engine.MongoDb/Configuration/**, src/Runtime.Engine.MongoDb/Repositories/MongoDb/**
---

## Observability — MongoDB Command Profiling

`MongoCommandObservability` (in `Repositories/MongoDb/Generic/`) subscribes to
`CommandStartedEvent` / `CommandSucceededEvent` / `CommandFailedEvent` on the singleton
`MongoClient` and acts as the Community Edition replacement for the Atlas / Enterprise
Performance Advisor. It emits OpenTelemetry instruments and structured slow-query logs
without changing any application code path.

### Metrics

Meter name: **`Meshmakers.Octo.MongoDb`** (registered in
`ObservabilityBuilder` (repository octo-common-services) — every service that calls
`builder.AddObservability()` exposes the metrics over Prometheus automatically).

| Instrument | Type | Tags | Purpose |
|------------|------|------|---------|
| `octo.mongodb.command.duration` | Histogram (ms) | `command_name`, `database`, `status` | Latency distribution per command per tenant DB |
| `octo.mongodb.command.errors` | Counter | `command_name`, `database`, `error_code` | Failure counts, tagged with the Mongo error code (e.g. `112` for WriteConflict) |
| `octo.ck.embedded_import.skipped` (Prometheus `octo_ck_embedded_import_skipped_total`) | Counter | `model`, `reason` | Embedded CK model import skipped, tenant has a newer version (see *Embedded CK Model Import Guard*); counted once per process per (tenant, embedded, installed) |
| `octo.ck.explicit_import.downgraded` (Prometheus `octo_ck_explicit_import_downgraded_total`) | Counter | `model` | Explicit `ImportCk` replaced a newer installed version |
| `octo.ck.model.revalidated` (Prometheus `octo_ck_model_revalidated_total`) | Counter | `result` (`recovered` / `still_failed`) | Post-import re-validation of `ResolveFailed` models |

The three `octo.ck.*` counters use curly-brace annotation units (`{import}`, `{model}`), which the Prometheus exporter
does not append to the series name. A plain unit such as `count` would add a `_count` suffix
(`octo_ck_embedded_import_skipped_count_total` — what the G1 E2E saw before D-G1-2).

The `tenantId` is deliberately **not** a tag — `database` is used instead as the
low-cardinality attribution dimension (it equals the tenant database name).

### Slow-Query Logging

Two thresholds on `OctoSystemConfiguration`:

| Property | Default | Behaviour |
|----------|---------|-----------|
| `SlowQueryThresholdMs` | `100` | Above this, the command is logged at **WARN** as: `Slow MongoDB command: <name> <target> on <db> took <ms>ms (requestId=<id>)`. `0` disables slow-query logging (metrics still emitted). |
| `SlowQueryFullCommandLogMs` | `1000` | Above this **additional** threshold, the truncated BSON command body (credentials redacted, see *Credential redaction in every rendered command*) is included in the log line — useful to capture the exact filter/pipeline for very slow operations. |
| `SlowQueryCommandPreviewBytes` | `2048` | Truncation limit for the BSON preview. |

The `<target>` is extracted from the first BSON element value of the command (e.g.
`aggregate=rt_entities`, `find=ck_types`) — enough to identify the affected collection
without dumping the full pipeline.

Env-var override example: `OCTO_System__SlowQueryThresholdMs=50`.

### Credential redaction in every rendered command

Every place that turns a command into text goes through ONE step, `MongoCommandSecretRedactor.ToRedactedJson`: the
driver-level Debug command log of `MongoRepositoryClient` (`MongoCommandObservability.RenderCommandForLog`), the slow-query
WARN line with the command body, the slow-query buffer behind `GET {tenantId}/v1/Diagnostics/slow-mongo-queries` (Studio
Diagnostics) and the explain preview (`SlowQueryExplainParser`, the parsed filter carries values). It masks:

| What | Recognised by | Becomes |
|---|---|---|
| Stored secret envelope `{ _t: "OctoSecret", e: "enc:v2:..." }` | shape (no CK knowledge) | `{ _t: "OctoSecret", e: "***" }` |
| Envelope string `enc:v1:` / `enc:v2:` | prefix | `"***"` |
| Value of a **sensitive attribute** | name, in the attribute context | every leaf below it `"***"`, field names and array lengths stay |

Sensitive attributes (`MongoCommandSensitiveAttributeNames`):

- **Known credential and token fields, without CK knowledge** (`KnownCredentialNames`): `passwordHash`, `securityStamp`,
  `userTokens` (System.Identity User: authenticator key, recovery codes, OTP challenges), `clientSecrets`, `apiSecrets`
  (hashed client / API resource secrets), `xmlData` (Data Protection key ring), `payload` and `referenceId` (OpenIddict token:
  protected payload, hashed reference id), `ticket` and `sessionKey` (server-side session: data-protected ticket, cookie key),
  `data` and `grantKey` (persisted grant: serialized grant, hashed handle). They apply everywhere, also in hosts without a CK
  cache and for tenants whose cache is not loaded. `data`, `payload` and `ticket` are generic: a same-named attribute of any
  model is masked too (accepted).
- **From the CK cache of the tenant** (`CollectFromCkCache`): the cache is keyed by tenant id and looked up with the
  command's database name, so this applies to tenants whose **database name equals the tenant id** (the platform default,
  the same rule the diagnostics endpoint filters by; decided for this rule). Other databases get the known names and the names
  already found. Type and record attributes with
  `access: Hidden`, a `securitySensitive` definition or value type `Secret`; on types also effective ownership `Secret`
  (record members with ownership `Secret`, e.g. `UserToken.Value`, are covered by their owning attribute and their name is too
  generic). Read at most once per 60 s per database, retried with the next command while the cache is not loaded; the set only
  grows (a name found for one tenant is masked in every database the client renders - costs detail, never a leak).
- **Attribute context only**: an element of a document that is the value of an `attributes` element (entity, record, a `$set`
  of the whole map), or a dotted path segment after an `attributes` segment (`attributes.passwordHash`,
  `attributes.userTokens.$[i0].attributes.value`, `attributes.clientSecrets.attributes.value` in a filter). A CK attribute
  called `filter` therefore never masks the command's `filter`.

Rules: the walk is copy-on-write (the driver's live command is never mutated) and runs only when the JSON contains a
secret marker, or `attributes` and one of the names; a failed redaction drops the preview (`DroppedPreview`) instead of
rendering it unredacted; truncation happens after redaction. A new place that renders command text must use
`ToRedactedJson` / `TruncateBson` with `GetFor(database)`. Not covered: the exception text of `MongoDB command failed`
(server error message; Hidden attributes are never indexed, so no duplicate-key text can carry one) and attributes of
association roles (Hidden is not allowed there). Tests: `MongoCommandSensitiveAttributeRedactionTests`,
`MongoCommandSensitiveAttributeNamesTests`, the cases in `MongoCommandObservabilityTests` (a User insert and a client
secret write leave no dummy value in the Debug rendering, the WARN line and the buffer) and
`RoundTrip/MongoCommandRedactionCkCacheTests` (names from the kitchen sink's cache rebuilt from MongoDB).

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

**Raw-BSON retention (refined by a later fix).** `CommandStartedEvent.Command` is only
valid while the started-event handlers run: for bulk-write commands (insert/update/delete with
OP_MSG payload-type-1 sections) it is a `RawBsonDocument` — or contains
`RawBsonDocument`/`RawBsonArray` slices — over the connection's send buffer, which the driver
returns to its pool right after the event. `MaterializeForRetention` therefore snapshots the
command before it enters the `_pending` map: raw arrays under the **bulk payload fields only**
(`documents`, `updates`, `deletes`) are replaced with a `"<N raw documents elided>"` placeholder
(no megabyte bodies retained or fingerprinted, fingerprint stable across batch sizes); every
other raw array — above all an aggregate's `pipeline` — is materialized element-wise into an
independently-owned `BsonArray`, and small raw sub-documents (lsid, writeConcern) are
deep-cloned. Background: the original fix elided *every* raw array, so a raw
`aggregate` command lost its pipeline and the explain probe sent
`{explain: {..., pipeline: "<7 raw documents elided>"}}` — server error 14 (`'pipeline' option
must be specified as an array`), a WARN+ERROR pair per cooldown window, and a shape-blind
fingerprint. `DispatchExplain` additionally guards via `ContainsElidedPlaceholder`: a command
still carrying a placeholder (raw bulk update/delete) is stamped `Unsupported` in the explain
cache instead of round-tripping a guaranteed failure. Defense in depth: `TruncateBson`,
`SlowQueryFingerprinter.Fingerprint`, and the explain-dispatch `DeepClone` all catch
`ObjectDisposedException` and degrade gracefully — before the raw-array fix, identity-services startup
(bulk seeding, everything above the slow threshold) flooded the log with ERROR entries and lost
exactly those entries from the slow-query buffer.

### Exception Safety

Driver command events fire on the driver's own thread pool. Every callback in
`MongoCommandObservability` is wrapped in try/catch and the inner `SafeLogError` swallows
any logger throws — a broken sink must never poison the driver's event pipeline.

### Per-Request Accumulator

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
  silently no-ops — only metrics and slow-log fire there, identical to the behaviour before the per-request surface.
- Heartbeat commands are still filtered before reaching the accumulator, matching the listener's
  ignored-commands set.

In OctoMesh today the scope is opened automatically by `MongoCommandSurfaceMiddleware`
(`octo-common-services` Observability), and consumed by `MongoStatsListener`
(`octo-asset-repo-services` GraphQL pipeline). REST responses get headers
`X-Octo-MongoDb-Duration-Ms` / `X-Octo-MongoDb-Command-Count`; GraphQL responses get
`extensions.mongoDb = { totalMs, commandCount, slowestMs, slowestCommand }`.

### Slow-Query Buffer

In addition to metrics, slow-log, and the per-request scope, `MongoCommandObservability`
also captures every slow command (above `SlowQueryThresholdMs`) into a process-wide
**ring buffer** so the Refinery Studio Diagnostics surface can show recent slow queries
without scraping logs:

- `SlowQueryEntry` POCO — Timestamp, CommandName, Target, Database, DurationMs, RequestId,
  CommandBsonPreview (redacted before it is stored), Success, ErrorCode
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

### Async Explain Capture

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

**Wire envelope must be stripped before wrapping.** `CommandStartedEvent.Command` is
the command *as it goes over the wire*, so it carries `$db`, `$readPreference`, `$clusterTime`,
`lsid`, … `RunCommandAsync` attaches `$db` itself, so wrapping the capture as-is made the server
reject **every** dispatch with `BSON field 'aggregate.$db' is a duplicate field` — the probe
never produced a plan from its introduction (2026-06-22) until this fix, which also means no COLLSCAN
detection and no index suggestions for any shape. It only became visible
after the raw-array fix stopped the earlier error-14 failure on the elided pipeline array; before that the
dispatch died one step sooner. `StripWireEnvelope` removes **every `$`-prefixed top-level field**
(prefix-based on purpose — the driver may attach further ones in a future version) plus the
session/transaction and concern fields in `WireEnvelopeFields`; the query itself (filter, sort,
pipeline, hint, collation) is untouched, or the explained plan would not be the one that was
measured. The strip happens on the explain clone only — the buffer preview and the fingerprint
are computed from `ctx.Command` and are meant to show the command as it actually went over the
wire. `Explain_DispatchedCommand_CarriesNoWireEnvelope` pins the shape of the **dispatched**
document, which no test covered before: that gap is why two consecutive defects shipped unnoticed in the same three lines.

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

### Unused-Index Analysis via $indexStats (Stage 3)

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

### CK-YAML Index Suggestions for COLLSCAN (Stage 2D)

For every Stage 2C suggestion that targets a known CK type, the suggester additionally
emits a **CK-YAML snippet** the operator can paste into the CK type's source YAML under
its `indexes:` array. Subsequent model imports re-create the corresponding MongoDB index
via the existing `CkTypeIndexDto` → `MongoDbRepositoryDataSource.PrepareAndCreateIndex`
machinery, so the index survives re-imports and cross-tenant migration.

| Component | Purpose |
|---|---|
| `MongoDbAttributePathResolver.TryReverseToCkPath` | Inverse of `ResolveToMongoDbFieldPath`. Strips `attributes.` prefix and `.value` suffix, walks even-indexed camelCase segments and odd-indexed `attributes` separators back into PascalCase CK attribute names. Returns null when the path isn't a CK attribute (e.g. `ckTypeId.fullName`, `_id`) or fails to resolve in the provider. |
| `CkYamlIndexSnippetWriter.Write` | Hand-formatted YAML matching the shape used by real CK types — `indexes: - indexType: Ascending - fields: - attributePaths: [...]`. Leading comment carries the audit trail (CK type full name). |

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

### MongoDB Index Suggestions for COLLSCAN

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
- Writing the suggestion back as a CK attribute flag. CK-YAML snippets under `indexes:` are emitted (see *CK-YAML
  Index Suggestions for COLLSCAN (Stage 2D)*); an `Indexed: true` attribute flag does not exist.
- Per-branch indexes for `$or`. One compound covering the union with a Notes caveat.
- Index-name length cap at 127 bytes (Mongo's hard limit) with SHA-256 short-hash suffix
  for truncated names so similar shapes don't collide.

### Pipeline Fingerprinting

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

- Stage 1: slow-log + OTel histograms
- Per-request surface: GraphQL extension + REST headers
- Studio surface: ring buffer + Diagnostics page
- Stage 2A: pipeline fingerprinting + grouped view
- Stage 2B: async `explain()` capture + COLLSCAN detection
- Stage 2C: MongoDB index suggestions for COLLSCAN
- Stage 2D: CK-attribute reverse mapping + CK-YAML emission
- Stage 3: `$indexStats` unused-index analysis (this section)
