---
description: "CK meta-model persistence: owner paths, runtime state, range retention, v2 interfaces, access, methods, index attribute paths"
applies_to: src/Runtime.Engine.MongoDb/Repositories/Entities/**, src/Runtime.Contracts.MongoDb/Repositories/Entities/**
---

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

### CK v2 Range Retention Round-Trip (AB#5665, AB#5914)

**Contract for classic models (decided 2026-10-14, AB#6675 / AB#6691): "unchanged semantics", not "unchanged bytes".**
Range retention is the compiler default since AB#6675 (the property/option `OctoCkRangeRetention` / `-rr` is gone; the code-level
`CkCompilerOptions.RangeRetention = false` remains for tests and re-pin tooling), and engines below 3.5.1 refuse a range-retaining
model (`minEngineVersion` 3.5.1) - so a model newly compiled from unchanged sources is persisted with `dependencyRanges`,
`minEngineVersion` and major-qualified references, and a rollback to an older engine can no longer read it. What stays: a classic
(exact-pinned) model - published before the switch or compiled with `RangeRetention=false` - keeps its exact pre-v2 document shape and its
exact-pin resolution/`ResolveFailed` lifecycle, and a model that resolves, behaves the same. The tests pin both: the classic shape and the
golden on a classic variant of Test-1.0.0 (`Fixtures/ClassicModelFactory`: no ranges, no minEngineVersion, concrete ids), the default
(`RangeRetainingImport_WritesRangesMinEngineVersionAndMajorQualifiedReferences`,
`RangeRetainingModel_DocumentCarriesRangesAndMinEngineVersion`) and the import-guard lifecycle for both flavours
(`ResolveFailedModel_..._ExactPinModel` with a minor System bump, `..._WhenItsRangeBecomesSatisfiable` with a System of the next major;
a minor bump keeps a range-retaining model `Available`).

A model compiled with range retention (the default, see the engine CLAUDE.md) carries
`CkCompiledModelRoot.DependencyRanges` (`{range, floor}` per direct dependency) and stores references into
its dependencies **major-qualified** (`System@2/Entity-1`). The three-place rule applies:

1. entity: `CkModel.DependencyRanges` (`CkModelDependency[] {Range, Floor}` strings), mapped
   `SetIgnoreIfDefault(true)` — classic exact-pinned models keep the pre-v2 document shape;
2. write: `InsertModelWithImportingState` and the `UpdateModelAsync` transient model (plus the
   `CkModelMongoDataSourceMapper` full update, which `$unset`s `dependencyRanges` / `ckLanguage` when they are
   null instead of writing explicit nulls — review L19; the `CkTypeMongoDataSourceMapper` does the same for
   `methods`, review N9; both pinned by `CkModelUpdateMapperTests`);
3. read-back: `TryLookupCkModelAsync` → `CkCompiledModelRoot.DependencyRanges`.

References need no new field: `System@2/Entity-1` is persisted verbatim in `CkTypeInheritance`,
`CkTypeAssociation`, the attribute rows etc. (strings), and bound to the installed version only in memory
by the engine resolvers (on a copy during import, so the version-less form is what gets persisted).
`ValidateSystemReferencesAsync` skips major-qualified references (nothing to compare; an uninstalled major
fails reference resolution instead).

**Index maintenance binds major-qualified rows (D3 / review M6).** Index maintenance runs on the persisted
rows during an import, before any CK cache exists, and used to look the verbatim `System@2/Entity-1` up as a
concrete id: "Base type 'System@2/Entity-1' not found", the base type's indexes were not created on the
collection root, and a range-retaining type derived from a collection root was missing from the `$graphLookup`
result (its declared indexes not created, unique-index filters wrong). `InstalledModelReferenceBinder` (built from
the installed `CkModel` rows, `Available` + `Importing` during an import) binds them:
- `CollectBaseTypesForCollectionRoots` binds every inheritance row's base id before walking the chain;
- `BindMajorQualifiedInheritancesAsync` recomputes `CkTypeInfo.InheritedTypes` / `Inheritances` in memory (same
  depth semantics as the `$graphLookup`) — **only when a major-qualified row exists**, so classic tenants keep the
  database result unchanged;
- `FetchAttributeMetadataAsync` adds `Name@Major/...` alias keys for attributes and records of the installed
  versions (index attribute-path resolution through `DatabaseAttributeMetadataProvider`).
The rows themselves stay verbatim (the read-back must round-trip). Pinned by
`CkRangeRetentionImportTests.RangeRetainingTypes_IndexMaintenanceFollowsMajorQualifiedBaseTypes` (red before).

**Additive System minor with mixed models as integration test.** `AdditiveSystemMinor_MixedExactAndRangeModels_RangeModelsStayAvailable`
imports an additive System minor (copy of the installed System, one more optional attribute) through
`IDatabaseCkModelRepository.UpdateModelAsync` into a throwaway tenant holding exact-pinned models (the
System.Bot / System.Communication shape) and range-retaining ones: exact pins go `ResolveFailed`, range-retaining
models stay `Available` and resolve against the new System. An engine whose dependency resolver matches ranges by
overlap instead of structurally (fixed in the engine's range-retention port) throws "Sequence contains more than one
matching element" here. It uses the repository, not
`ITenantContext.ImportCkModelAsync`, because it isolates the repository behaviour. On the tenant path, the
embedded-import guard (F1.0-S1, see *Embedded CK Model Import Guard*) keeps a newer System that a service does not
embed; since Phase 1 a System bump therefore sticks, and exact-pinned service models go `ResolveFailed` until their
service ships the new System.

`ValidateDependencies` now logs, for every model it marks `ResolveFailed`, what is unmet
(`DescribeUnmetDependencies`): `RrBase-[1.0,2.0) (floor 1.1.0): installed RrBase-1.0.0` for range-retaining
models, `exact pin System-2.2.2: installed System-2.5.0` for classic ones.

Pinned by `CkRangeRetentionImportTests` (throwaway tenants): document shape, version-less inheritance row,
a range-retaining dependent stays `Available` across an additive minor of its dependency while an
exact-pinned one goes `ResolveFailed`, the rebuilt cache binds to the new version, and a downgrade below the
floor goes `ResolveFailed` with the range/floor/installed description. The test bumps a test base model, not
System, to stay independent of the System the test host embeds.
`UsedSurface_PersistsThroughTenantImport_ExactPinnedModelHasNone` (AB#4472 / AB#6273) imports a range-retaining
dependent with `usedSurface` / `usedSurfaceHash` per range through `ITenantContext.ImportCkModelAsync` (the bot-services
ImportCk path) and checks the `CkModel` document (list verbatim, hash recomputable) and the typed read-back; the
exact-pinned twin has no such field.

### CK v2 Meta-Model Persistence — interfaces, access, methods, ckLanguage, visibility, derivable, interface extends/associations/methods/deprecated (AB#5667 / AB#5668 / AB#5669 / AB#5914 / AB#5915)

CK v2 adds these constructs to the compiled model. All of them follow the three-place rule
(entity + class map → write in `ExecuteImport` → read-back in `TryLookupCkModelAsync`):

| Construct | Persisted as | Write | Read-back |
|---|---|---|---|
| `ckLanguage` (`CkModelPropertiesDto.CkLanguage`) | `CkModel.CkLanguage`, `SetIgnoreIfNull` | `InsertModelWithImportingState`, `UpdateModelAsync` transient model, `CkModelMongoDataSourceMapper` | model root |
| `access` (`CkTypeAttributeDto.Access`) | `CkTypeAttribute.Access` (Int32 enum, nullable), `SetIgnoreIfDefault` | `ProcessCkTypeAttributes` (single writer for types, records, association roles) | **three sites**: records, types, association roles |
| `methods` (`CkTypeDto.Methods`) | `CkType.Methods` = the Contracts `CkMethodDto` family **embedded verbatim** (class maps in `MongoRepositoryClient.RegisterCkMethodClassMaps`, every optional member left out when default) | `ProcessCkTypesAndAssociations` (`null` when empty); `CkTypeMongoDataSourceMapper.ApplyUpdate` | types |
| `implements` (`CkTypeDto.Implements`) | new collection `CkTypeInterfaceImplementation` (clone of `CkTypeInheritance`: one row per declared entry, owned by the model that declares the TYPE, reference persisted verbatim) | `ProcessCkTypesAndAssociations` | types (`null` when the type declares none) |
| `interfaces` (`CkModelRootBase.Interfaces`) | new collection `CkInterface` (`_id` = `CkId<CkInterfaceId>`, embedded `CkInterfaceAttribute` members) | `ProcessCkInterfaces` (runs before the types) | model root (`null` when the model declares none) |
| `minEngineVersion` (`CkCompiledModelRoot.MinEngineVersion`, Phase 1) | `CkModel.MinEngineVersion`, `SetIgnoreIfNull`, `Unset` on full update | as `ckLanguage` | model root |
| `visibility` (Phase 1) | `Visibility` (Int32 enum, nullable, `SetIgnoreIfNull`) on `CkType`, `CkRecord`, `CkEnum`, `CkAttribute`, `CkAssociationRole`, `CkInterface`; embedded on `CkMethodDto` | the matching `Process*` | the matching element |
| `derivable` (Phase 1) | `Derivable` on `CkType`, `CkRecord` (same mapping) | `ProcessCkTypesAndAssociations`, `ProcessCkRecords` | types, records |
| `writeAccess` (AB#8574, `CkTypeDto.WriteAccess`) | `CkType.WriteAccess` = the Contracts `CkTypeWriteAccessDto` **embedded verbatim** (class map in `MongoRepositoryClient`, `Principals` / `Channels` `SetIgnoreIfNull`; the member itself `SetIgnoreIfNull`, so unrestricted types keep their document shape and legacy documents read back as `null` = unrestricted) | `ProcessCkTypesAndAssociations`; `CkTypeMongoDataSourceMapper.ApplyUpdate` (`Unset` when null) | types. **Required for enforcement**: without it a tenant drops the block and the engine's `TypeWriteAccessGuard` sees an unrestricted type. Gates: C# kitchen sink `Thing` declares a block (`CkMetaModelReflectionGateTests`), `CkMetaModelLegacyDocumentTests.TypeWriteAccess_*`, end to end `TypeWriteAccessEnforcementTests` (real repository: Client/Unknown refused with 144 and nothing persisted, Interactive and system sessions write, derived type refused through its base) |
| `usedSurface` / `usedSurfaceHash` (AB#4472, Phase 2) | `CkModelDependency.UsedSurface` / `UsedSurfaceHash` inside `CkModel.DependencyRanges` (class map `SetIgnoreIfNull`; legacy entries read as null) | `CkModelDependency.FromDto` | `CkModelDependency.ToDto` |
| `securitySensitive` (AB#6269, Phase 2) | `CkAttribute.SecuritySensitive` (`bool?`, `SetIgnoreIfNull`, `Unset` on full update) | `ProcessCkAttributes` | attributes |
| interface `extends` / `associations` / `methods` / `deprecated` (Phase 1) | on the `CkInterface` document: `Extends` (`CkId<CkInterfaceId>` list), `Associations` (the Contracts `CkInterfaceAssociationDto` embedded, own class map), `Methods` (`CkMethodDto` embedded), `Deprecated` — all `SetIgnoreIfNull`, empty lists stored as absent | `ProcessCkInterfaces` | interfaces |
| `targetCkInterfaceId` on a type association (Phase 1) | `CkTypeAssociation.TargetCkInterfaceId` (`SetIgnoreIfNull`; `TargetCkTypeId` stays set — the interface narrows the target) | `ProcessCkTypesAndAssociations` | type associations |

**Decision — interface association members are embedded in the `CkInterface` document, not `CkTypeAssociation`
rows** (F1.3-S2): they are declarations an implementing type must satisfy, not associations of a type, and nothing
in index or collection maintenance reads them; `CkTypeAssociation` rows stay type-to-type (with an optional interface
narrowing). Every mapper `ApplyUpdate` `Unset`s a null Phase 1 member so a full update keeps the v1 document shape.
A forged compiled model that breaks visibility/derivable rules (messages 112/113) is refused on import — the import's
hard resolve validates against the installed models read back from MongoDB (`ForgedModel_ViolatingDerivableOrVisibility_IsRefusedOnImport`).

Both new collections go through the full plumbing: entity, class map, `CkIdSerializer`/`RtCkIdSerializer`
for `CkInterfaceId` (`OctoInterfaceIdSerializer`), mapper, `ICkMongoDbRepositoryDataSource` property,
`UpdateCollectionsAsync` create list, `TransientCkModel` lists, bulk import, `UpdateModelStateAsync`,
`DeletePreviousVersion` (regex on `ckModelId`) and the read-back. Only DECLARED implements/methods are stored;
inherited ones (`AllImplementedInterfaces`, `AllMethods`, `ImplementingTypes`) are resolved by the engine graph.

**Classic (v1) models keep their exact document shape**: no `ckLanguage`, `access` or `methods` element and no
row in the two new collections (pinned by `ClassicModel_DocumentsCarryNoCkV2Elements` and the BSON tests).
A *declared* `access: ReadWrite` is written (`0`) — the member is nullable, so only "undeclared" is absent, and
the declared value reads back verbatim.

**Import guard (message 126, minEngineVersion, review M-M2):** `ExecuteImport` refuses a model whose `MinEngineVersion`
is above the running engine (`CkEngineVersion.IsSatisfiedBy`) next to the ckLanguage check — before the import lock and
before `InsertModelWithImportingState` deletes the installed rows of the model name. The resolver's own 126 check runs
only after that delete and left the installed version without its `CkModel` row
(`ModelRequiringANewerEngine_IsRefusedBeforeTheInstalledVersionIsTouched`). That test pins the running engine with
`CkEngineVersion.OverrideCurrentForTests` (engine test seam, AB#6274): the assembly version differs per build (DebugL
`999.0.0`, main CI `0.1.*` where `Current` is `null` and the check is skipped, r-tag builds `3.x`), so tests of
message 126 must never depend on it.

**A failed import keeps the installed version (AB#7381).** `InsertModelWithImportingState` deletes the installed `CkModel` row(s) of the
model name (any version) before the system-reference pre-validation (`ValidateSystemReferencesAsync`) and the dependency resolution
(`HardResolveAsync`) run, and returns the deleted rows. When the import then fails *before the element rows were committed* (G-L3 below
keeps its behaviour afterwards), the failure path deletes the `Importing` row and **puts the previous rows back** in one transaction, still
under the model import lock: version, state (`Available` or `ResolveFailed`), dependencies, ranges, `resolveFailedReason` and `ckLanguage`
are as before (field order of the document may differ). The installed elements are untouched at that point (they are replaced only inside the
import transaction, which did not commit), so LibraryStatus shows the old version and its RT entities are not affected. Before the fix a failed
import - a forced one whose dependency is `ResolveFailed`, a stale System reference, a concurrent tenant change - left the model uninstalled
(test-2 meshtest, 2026-10-23: Industry.Energy 3.5.0 vanished after a failed 3.6.0). Tests: `CkRangeRetentionImportTests.FailedImport_*`
(dependency resolution fails after a forced Basic update; system reference mismatch), red without the restore.

**Readers wait for a running import (AB#8677).** Between `InsertModelWithImportingState` and the state flip the tenant holds no `Available`
version of the imported model (about a second for System.Bot; longer for big models), and the element rows are replaced in between. A
service that resolved a dependency on the model in that window failed (`ModelValidationException` "no Available version ... (installed:
3.6.0 Importing)") and, for the system tenant, stopped the host. Keeping the old row `Available` until the flip was rejected: its element
rows are replaced when the element transaction commits, long before the flip, so readers would see an `Available` model with missing or
foreign elements, and `DeletePreviousVersion` and the AB#7381 restore assume one row per model name. Instead readers wait on the **model
import lock**, which is held over the whole window: `IsExistingAsync(range)` waits (outside a transaction, up to
`CkModelImportInProgress.DefaultWaitTimeout`, 2 min, **for the whole resolve**: the deadline is
`TenantDatabaseSourceIdentifier.ImportWaitDeadline`, started by the first wait, so a resolve that meets several running imports does not
wait 2 min per lookup; cancelled with `TenantDatabaseSourceIdentifier.CancellationToken`, which the import sets from its caller) while the lock of the model is held (`ICkMongoDbRepositoryDataSource.GetModelsBeingImportedAsync`,
non-expired `ck_model_import_*` locks); `TenantRepository.RefreshCkCacheServiceAsync` (one snapshot transaction, which never sees the
flip) retries the cache load with a new transaction once running imports are done. When the wait runs out, or an import fails while
another model is being imported, the exception is marked with `CkModelImportInProgress.ExceptionDataKey` in `Exception.Data`; the tenant
setup of `octo-common-services` retries such a failure instead of failing the host. Residual race: a reader outside a transaction that
checked the lock just before an import took it can still read the old row while it is replaced (then the cache load retry or the caller's
retry applies). Tests: `CkRangeRetentionImportTests.InPlaceUpgrade_WhileDependentsResolve_NoResolutionFails`,
`FailedInPlaceUpgrade_WhileDependentsResolve_*` (both red without the fix with the production message),
`ModelsBeingImported_ReflectTheHeldImportLocks`, `CkModelImportWaitDeadlineTests` (unit: one deadline per resolve, cancellation).

**The resolve reads the held import locks from a cache (AB#8947).** `IsExistingAsync(range)` runs on every tenant lookup of every request
(`SystemContext.TryFindTenantContextAsync` -> `IsSystemTenantExistingAsync` -> `IsCkModelSatisfiedAsync`), so a lock query per call was one
MongoDB `find` on `SysLock` per request in every service. It asks `CkModelImportLockCache` first: one answer per tenant database
(`ICkModelImportLockScope.ImportLockScopeKey` = servers + database, shared by the admin and user data source instances) and process, reused
for at most `DefaultTimeToLive` (2 s), one query in flight per entry (concurrent readers share it; cancelled after `DefaultQueryTimeout`,
30 s, so a stalled read cannot hold later resolves; a reader re-checks the entry under its gate before it starts one), and dropped at once when this process
acquires or releases a model import lock (`MongoDbRepositoryDataSource.AcquireModelImportLockAsync` and the release of its handle). That
answer can predate an import in another process, or by a service on an older package (rolling deployment). Therefore a **miss** (no matching
version) is never believed from the cache: `IsExistingAsync` asks the lock collection directly, waits for a running import of the model, and
reads again - also when no import runs any more (it may have finished between the two reads) and also after a wait on the cached answer
(another import may have started). Hits are not re-checked. The importer does not wait for the cache. Rules: the wait loop (`CkModelImportInProgress`),
`TenantRepository.TryGetModelsBeingImportedAsync` and `MarkIfOtherImportsRunAsync` keep reading the lock collection directly - never route
them through the cache (a stale "running" would misclassify a failure, a stale "not running" would end a wait early); a new reader on a hot
path uses the cache and re-checks its misses. Cost: one `SysLock` read per tenant database, process and TTL for hits; one `SysLock` and one
more `CkModel` read per miss. The race
of AB#8677 stays as it was: lock check and model read are separate statements, so an import that starts between them can still make a
reader miss or read elements being replaced. Tests:
`CkModelImportLockCacheTests` (unit: once per TTL, local invalidation, a change during the query, single flight, a cancelled reader, a
stalled query timing out, shared scope, `IsExistingAsync` with a counting fake, direct check on a miss, stale answer during an upgrade, import
finished between the read and the direct check), `CkModelImportLockCacheIntegrationTests`
(worst-case delay of another process, reader of another process during an in-place upgrade never sees "does not exist", rolling deployment
with an importer that does not signal the cache and a never-expiring reader answer, `SysLock` reads per tenant lookup counted with the
MongoDB profiler: one per lookup before, at most one per TTL window after; the two reader tests are red without the miss check). Follow-up
outside this change: `IsSystemTenantExistingAsync` still costs `listDatabases` and a `CkModel` find per lookup.

**Import guard (message 91, CkLanguageNotSupported):** `ExecuteImport` refuses a model whose `CkLanguage` is
above `CkModelPropertiesDto.MaxSupportedCkLanguage` before the lock and before anything is written (the engine
`ElementResolver` raises 91 on resolve too; this guard covers callers that bypass resolution).

**Round-trip gate (concept §4.6, the isRuntimeState lesson) — MANDATORY for every meta-model change** —
`tests/.../RoundTrip/`:

- `CkMetaModelReflectionGateTests` (F1.3-S3, AB#5916) is the hard gate: every compiled model of the repository
  (installed System, System.StreamData, `Test-1.0.0`, `Test-2.0.0`, the MSBuild kitchen sink `KitchenSink-1.0.0`, the
  C# twin) → import → `TryLookupCkModelAsync` → `CkModelReflectionComparer`, a walk by **reflection over every public
  property** of the DTO graph (runtime types of both sides; DTO = class name ending in `Dto`/`Root`; DTO collections
  matched by identity, scalar collections in order). Intentionally unpersisted properties are in
  `CkModelReflectionComparer.AllowList` with a reason: `CkCompiledModelRoot.Migrations` (executed on import),
  `CkCompiledModelRoot.Compatibility` (AB#6295: the author's `compatibility.acknowledge` entries are build and publish-time
  metadata, the publish gate reads them from the catalog JSON; also listed in `CkModelJsonComparer.RoundTripIgnoredPaths`;
  the C# kitchen sink carries a value, so removing the entry turns the gate red). `CkTypeDto.Indexes` is NOT on the list any more
  (AB#7125): it is read back, and dropping the mapping fails the gate for System (`Entity-1` and five more types) and
  `TypeIndexes_AllMembers_SurviveTheMongoRoundTrip` (Text with language and weights, UniqueNotDeleted, Ascending, order).
  `UnpersistedDtoProperty_FailsTheGate` is the automated mutation check (a type
  DTO subclass with an unknown member goes through the real import and is reported); removing a read-back mapping
  (e.g. `Visibility = t.Visibility`) fails the gate with the property path (verified).

- `CkMetaModelRoundTripTests`: import → `TryLookupCkModelAsync` → compare the read-back with the compiled DTO
  as JSON over **all public properties** (`CkModelJsonComparer`, no hand-written field list). Corpus: installed
  System, `Test-1.0.0`, the MSBuild-compiled YAML kitchen sink `KitchenSink-1.0.0` (`tests/TestCkModelKitchenSink`,
  registered via `AddCkModelKitchenSinkV1()` in `CkModelImportMigrationFixture`) and its C#-built twin
  `CkV2KitchenSinkModel` (`KitchenSinkCs-1.0.0`, also used for the next-version replacement test).
  A new DTO property that is not persisted fails here by itself. `RoundTripIgnoredPaths` lists what is
  legitimately not stored, each with a justification: `$schema`, `migrations` and `compatibility` (dependencies, dependency
  ranges and, since AB#7125, `types[*].indexes` are compared). `defaultValues` / `autoCompleteValues` scalars compare by invariant text
  because the import converts them to the attribute's value type by design.
- `CkMetaModelCacheRoundTripTests`: the cache rebuilt from Mongo (YAML kitchen sink) exposes interfaces,
  declared AND inherited implements / methods (`AllImplementedInterfaces`, `AllMethods`, `ImplementingTypes`),
  methods field by field, `access` at all three assignment sites and `ckLanguage`.
- `CkMetaModelLegacyDocumentTests`: BSON only — classic element sets, pre-v2 documents read back as
  `null`/default, every method field and the new collections round-trip.
- The gate is the persistence half of the engine's touch-point checklist (octo-construction-kit-engine `CLAUDE.md`,
  CK v2 touch points): a new meta-model field is only done when both the engine checklist and the field checklist
  below are complete and this gate is green. The gate's xmldoc points to that engine checklist directly (AB#6272):
  its row 10 is this gate, its row 7 is the engine's classification guard `CkSemVerClassificationGuardTests`, which
  fails when the new field has no diff, no probe or no classifier rule.

**No Mongo migration in Phase 1 (plan §4).** No System/System.* model gains a persisted field (all stay `ckLanguage`
1, range retention is off by default), so Phase 1 needs no System version bump, no re-import and no data migration.
Every new member is nullable and absent from v1 documents; the new collections (`CkInterface`,
`CkTypeInterfaceImplementation`) are created empty by `UpdateCollectionsAsync` on the next import of any model, and only
a v2 import writes rows into them. A v2 import makes the tenant unsafe for
pre-Phase-1 services (they ignore `access`, so Hidden attributes would be exposed): no v2 model in a shared environment
before every service there runs the Phase 1 engine; `minEngineVersion` and the `ck-models/v3` catalog path enforce it on
the catalog side.

**Phase 1 additions (F1.3-S1, AB#5914):**
- `implements` order (review L20): each `CkTypeInterfaceImplementation` row stores its `Position` in the declared list;
  the read-back orders by it (interface id ordinal as tie-break), so multi-interface types round-trip in declared order
  (`TypeWithThreeInterfaces_ReadsBackInDeclaredOrder`).
- `CkModel.MinEngineVersion` (F1.1-S6) is persisted like `ckLanguage` (absent for v1, `Unset` on full update) and read
  back, so the repository resolver can refuse a model that needs a newer engine (message 126).
- Method `Visibility` (embedded `CkMethodDto`) is left out of the document when undeclared.

**Phase 3 addition (AB#6878, touch-point row 10 of `execution.handler`, engine AB#6877):**
- `execution.handler` (`CkMethodExecutionDto.Handler`, `'service'` | `'pipeline'`) is persisted inside the embedded
  `CkMethodDto.Execution` (class map `CkMethodExecutionDto` in `MongoRepositoryClient.RegisterCkMethodClassMaps`,
  `SetIgnoreIfNull`). An undeclared handler is left out of the document, so models without the key stay
  byte-identical and documents written before the field read back as `null`, i.e. the effective handler
  `service` (`CkMethodHandlers.Effective`, `CkMethodGraph.Handler`). There is no separate write or read-back site:
  the DTO is embedded verbatim. Gates: both kitchen sinks (C# `RoundTripMethods.Full` and the YAML
  `TestCkModelKitchenSink` method `ChangePassword-2`) declare `handler: pipeline`; `CkMetaModelReflectionGateTests`,
  `CkMetaModelCacheRoundTripTests` (cache rebuilt from Mongo) and `CkMetaModelLegacyDocumentTests` cover it.
  Do not publish a model with `handler: pipeline` before this persistence is released: an older service drops the value on import.
**Phase 3 addition (AB#7024, touch-point row 10 of `replacesGenericCreate`, engine AB#7024):**
- `replacesGenericCreate` (`CkMethodDto.ReplacesGenericCreate`, `bool`) is persisted on the embedded `CkMethodDto` (class map
  `CkMethodDto` in `MongoRepositoryClient`, `SetIgnoreIfDefault`). A method that does not set the flag has no such key
  in the document, so models without the key stay byte-identical and documents written before the field read back as
  `false` (`CkMethodGraph.ReplacesGenericCreate`). There is no separate write or read-back site: the DTO is embedded
  verbatim. The model's raised `minEngineVersion` (engine `CkEngineVersion.ReplacesGenericCreateMinEngineVersion`) is the
  existing `CkModel.MinEngineVersion` field. Gates: both kitchen sinks (C# `RoundTripMethods.ReplacesGenericCreate` and the
  YAML `TestCkModelKitchenSink` method `Widget.Provision-1`) declare the flag on the concrete type `Widget`;
  `CkMetaModelReflectionGateTests`, `CkMetaModelCacheRoundTripTests` (cache rebuilt from Mongo) and
  `CkMetaModelLegacyDocumentTests` (`MethodReplacesGenericCreate_*`, `MethodDocument_WrittenBefore*`) cover it.
  Do not publish a model with `replacesGenericCreate` before this persistence is released: an older service drops the flag on import
  (the engine's `minEngineVersion` makes a service with an older engine refuse the model, message 126).
**Phase 3 addition (AB#7036, touch-point row 10 of `authorization.dataAccess`, engine AB#7036):**
- `dataAccess` (`CkMethodAuthorizationDto.DataAccess`, nullable enum `CkMethodDataAccessDto` `Read` | `Write`, stored as its integer
  like `kind`) is persisted on the embedded `CkMethodAuthorizationDto` (class map in `MongoRepositoryClient`,
  `SetIgnoreIfNull`): a method that does not declare it has no such key in the authorization document, so models without the key
  stay byte-identical, and documents written before the field read back as `null`, i.e. the effective value `Write`
  (`CkMethodGraph.DataAccess`, `CkMethodAuthorization.Resolve`). There is no separate write or read-back site: the DTO is embedded
  verbatim. The model's raised `minEngineVersion` (engine `CkEngineVersion.DataAccessMinEngineVersion`) is the existing
  `CkModel.MinEngineVersion` field. Gates: both kitchen sinks (C# `RoundTripMethods.Full` and the YAML `TestCkModelKitchenSink`
  method `Thing.ChangePassword-2`) declare `dataAccess: Read`; `CkMetaModelReflectionGateTests`, `CkMetaModelCacheRoundTripTests`
  (cache rebuilt from Mongo) and `CkMetaModelLegacyDocumentTests` (`MethodAuthorizationDataAccess_*`,
  `MethodAuthorization_WrittenBefore*`) cover it. A service whose persistence predates the field would drop it on import and then
  treat a `Read` method as `Write` (fail-closed, but wrong); the engine's `minEngineVersion` makes a service with an older engine
  refuse the model (message 126). The gateway that reads the value is AB#7037.
**Phase 3 addition (AB#7006, touch-point row 10 of the method result marker `sensitive`, engine AB#7006):**
- `sensitive` (`CkMethodResultDto.Sensitive`, `bool`) marks the whole result of a method as never to be logged, audited or
  traced. It is persisted on the embedded `CkMethodResultDto` (class map `CkMethodResultDto` in `MongoRepositoryClient`,
  `SetIgnoreIfDefault`): a result without the marker has no such key in the document, so models without the key stay
  byte-identical and documents written before the field read back as `false` (`CkMethodGraph.HasSensitiveResult`). There is no
  separate write or read-back site: the DTO is embedded verbatim. The model's raised `minEngineVersion` (engine
  `CkEngineVersion.SensitiveResultMinEngineVersion`) is the existing `CkModel.MinEngineVersion` field. Gates: both kitchen
  sinks (C# `RoundTripMethods.Full` and the YAML `TestCkModelKitchenSink` method `Thing.ChangePassword-2`, a service handler)
  carry `sensitive: true` on the result; `CkMetaModelReflectionGateTests`, `CkMetaModelCacheRoundTripTests` (cache rebuilt
  from Mongo) and `CkMetaModelLegacyDocumentTests` (`MethodResultSensitive_*`, `MethodResultDocument_WrittenBefore*`) cover it.
  A service whose persistence predates the marker would drop it on import, so a model with a sensitive result is only
  imported by a service whose engine knows the key: the model's raised `minEngineVersion` makes an older engine refuse it
  (message 126) instead of storing the model without the marker.
**Phase 3 addition (AB#6926, touch-point row 10 of `execution.mode`, engine AB#6925):**
- `mode` (`CkMethodExecutionDto.Mode`, `'sync'` | `'async'`) is persisted inside the embedded `CkMethodDto.Execution` (class
  map `CkMethodExecutionDto` in `MongoRepositoryClient.RegisterCkMethodClassMaps`, `SetIgnoreIfNull`). An undeclared mode is
  left out of the document, so models without the key stay byte-identical and documents written before the field read back
  as `null`, i.e. the effective mode `sync` (`CkMethodModes.Effective`, `CkMethodGraph.Mode` / `IsAsync`). An explicitly
  declared `sync` is stored as declared. There is no separate write or read-back site: the DTO is embedded verbatim. The
  model's raised `minEngineVersion` (engine `CkEngineVersion.ExecutionModeMinEngineVersion`, 3.5.67) is the existing
  `CkModel.MinEngineVersion` field. Gates: both kitchen sinks declare `mode: async` on the service method
  `Thing.ChangePassword-2` (C# `RoundTripMethods.Full`, also `idempotent: true`) and an explicit `mode: sync` on the pipeline
  method `Thing.Reindex-1` (C# `RoundTripMethods.Static`; pipeline handlers are sync only, message 142);
  `CkMetaModelReflectionGateTests`, `CkMetaModelCacheRoundTripTests` (cache rebuilt from Mongo) and
  `CkMetaModelLegacyDocumentTests` (`MethodExecutionMode_*`, `MethodExecution_DocumentWrittenBeforeTheModeField_ReadsAsSync`)
  cover it. A service whose persistence predates the field would drop it on import and run an async method as sync; the
  model's raised `minEngineVersion` makes a service with an older engine refuse it (message 126).
- Hidden is not allowed on association-role assignments (engine rule 108); the kitchen sinks use `ReadOnly` there.
- Index maintenance: `FetchAttributeMetadataAsync` always binds major-qualified references including `Importing`
  models, while the inheritance chain binding follows `includeModelsInStateImporting`. Intentional: attribute
  metadata of all states is loaded anyway, and outside an import no `Importing` model exists.

### Index attribute paths resolve against the inheritance chain (AB#9524)

- A stored `CkType` lists only the attributes it assigns itself. `PrepareAndCreateIndex` therefore resolves the paths of
  an index against the **inheritance chain of the index-defining type** (`IndexAttributePaths.BuildTypeChains`: base
  chain from `CollectBaseTypesForCollectionRoots`, the root, descendants through `CkTypeInfo.InheritedTypes`), most-base
  first, so the type's own assignment wins over an inherited one of the same name (`IndexAttributePaths.ProviderFor`).
  The Secret guard and the Hidden backstop (every type of the collection, each with its chain) use the same providers.
- **Key-spec invariant:** a path that resolved against the type's own attributes resolves exactly as before; a plain
  inherited attribute (`Name`) resolves to the very string the fallback built (`attributes.` + camelCase of the written
  path), so no index is rebuilt. Inherited record paths (`attributes.timeRange.attributes.from` instead of the fallback
  `attributes.timeRange.From`) and array paths (`attributes.tags`) change the key spec once - the old index pointed at
  a field that is never stored (decided by Odin 2026-11-24; no shipped model indexes such a path).
- A path that still does not resolve logs `Could not resolve attribute path ... using fallback` and falls back as
  before. Known limitation: the merged text index is defined on the collection root and takes fields of descendants;
  those resolve against the root's chain only. Tests: `IndexAttributePathsTests` (unit, one per abuse case),
  `InheritedIndexAttributePathTests` (MongoDB, exact key specs and a no-op second update).
