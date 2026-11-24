---
description: "CK model import: migration on import, hidden backstop, import guard, ResolveFailed handling, blueprint ranges, auto-import"
applies_to: src/Runtime.Engine.MongoDb/Services/**, src/Runtime.Engine.MongoDb/CkCache/**, src/Runtime.Engine.MongoDb/Blueprints/**, src/Runtime.Engine.MongoDb/Fixups/**, src/Runtime.Engine.MongoDb/EmbeddedCkModelImportGuard.cs
---

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

**Migration origin includes `ResolveFailed` models:** `GetSchemaVersionsDirectAsync` lists the `Available` **and** the
`ResolveFailed` stored models, and `MigrationOriginVersions.Select` picks per model name the version the data is at
(Available wins over ResolveFailed, then the highest version; `Importing` is never an origin). A model goes `ResolveFailed`
when another service replaces a dependency it pins exactly (the 3.x `System.Communication` pinning `System.Bot-[3.4.0]` after
`System.Bot` 3.5.0 was imported); its data is still at that version, so the import of the current model must migrate from it,
independent of the order in which bot-services and the comm-controller start. Only the imported model is migrated; other
ResolveFailed models are untouched and heal through the existing re-validation. Tests: `MigrationOriginVersionsTests`
(selection) and `CkModelImportGuardTests.ResolveFailed*Origin*` (against Mongo); the end-to-end 3.x case lives in
`octo-communication-controller-services` (`SystemCommunication4MigrationTests`, bot-first and comm-first).

**Design note:** `GetSchemaVersionsDirectAsync` queries the database directly (not through `IRuntimeRepositoryProvider`) to avoid recursion, since `TryFindTenantContextAsync` itself calls `UpdateSystemCkModelAsync`.

### Key Components

| Class | Description |
|-------|-------------|
| `MongoRuntimeRepositoryProvider` | Implements `IRuntimeRepositoryProvider` using `ISystemContext` |
| `MongoTenantBlueprintHistory` | MongoDB-based blueprint history storage |


### Hidden Backstop and v1 / Rollback Compatibility (CK v2 F1.3-S4)

- **Hidden is never indexed (review N6).** Unlike Secret, a Hidden attribute is stored in plain text and has no
  runtime backstop, so a unique index would reveal values through duplicate-key errors and a text index through
  search hits. `PrepareAndCreateIndex` drops every index path that reaches a Hidden assignment (terminal attribute or a
  record attribute on the way, `DatabaseAttributeMetadataProvider.ReachesHiddenAttribute`, matched
  **case-insensitively** like the database resolves field names) and logs ERROR `Skipping Hidden attribute ...`; an
  index left without fields is not created. The compiler rejects it already (message 106); this guards hand-edited and
  older compiled models (`HiddenAttribute_IsNeverIndexed_EvenThroughACaseInsensitivePath`, mutation-checked).
  **Every type of the collection counts (review M-M1):** all types stored in one collection (base chain, root, every
  descendant) share the attribute fields, so a path is skipped when *any* of them reaches a Hidden assignment — a
  derived type indexing a Hidden attribute it inherits, or a sibling's Hidden assignment of a field another type
  indexes, also through the merged text index (`HiddenAttributeOfAnotherTypeInTheCollection_IsNeverIndexed`,
  mutation-checked against the old indexing-type-only check). Case variants of one name keep the Hidden assignment
  (review E-L7).
- **The backstop does not rely on the engine graph's text-index merge.** Index maintenance merges the text indexes of
  all types of a collection itself (`AnalyseIndex`, from the stored `CkType` documents); `CkTypeGraph.MergeTextIndexes`
  in the engine (which, on main, never merges a derived text index into a root that has its own) is not involved. The
  read-back DTOs (and so the runtime graph) do carry the type indexes.
- **Archives never capture Hidden (review G3 E-M2, engine `ArchiveHiddenColumnGuard`).** `TenantContext` passes the CK
  cache into `ArchiveLifecycleService` (activation refuses such columns) and, after every CK model import (explicit,
  embedded, System restore of a child or the system tenant, and a re-validation that recovered models), calls
  `RevalidateArchiveAccessAsync`: every **active** archive is re-checked with `RevalidateAccessAsync` and goes `Failed`
  (stops ingesting) when a column now reaches a Hidden attribute — best-effort (ERROR log, the import is not failed);
  skipped when stream data is off, no repository is registered or System.StreamData is not installed. The CrateDB
  column builder (`ArchivePathTypeResolver`) refuses a Hidden column and a whole-record column whose record contains a
  Hidden sub-attribute, and `BuildRecordObject` leaves Hidden sub-attributes out like Secret ones (defence in depth).
  Pinned by `ArchiveHiddenAccessTests` (mutation-checked).
- **N2/N3 closed (`octo-construction-kit-engine/docs/archive-hidden-columns.md`):**
  - **N2 — every service's import reaches the archives.** `RevalidateArchiveAccessAsync` is public
    (`ITenantContext.RevalidateArchiveAccessAsync`, default implementation does nothing). It still needs the archive
    lifecycle service, so in identity, comm-controller and platform-services it returns at once; the stream-data services
    run it from `PosCreatePosUpdateTenantConsumer` (octo-common-services) on every `PosUpdateTenant` of scope `Full`, i.e.
    after a model import in any service.
  - **N3 — `Failed` on Hidden columns is readable and recoverable.** `MongoArchiveRuntimeStore` persists
    `Archive.StatusReason` and `Archive.HiddenColumns` (System.StreamData 1.27.0) with the status in one write
    (`SetStatusAsync(rtId, status, reason, hiddenColumns)`; the two-argument write clears both). The query paths of
    `CrateDbStreamDataRepository` use `EnsureArchiveQueryableAsync`: an archive with `IsFailedOnHiddenColumns` answers
    queries with a snapshot without the Hidden columns and without computed columns whose formula reads one (projection
    and names refused, current and pending formulas); the row export (`ExportRowsAsync`) leaves the Hidden columns and
    every stored version of such computed columns (transitively, `{base}__v{N}` with a numeric N) out too; every write still requires
    `Activated`. `DropIngestedColumnStorageAsync` drops the columns (`ALTER TABLE … DROP COLUMN IF EXISTS`; never a
    standard column, never a physical column a kept path also maps to, only plain identifiers) and
    `RemoveIngestedColumnsAsync` removes them from the definition; the engine's
    `RedactHiddenColumnsAsync` (CK method `Archive.RedactHiddenColumns-1`, deprecated) and `RedactConfirmedHiddenColumnsAsync`
    (`Archive.RedactHiddenColumns-2`, System.StreamData 1.29.0: drops only when the Hidden set equals the caller's
    `expectedColumns`, otherwise `HiddenColumnRedactionColumnsChangedException` before anything is dropped) run both and resume
    the archive.
    `ArchiveReconciler` and a failed provisioning record their reason too.
  - Tests: `ArchiveHiddenAccessTests` (reason and columns round-trip through MongoDB, the public re-validation),
    `ComputedColumns/ArchiveHiddenColumnRedactionIntegrationTests` (real CrateDB: readable history, refused writes, drop,
    resume, idempotence; the confirmed redaction refuses a changed or unknown column set with table, rows, values and
    definition untouched and then drops only the confirmed column), `StreamData.UnitTests/CrateDbFailedOnHiddenColumnsReadGateTests`.
- **Backlog (G3 re-review N6/N7):**
  - **N6 — cost:** the re-check runs after every single model import (also per model of a batch or blueprint): it
    loads the cache, enumerates all archives and loads each active one, between the Pre and Post notification. Skip it
    when the tenant cache has no Hidden assignment, and run it once per batch.
  - **N7 — no compare-and-swap:** `SetStatusAsync` does not check the previous status, so a concurrent operator Disable
    can be overwritten by `Failed`, and racing services write duplicate audit rows.
- **New collections are lazy.** `CkInterface` / `CkTypeInterfaceImplementation` are created (empty) by
  `UpdateCollectionsAsync` on the next import of any model, v1 included; only v2 imports write rows. Reads on a tenant
  without them return empty and the cache loads
  (`TenantWithoutTheNewCollections_LoadsAndImportsV1ThenV2`). No startup migration.
- **Rollback safety.** `V1Import_WritesTheSameCkDocumentsAsTheMainEngine` compares every `Ck*` document of a fresh
  tenant plus a `Test-1.0.0` import with a golden captured from engine-mongodb origin/main e816cd3
  (`testData/ckv2-golden-main-v1-ck-documents.txt`, normalized by `CkDocumentSnapshot`: generated ObjectId ids and
  index-state timestamps removed). The diff is empty: a tenant that only saw v1 imports through the Phase 1 engine can
  go back to the main engine. Regenerate the golden only when the System or Test model itself changes.
- A **v2 import** makes the tenant unsafe for pre-Phase-1 services (they ignore `access`, Hidden would be exposed) —
  see `docs/ck-meta-model-persistence.md`, *No Mongo migration in Phase 1*.

### New meta-model field checklist (do this for EVERY new CK DTO property)

1. Entity member (`Repositories/Entities/*`), nullable or defaulted so legacy documents read back unchanged.
2. Class map in `MongoRepositoryClient.RegisterClassMaps`: `SetIgnoreIfNull` / `SetIgnoreIfDefault` so
   models that do not use the field keep their document shape; register a BSON serializer for new id types.
3. Write: the matching `Process*` method in `DatabaseCkModelRepository` (and `InsertModelWithImportingState` /
   `UpdateModelAsync` for model-level fields); keep the `*MongoDataSourceMapper.ApplyUpdate` in sync.
4. Read-back: `TryLookupCkModelAsync` — at EVERY site that builds the DTO (assignments: records, types,
   association roles).
5. A new collection additionally needs the plumbing list in `docs/ck-meta-model-persistence.md` (data source property, create list,
   `TransientCkModel`, bulk import, model state, `DeletePreviousVersion`, read-back query).
6. Run the hard gate `RoundTrip/CkMetaModelReflectionGateTests` (reflection over every compiled model) and
   `RoundTrip/CkMetaModelRoundTripTests` (JSON view, document shapes) — they fail until steps 1–4 are done. Extend the kitchen sink
   (C# and YAML) so the new field is actually set, and add a BSON legacy assertion.

### Attribute Ownership Round-Trip

`ownership` (`AttributeOwnershipDto`: `SeedOwned | TenantOwned | RuntimeState | Secret`) replaces
the `isRuntimeState` boolean, which conflated "preserve on Upsert" with "exclude from `ExportRt`".
It exists at TWO levels and both must survive the Mongo round-trip described in `docs/ck-meta-model-persistence.md`:

1. **Definition** — `CkAttributeDto.Ownership` (nullable). Persisted on
   `Repositories/Entities/CkAttribute.cs` next to the boolean; `ProcessCkAttributes` writes BOTH
   (`IsRuntimeState` is now the computed mirror `Ownership.IsPreservedOnUpsert()`), and the
   read-back in `TryLookupCkModelAsync` copies both.
2. **Assignment** — `CkTypeAttributeDto.Ownership` (nullable, `null` = inherit from the
   definition). Persisted on `Repositories/Entities/CkTypeAttribute.cs`, written by the single
   `ProcessCkTypeAttributes` and read back at **three** distinct sites in `TryLookupCkModelAsync`
   — records, types and association roles each build their own `CkTypeAttributeDto`. Miss one and
   only that element silently loses its override.

Both members are mapped `SetIgnoreIfDefault(true)` in `MongoRepositoryClient.RegisterClassMaps`, so
an undeclared ownership stays absent from the document — the exact shape an engine without ownership markers
wrote. **Legacy documents:** absent → `Ownership == null` → `AttributeOwnership.Resolve(null,
isRuntimeState)` maps the persisted boolean (`true` → `RuntimeState`, `false` → `SeedOwned`), so
behaviour is unchanged and a flagged attribute never degrades to "seed wins" (which would let a
re-apply overwrite credentials). Same operational caveat as for the runtime-state flag: a tenant keeps its
ownership-less docs until the model is **re-imported**, and a re-import needs a version bump
because `ImportCkModelAsync` short-circuits on an already-installed version.

Pinned by `CkAttributeOwnershipPersistenceTests` (import → cache, definition + override at all
three assignment sites, using `Test/OwnershipEntity`, `Test/OwnershipOverrideEntity`,
`Test/OwnershipRecord` and the `Test/OwnershipLink` role) and `CkAttributeOwnershipLegacyDocumentTests`
(BSON level, no DB: verbatim round-trip plus a document with the `ownership` element stripped).

### Extensible Enum Preservation on Import

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

### Embedded CK Model Import Guard (CK v2 F1.0-S1)

A CK model import deletes every `CkModel` row of the same **name** before it inserts
(`InsertModelWithImportingState`), so an exact-id existence check cannot see a newer installed version. Before
F1.0 every service re-imported its embedded System on tenant resolve whenever the exact id was missing — an
additive System bump (2.5.0 → 2.6.0) installed by one service was reverted by the next service within a second.

`EmbeddedCkModelImportGuard` (`src/Runtime.Engine.MongoDb/EmbeddedCkModelImportGuard.cs`) is now the single guard
of every **embedded/startup** import. It looks the model up by name (`FindInstalledAsync`, any state except
`Importing` — a `ResolveFailed` model counts as installed) and decides (`Decide`, pure):

| Installed | Decision | Log | Counter `octo.ck.embedded_import.skipped` |
|---|---|---|---|
| none / older | import (upgrade + migrations as before) | INFO | — |
| same version | short-circuit; `RetryPendingMigrationsAsync` only for an `Available` model | DEBUG | — |
| newer, same major | **skip** | INFO `downgrade prevented` | `reason=newer_installed` |
| higher major | **skip** | WARN `this service is too old for the tenant` | `reason=newer_major_installed` |

Used by `UpdateSystemCkModelAsync` (tenant resolve/create; the infrastructure-shell guard keeps its
position after it), `ImportCkModelAsync(CkModelId, OperationResult)` (every service's tenant setup, blueprint
dependencies via `MongoRuntimeRepositoryProvider`) and, through it, `ImportEmbeddedCkModelWithDowngradeGuardAsync`
(System.StreamData descriptor, service-managed models — the guard that used to live only there). A skip sends
**no** pre/post tenant-update notification, runs no migration and leaves the CK cache alone - unless the loaded cache
lacks the installed `Available` version (next bullet but one).

- **The decision is repeated under the model import lock** (G-H1). The early decision only saves the lock wait:
  while a caller waits, another service may install this or a newer version (parallel stack start, rolling update,
  a fresh tenant set up by two services), and the old exact-id re-check under the lock missed the newer row, so
  `InsertModelWithImportingState` deleted it. Embedded callers pass `TenantDatabaseSourceIdentifier.GuardAgainstDowngrade`;
  `ExecuteImport` then decides by name under the lock and reports a skip in `TenantDatabaseSourceIdentifier.ImportOutcome`
  (`SkippedUnderLock`, `InstalledModelId`). The caller sends the Post that pairs its Pre and runs no migration.
  Explicit imports keep the exact-id re-check.
- **A skip drops a cache that lacks the installed version** (`TenantContext.UnloadCkCacheIfItLacks`). At service
  start a service often loads the tenant's CK cache before another service's import of the same model, and that
  service's `PosUpdateTenant` is published before this process listens to the bus, so nothing unloaded the cache: it,
  and the asset repository's GraphQL schema built from it, missed the new types (archive types after a System.StreamData
  bump) until a manual ClearCache. Every skip (already installed, newer installed, skipped under the lock; embedded models
  and the System model) now compares the installed `Available` version with the model ids of the loaded cache and
  unloads it only when the version is missing - an in-memory check, so a tenant whose models did not change keeps its
  cache. Tests: `CkModelImportGuardTests.EmbeddedImport_*Cache*`.
- **Skips are reported to the caller** as a `Warning` in the `OperationResult` (the blueprint install path names the
  installed row and its state in its error 25 instead of guessing at missing dependencies).
- **Logged once per process** (G-L2): the first skip per (tenant, embedded id, installed id) logs INFO/WARN and is
  counted; repeats log DEBUG only — `UpdateSystemCkModelAsync` runs on every tenant resolve (per GraphQL request in
  the asset repository).
- **Dependencies are checked before the Pre notification** (G-L1): a model whose exact dependencies are not installed
  is skipped (WARN "missing dependencies") without any notification; the catalog load of the System restore also
  moved before Pre. Only a `ModelValidationException` from the import itself still leaves an unpaired Pre (rare).
- **Notifications of `UpdateSystemCkModelAsync` name the updated tenant** (the `tenantId` parameter). For child tenants
  resolved through the system context they used to go to the system tenant.
- **Existence checks downstream of the guard** (G-H2): a tenant may now legitimately have a newer version than a
  service embeds. `SystemContext.IsSystemTenantExistingAsync` therefore uses `ITenantContext.IsCkModelSatisfiedAsync`
  (by name, embedded version or newer) — with the exact check an older identity refused to start ("carries no usable
  System CK model") and older services threw `SystemTenantDatabaseNotExisting` on every request.
  `IsCkModelExistingAsync` stays exact (documented). Exact callers in other repos to review: identity
  `DefaultConfigurationCreatorService` (System.Identity, System.Notification) and `CreateIdentityDataCommandRequestConsumer`,
  comm-controller `TenantInitializationExtensions`, asset-repo `ModelsController` dependency status.

- **Caller imports never touch a service-managed model (AB#9325).** `ImportCkModelAsync(CkCompiledModelRoot)` is the
  caller overload: the bot's `ImportCk` / `ImportCkBatch` jobs call it for every file import and catalog import, whatever
  queued the bus command (asset repository, MCP or AI service, a Hangfire job enqueued before an update). It refuses every
  model whose name is service-managed - `ServiceManagedCkModels.IsServiceManaged` (`Runtime.Contracts.MongoDb`): `System`
  or `System.*`, case-insensitive, trimmed; `Customer.System` and `SystemX` are not - for every version (newer, equal,
  older, another major), also when the tenant does not have the model, before anything is read or written (no lock, no
  `Importing` row, no element rows, no migration, no notification, no re-validation). It throws
  `ServiceManagedCkModelImportRefusedException` (`Code` = `CK_SERVICE_MANAGED_MODEL`, the code of the asset repository's REST
  refusal; `ModelId`; the message names the model and the rule and carries no model content), logs one WARN `Refused caller
  import of service-managed CK model ...` and counts `octo.ck.explicit_import.refused` (no tags: the name is
  caller-supplied). The predicate is platform-wide on purpose: service-managed descriptors are registered per host (the bot
  knows only System.Bot), so they cannot decide it. Service-managed models reach a tenant only through the `CkModelId`
  overload (embedded/startup import, blueprint provider, `ClearCache` / `ReRunTenantSetup`), which is unchanged; it only
  brings in a missing or older version (a damaged same-version or a too new System needs a manual database repair).
  The element delete of every import selects rows by `ModelElementsPattern(name)` = `^<escaped name>-[0-9]`: an
  unescaped name (`Sy.tem.Identity`, `.*`) of an ordinary caller model used to match and delete the element rows of
  System models. Engine
  tests that must put a tenant on a specific System version use the internal seam
  `TenantContext.ImportServiceManagedCompiledCkModelForTestsAsync` (`CkModelImportGuardTests.InstallAsServiceAsync`); tests
  in other repositories load the model into a test catalog and import it by id. Tests:
  `CkModelImportGuard/ServiceManagedImportRefusalTests` (real System.Identity 3.8.0 snapshot in `testData/`: newer without
  `writeAccess`, same version manipulated, downgrade and next major refused with the database unchanged, message 144 still
  enforced afterwards; every System name and spelling; names that only contain "System"; an ordinary model that pins a
  self-built System.Identity does not install it).
- **Explicit imports of other models may downgrade** (platform-owner decision Q5): for a model that is not
  service-managed the caller overload has no guard of its own; the *API endpoint* judges the file with
  `EvaluateExplicitCkImportAsync` (tenant upgrade guard, below) before it queues the job and refuses a breaking one unless forced.
  A downgrade that passes may happen, logged WARN `Explicit downgrade of CK model ... from X
  to Y` and counted in `octo.ck.explicit_import.downgraded` (tag `model`).
- **No notification after a swallowed `ModelValidationException`** in the `CkModelId` overload any more (the System
  path already returned early): announcing a failed import made every service re-run its tenant setup.
- **Consequence for a service with an older embedded System:** it runs against the tenant's newer System (runtime
  code uses version-less ids, the cache is rebuilt from what is installed), but its own exact-pinned service model
  goes `ResolveFailed` until the service is upgraded — the v1 contract made visible instead of a silent downgrade.
  **Runtime code must therefore never look up a CK element by a generated *versioned* id** (`SystemCkIds.Ck…Id` =
  `System-2.5.0/…`): it is not in the cache of a tenant with a newer System. Use the version-less `RtCkId`
  (`….ToRtCkId()` / `GetRtCkType`). D-G1-1: `AutoIncrementModifier` looked up `SystemCkIds.CkAutoIncrementTypeId`
  on every entity insert, so an older service could not write anything into a tenant with a newer System (also its
  migration-history rows); it now looks the type up version-less and only when an auto-increment attribute is
  written. AB#6209: every new entity of a mixed batch is numbered (types/entities without auto-increment are
  skipped, no longer end the pass); since the pre-document modifications run on insert AND replace without saying
  which, the modifier reads the stored entities of the batch by RtId: a stored entity is never renumbered (an empty
  payload value gets the stored number back), a new one is numbered only where the attribute has no value (ImportRt
  values kept, counter not advanced). AB#9753: numbers are allocated atomically - one `findOneAndUpdate` with
  `$inc` per counter and batch (a block, filter `currentValue + n <= end`), handed out in batch order; the counter
  is never read first and never written back (so an older typed `RtAutoIncrement` cannot drop attributes only the
  newer System has). In a transaction a parallel writer of the same counter aborts the transaction with
  `TransientTransactionError`, which the modifier passes on unretried (only the transaction owner can retry, and it
  must rebuild the entities: the aborted attempt left its rolled-back number in the entity object); outside a
  transaction the `$inc` is retried up to 3 times. Sweep result (engine-mongodb `src/`): this was the only versioned generated-id lookup; the other
  `GetCkType`/`GetCkRecord`/`GetCkEnum` calls take ids from the CK cache or the database, which are the installed
  versions. Pinned by `OlderEmbeddedSystem_CanWriteEntities_IntoATenantWithANewerSystem`.
- Counters live in `CkModelImportDiagnostics` on the meter `Meshmakers.Octo.MongoDb` (no tenant tag; the tenant
  is in the log line).

### Re-validation of ResolveFailed Models after Every Import (CK v2 F1.0-S2)

`DatabaseCkModelRepository.ValidateDependencies` (end of `ExecuteImport`, inside the `sessionComplete`
transaction) soft-resolves **`Available` and `ResolveFailed`** models; `Importing` is never touched. Before F1.0 it
resolved `Available` models only, so a `ResolveFailed` model never recovered although the log promised it (R2-3:
Basic.Accounting stayed `ResolveFailed` after System went back to the version it pins).

- **Also without an import** (G-M1): when an embedded import finds its model already installed (or a newer one) in
  state `ResolveFailed`, it calls `IDatabaseCkModelRepository.RevalidateAsync` instead of re-importing. Before F1.0 a
  restart re-imported such a model and so healed it; without this the model would wait for an unrelated import. A
  recovery unloads the CK cache and sends one Pre/Post pair; nothing is sent when nothing changed (no loop).
- **Own transaction, retried** (G-L3): the state flip + re-validation run through `RunInTransactionWithRetryAsync`
  (3 attempts on `TransientTransactionError` / `UnknownTransactionCommitResult` — parallel imports re-validate the same
  models). Logs and counters are emitted after the commit. Once the element rows of an import are committed, a failure
  of the post-work no longer deletes the `CkModel` row (it stays `Importing` and is replaced by the next import).
- **Collections and indexes of recovered models** (G-M2): `UpdateCollectionsAsync` no longer drops empty collection
  roots of `ResolveFailed` models, and every recovery runs `UpdateCollectionsAsync` + a scoped `UpdateIndexAsync` for the
  recovered models.
- The resolver sees `ResolveFailed` models only for this call: `TenantDatabaseSourceIdentifier.IncludeResolveFailedModels`
  widens the state filter of `IsExistingAsync(CkModelIdVersionRange)` and `TryLookupCkModelAsync`. The CK cache
  (`ModelLoaderService`), imports and every other lookup keep resolving `Available` models only.
- `ComputeFailedModels` seeds the failed set with models whose exact dependency is not installed (reason names the
  installed version), the resolver's skipped models and its inheritance failures, and **propagates** the failure to
  every dependent until nothing changes — with `ResolveFailed` models visible, a dependent of a model that fails
  only its inheritance would otherwise stay `Available` on an unusable dependency.
- Transitions: `Available` → failed → `ResolveFailed` (WARN with the reason); `ResolveFailed` → resolves →
  `Available` (INFO, `octo.ck.model.revalidated{result=recovered}`; `UpdateModelStateAsync` flips the element rows
  too); still failing → stays, DEBUG only (`result=still_failed`, no log spam on every import).
- The callers in `TenantContext` unload the tenant's CK cache after every import, so a recovered model's types are in
  the next cache load.
- **Ranges (review I2, Phase 1):** `ComputeFailedModels` works on requirements (`RequirementsOf`): the persisted
  `DependencyRanges` (effective range incl. floor) of a range-retaining model, the exact pins of a classic model — the
  same requirements the engine's `RepositoryDependencyResolver` uses (`GetResolutionRanges`). A requirement that only
  `ResolveFailed` models satisfy propagates the failure. The embedded-import dependency pre-check in
  `TenantContext.ImportCkModelAsync(CkModelId, …)` uses `GetResolutionRanges` too.
- Also fixed on the way: an operator-precedence slip in `MongoDbRepositoryDataSource` (`a && b ? c : d`) that dropped
  the id filter of the base-type prefetch and loaded every `Available` type of the tenant.

- **SemVer resolution matrix.** The same table as the engine's catalog and repository resolvers
  (`octo-construction-kit-engine/docs/ck-semver-rules.md`, "Dependency resolution by SemVer"): a range-retaining dependent of
  `System-[2.5,3.0)` (floor 2.5.0) stays `Available` on 2.5.0 and on a higher minor/patch (2.6.1), is `ResolveFailed`
  on a version below the floor (2.4.9) and on a higher major (3.0.0), and the reason names the range and the installed
  version; an exact pin keeps exact match; no special treatment for System / Meshmakers-shipped models. Pinned by
  `CkModelImportGuardDecisionTests.ComputeFailedModels_SemVerMatrix_*` (unit) and
  `CkRangeRetentionImportTests.SemVerMatrix_ImportOfRangeRetainingDependent_AgainstInstalledBaseVersion` (real
  MongoDB: an import against an unsatisfiable range throws, nothing is installed, and the message names the range and
  the installed version). `DatabaseCkModelRepository` implements the engine's optional `IRepositoryVersionLister`
  (`ListVersionsAsync`: the versions `IsExistingAsync(range)` considers, ascending) so that the engine's
  `RepositoryDependencyResolver` can name them in the rejection. The repository also implements
  `IRepositoryInstalledVersionLister` (`ListInstalledVersionsAsync`: every held version of the name with its state, `Importing` included), and the
  rejection reads `X-[2.5,3.0): the tenant has no Available version of X (installed: 2.5.0 ResolveFailed)` instead of a catalog wording.

Pinned by `CkModelImportGuard/CkModelImportGuardTests` (real MongoDB, throwaway tenants: same-major and major skip,
refused caller downgrade of System + upgrade back of a service-installed older System, same-version short-circuit, embedded vs explicit service-model overloads,
notification counts and tenant ids, log levels and once-per-process logging, counters, operation-result warnings, the
deterministic under-lock skip plus a concurrent-import smoke test, a system database with a newer System, the stale
`ResolveFailed` heal with collection/index restore, the missing-dependency skip without notifications, and the R2-3
recovery incl. "still failed" on an unrelated import) and `CkModelImportGuardDecisionTests` (decision table, failure
propagation). The concurrent-import test rarely hits the lock path by itself; the deterministic under-lock test is
the one that fails without the G-H1 fix.

### Blueprint CK dependency ranges against the installed version

`BlueprintServiceIntegrationTests`-style coverage for `ckModelDependencies` ranges lives in
`BlueprintCkDependencyRangeIntegrationTests` (collection `BlueprintServiceCollection`, blueprint `TestCkRangeBp` 1.0.0 / 2.0.0):
`RrBase` (range-retaining) and `ClassicBase` (exact-pinned) are imported into a throwaway tenant first (they are in no catalog, so
the test proves the install step does not touch a model that is already inside its range). Inside the range (at the floor, or
newer) the model is kept; above the range the apply fails **before** any CK model of the blueprint is installed (`Test` stays
uninstalled, no installation row), naming blueprint, model, installed version and range; the update path shows the same. The
check itself lives in the engine (`BlueprintService.AddOutOfRangeInstalledCkModelErrorsAsync`, see the engine's
`docs/blueprints.md`); `MongoRuntimeRepositoryProvider.EnsureCkModelInstalledAsync` is unchanged (it still keeps a
satisfying-or-newer installed model by name and installs the floor otherwise).

### ResolveFailed reason

`CkModel.ResolveFailedReason` (BSON `resolveFailedReason`, nullable string, `SetIgnoreIfNull`) holds the plain-text reason a
model is `ResolveFailed`: written together with the state by `ValidateDependencies` (post-import re-validation,
`UpdateModelStateAsync(..., reason)`), cleared (`$unset`) when the model recovers or an import replaces it, rewritten
only when the text changed while a model stays failed (`CkModels.UpdateOneAsync`, no element-row rewrite). The three-place
rule: entity + class map (`MongoRepositoryClient`), write (`UpdateModelStateAsync` and the full-update
`CkModelMongoDataSourceMapper`, which unsets instead of writing null - pinned by `CkModelUpdateMapperTests`), read-back
(`GetCkModelsAsync`, `FindManyAsync`). Text (`BuildReason`): for an unmet requirement `Unmet dependency: ` +
`DescribeUnmetDependencies` (range + floor or exact pin, installed version - e.g. `Unmet dependency: RrBase-[1.0,2.0) (floor
1.1.0): installed RrBase-1.0.0`), otherwise the short cause of `ComputeFailedModels` capitalised (`A dependency does not
resolve`, `Dependency 'X-1.0.0' is ResolveFailed`, ...); names and versions only, capped at 1000 characters. Old documents
without the field load as "no reason". Pinned by `CkRangeRetentionImportTests.ResolveFailedReason_IsPersisted_ReadBack_AndClearedOnRecovery`
(real MongoDB: failed -> reason persisted and read back through the tenant repository after the CK cache was dropped ->
dependency restored -> reason gone). The public surface (LibraryStatus, GraphQL `CkModel`, CLI) lives in asset-repo / octo-cli.

### Service-Managed CK Model Auto-import

`TenantContext.EnsureServiceManagedCkModelsImportedAsync` runs on every tenant-resolve
(`TryGetChildTenantContextAsync`) and imports each host-registered
`IServiceManagedCkModelDescriptor` (e.g. `System.UI`, registered by platform-services) at its
embedded version via the same downgrade-guarded import. A per-process `ConcurrentDictionary`
guard (`_serviceManagedCkModelsAttempted`, keyed `{TenantId}:{modelName}`) makes the import run
at most once per (tenant, model) — it breaks the `ImportCkModelAsync → RetryPendingMigrations →
tenant-resolve → here` recursion. `_streamDataAutoImportAttempted` is the StreamData analogue.

**Guard invalidation on tenant delete/update.** Because the guard is per-process
and keyed only by tenant, a **delete+recreate of a tenant within one process lifetime** (e.g.
`om_initialize_tenant` re-provisioning) would hit the still-armed guard and skip the auto-import,
leaving the fresh tenant without its service-managed model. `ISystemContext.InvalidateTenantResolveImportGuards(tenantId)`
clears both guards for a tenant; the Pre-delete / Pre-update tenant lifecycle consumer
(`PreUpdatePreDeleteTenantConsumer` in `octo-common-services`) calls it next to the CK-cache
unload, so the next resolve re-imports. Regression test:
`ServiceManagedCkModelDescriptorTests.InvalidateTenantResolveImportGuards_ReenablesImportAfterTenantRecreate`.

**Blueprint floor → descriptor version redirect.** The cockpit blueprints declare
`ckModelDependencies: System.UI-[2.2.0,3.0)` as a *pure satisfiability floor* — the actual install
target is the descriptor's embedded version, NOT the floor's lower bound. But `BlueprintService`
still passes the range's `MinVersion` (2.2.0) to `EnsureCkModelInstalledAsync` when the dependency
is unsatisfied, and that version may no longer be published/embedded (only 2.3.0 is). So
`MongoRuntimeRepositoryProvider.EnsureCkModelInstalledAsync` redirects the requested model id to the
matching `IServiceManagedCkModelDescriptor` version when one is registered and is ≥ the requested
version — otherwise a blueprint apply on a tenant that hadn't yet been auto-imported failed with
`Model 'System.UI-2.2.0' not found in one of the registered catalogs`. Regression test:
`MongoRuntimeRepositoryProviderTests.EnsureCkModelInstalledAsync_ServiceManagedModel_RedirectsFloorToDescriptorVersion`.
