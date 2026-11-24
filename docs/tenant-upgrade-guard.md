---
description: Tenant upgrade guard and the computed-column backfill job
applies_to: src/Runtime.Contracts.MongoDb/UpgradeGuard/**, src/Runtime.Engine.MongoDb/UpgradeGuard/**
---

## Tenant upgrade guard (CK v2 F2.5, AB#6867)

Read-only service that answers, before anything is imported: **which installed models would an update (set) of CK models
break?** Motivation (2026-10-17, staging-1 tenant sbeg): `EnergyCommunity.Registration-2.8.0` (exact pins `Basic-2.4.0`,
`Basic.Energy-1.9.0`, `EnergyCommunity-4.9.0`) went `ResolveFailed` after `Basic-2.5.0` / `Basic.Energy-1.10.0` /
`EnergyCommunity-4.10.0` were imported, and LibraryStatus offered those updates as plain "Update". Until now the only tenant check
was `DatabaseCkModelRepository.ValidateDependencies`, which runs *after* the import. The guard is the shared engine of F2.5
(AB#5687); LibraryStatus / CheckUpgrade (AB#6868), the import refusal (AB#6870), blueprints (AB#6871), the CLI (AB#6869) and Studio
(AB#6872) only present or enforce its result.

**Public API** (asset-repo and other services call it through the tenant context):

```csharp
// Runtime.Contracts.MongoDb: ITenantContext (implemented by TenantContext)
Task<CkUpgradeGuardResult> EvaluateCkUpdateAsync(IReadOnlyCollection<CkCompiledModelRoot> updateSet,
    IReadOnlyCollection<CkModelId>? availableDependentVersions = null, CancellationToken cancellationToken = default);

// Explicit callers only (AB#6966): the file import and its preview. Same rules, but service-managed candidates
// (System, System.*) are judged as replacements too, in either direction.
Task<CkUpgradeGuardResult> EvaluateExplicitCkImportAsync(IReadOnlyCollection<CkCompiledModelRoot> updateSet,
    IReadOnlyCollection<CkModelId>? availableDependentVersions = null, CancellationToken cancellationToken = default);
```

Result types (`Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard`):
`CkUpgradeGuardResult { Blockers, Notes, IsBlocked }`; `CkUpgradeBlocker { Dependent, Update, Kind, Subject, Code, Message,
DependentIsInUpdateSet, SuggestedDependentUpdate }`; `CkUpgradeBlockerKind { ExactPin, FloorNotMet, RangeNotSatisfied,
ElementRemoved, ElementChanged, ElementBecameInternal, NameCollision }`. The stable codes are `CK_UPGRADE_EXACT_PIN`,
`CK_UPGRADE_FLOOR_NOT_MET`, `CK_UPGRADE_RANGE_NOT_SATISFIED`, `CK_UPGRADE_ELEMENT_REMOVED`, `CK_UPGRADE_ELEMENT_CHANGED`,
`CK_UPGRADE_ELEMENT_BECAME_INTERNAL`, `CK_UPGRADE_NAME_COLLISION`; `Message` is English for logs, presentation layers render from
`Code` + `Dependent` + `Update` + `Subject`. `RangeNotSatisfied` is an addition to the six kinds of the story: a dependent whose
range has an upper bound below the candidate is unsatisfied after the import just like a floor violation.
Below the context, `ITenantUpgradeGuard.EvaluateAsync(TenantDatabaseSourceIdentifier, ...)` (Runtime.Engine.MongoDb, registered
transient in `AddMongoDbRuntimeRepository`) is the same service for code that already holds a data source.

**Rules** (`UpgradeGuard/TenantUpgradeGuard.cs`):

- *Post-state.* The set is judged as a whole. A **replacement** is a candidate whose name is installed (`Available`) in the same major
  with another version. Dependents are every installed `Available` model that the set does not replace, plus every model of the set
  (`DependentIsInUpdateSet = true`): a dependent replaced in the set by a version that fits is no blocker, one replaced by a version
  that still breaks is reported.
- *Exact-pinned dependent* (no `dependencyRanges`, i.e. a classic model; legacy documents without `usedSurface`/ranges are always this):
  a pin on another version of the replaced model's major is an `ExactPin` blocker; the pin on the candidate's own version is fine; a pin
  on another major is not affected. Decided from the model row alone (no model load).
- *Range-retaining dependent*: the engine's `ICkSurfaceSatisfactionChecker` (AB#5437, the judge of the cascade dry run) runs on the
  dependent rebuilt from MongoDB (`TryLookupCkModelAsync`, so `usedSurface` comes from the persisted `CkModelDependency`) with a
  `CkSurfaceCandidate` whose baseline is the installed version and whose classified changes come from `ICkModelDiffService` +
  `ICkSemVerClassifier`. Findings map to kinds by their fixed phrases (`TenantUpgradeGuard.Classify`; the unit tests run the real
  checker against every phrase, so an engine rewording turns a test red; structured findings in `CkDependentCheck` would remove the
  text coupling - open point for the engine).
- *Never reported*: dependents that are `ResolveFailed` or `Importing` (not caused by the update); service-managed candidates
  (`System`, `System.*` - the embedded-import downgrade guard and the service version decide there) **unless the caller is explicit**:
  `EvaluateExplicitCkImportAsync` / `ITenantUpgradeGuard.EvaluateAsync(..., includeServiceManagedModels: true)` judge them like any
  model (AB#6966: a System file import that removes a used attribute left the tenant unloadable; a downgrade is judged like an
  upgrade, a harmless one is no blocker). Blueprint apply/update, FixAll, the batch import and the embedded import keep the default; candidates of another major than the
  installed one (a parallel install); candidates that are not installed or have the installed version; a `ResolveFailed` installed
  base (the update may repair it).
- *Unknowns are announced, not guessed*: a range-retaining dependent without `usedSurface` (compiled before AB#4472) is judged by its
  referenced elements only and yields a `Notes` entry; a dependent or baseline that cannot be loaded yields a note and is not assumed
  healthy or broken.
- *Read-only, persisted data only*: the internal `IInstalledCkModelReader` (model rows + load by id) has no write member; there is no
  catalog access and no import lock. The dependents' compiled form is loaded only for range-retaining dependents of a replaced model
  (plus the baseline); 100 installed models with 10 updates take milliseconds (unit test: < 2 s, zero loads for exact pins).
- `availableDependentVersions` (optional, e.g. the catalog versions) only fills `SuggestedDependentUpdate` (newest version above the
  dependent's); it is a suggestion - prove it by evaluating the combined set.

**Installed baseline read-back (AB#7125).** The baseline of every check is the installed model rebuilt from the persisted rows
(`MongoInstalledCkModelReader` → `TryLookupCkModelAsync`). Until AB#7125 that rebuild never mapped `CkType.Indexes` back (the
same defect class as AB#4589: persisted, written on import, but missing in the read-back), so the baseline had no indexes, every
index of the new version looked ADDED and `CkSemVerClassifier` rated an added unique index Major ("existing data may violate the new
unique index"): a false `ElementChanged` blocker for every model that declares an index (e.g. `Basic.Energy` 1.9.0 to 1.10.0,
`MeteringPoint-1` UniqueNotDeleted on `MeteringPointNumber`, staging-1 tenant eegroider). `DatabaseCkModelRepository.IndexesOf` maps
`IndexType`, `Language` and every field (`Weight`, `AttributePaths`) back; an empty list reads back as `null`, like the compiled
DTO. No persistence format change. Side effect: dependency resolution of an import now sees the indexes of the installed dependency
types (the runtime graph carries them); the full suite is green with it. Before the fix the guard outputs (LibraryStatus,
CheckUpgrade, FixAll/Update refusals) could show a false "would break" for any update of a model with an index; a real
index change is still reported (`InstalledUniqueIndex_IsReadBackAsBaseline_SoOnlyARealNewIndexBlocks`).

**Tests**: `StreamData.UnitTests/UpgradeGuard/TenantUpgradeGuardTests.cs` (the sbeg case, range-retaining Registration, replaced
dependent in the set, element removed / member removed / internal / changed / floor / range / name collision, additive update, several
dependents, no dependents, ResolveFailed, service-managed, other major, legacy models, read-only, 100x10) against the real
checker/diff/classifier; `Runtime.Engine.MongoDb.IntegrationTests/TenantUpgradeGuardIntegrationTests.cs` (Mongo round trip through
`ITenantContext.EvaluateCkUpdateAsync`: persisted exact pins, ranges, `usedSurface` and types; model documents identical before and after).


## Computed-column backfill as a background job (AB#8002)

Adding a computed column or changing its formula only queues the backfill (engine `ArchiveLifecycleService.Enqueue…`,
concept `octo-construction-kit-engine/docs/concept-computed-columns.md` §8.3a). This repository runs and executes it:

- **`ComputedColumnBackfillHostedService`** (`Runtime.Engine.MongoDb/StreamData`, registered by
  `AddComputedColumnBackfillBackgroundService()`, options `ComputedColumnBackfillOptions` = `StreamData:ComputedColumnBackfill`,
  15 s tick, 30 s startup delay) drains every tenant of the shared `IRollupTenantSource` one after the other through
  `IArchiveLifecycleService.RunPendingComputedColumnBackfillsAsync(stoppingToken)`. A host that serves the computed-column
  mutations must register it, or queued backfills never run. Tests: `ComputedColumnBackfillHostedServiceTests`.
- **`CrateDbStreamDataRepository.BackfillComputedColumnAsync`** walks the archive in time slices of
  `StreamDataConfiguration.ComputedColumnBackfillSlice` (default 1 day) on the time key (`timestamp` / `window_start`): one
  `min/max` query, then every keyset page is bounded to its slice, an empty slice is skipped with one `min(time) >= …` lookup, and a
  bounds row of unexpected shape falls back to the unsliced keyset walk. It reports the running row count after every bulk
  (`IProgress<long>` overload). Every statement uses `CrateStatementClass.Maintenance` (the `StreamRawRowsAsync` /
  `ExecuteBulkAsync` overloads with a statement class set the command timeout explicitly) — the overloads without a class keep the
  driver default of 30 s, which is what killed the test-2 backfills (`XX000: Job killed. Cancellation request by: octo-system`).
  Tests: `CrateDbComputedColumnBackfillTests` (unit, sliced cases) and `ComputedColumnBackfillJobIntegrationTests` (real CrateDB:
  queued add survives the request's cancellation, two queued backfills, failure + retry).
- **Checkpoint / resume (AB#8002 follow-up).** The resumable overload `BackfillComputedColumnAsync(snapshot, column, resumeFrom,
  IProgress<ComputedColumnBackfillProgress>, ct)` starts the slice walk at `resumeFrom` (when it is after the first row) and reports
  `CompletedThrough` = the start of the next slice after every completed slice and gap jump; the engine's runner stores it in the job's
  `RangeStart`, so a backfill cut short by a rollout resumes there. The unsliced fallback reports no checkpoint (a resumed run walks it
  all again). Tests: `ResumedBackfill_StartsAtTheCheckpoint_…`, `ResumePastTheLastRow_WritesNothing` (unit) and
  `ARolloutMidRun_HandsTheJobBack_AndTheNextRunResumesFromTheCheckpoint` (real CrateDB).
- **`MongoRecomputeJobStore.GetActiveForArchiveAsync`** skips computed-column jobs (`RecomputeJobSnapshot.IsComputedColumnBackfill`),
  so the rollup orchestrator never adopts, coalesces into or fails one.
