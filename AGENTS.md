# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the MongoDB implementation of the OctoMesh Runtime Engine. It provides:
- MongoDB-based persistence for runtime entities
- Query engine with support for field filters, navigation properties, and aggregations
- Integration with the Construction Kit (CK) model system

### Entry points

Where to start reading, not a full list; every class sits in a folder of the routing table.

- `TenantRepository` - Main repository for tenant-specific data operations
- `SingleOriginRtQuery<T>` - Query engine for single-origin queries with field filters and navigation
- `MultipleOriginHierarchicalDeepRtGraphQuery` - Deep graph queries following parent-child hierarchy
- `RtInterfaceQueryExecutor` - Interface query: one query per collection, merged in the application
- `RtPathEvaluator` and `NavigationEnd` - Attribute paths and navigation ends; defined in the engine
  (`octo-construction-kit-engine`, Runtime.Contracts), used here
- `MongoRepositoryClient` - Base class that registers BSON conventions and class maps and builds the driver client
  (write concern majority / 30 s, read concern majority, see `docs/tenant-registry-and-clients.md`,
  *Write and read concern of the repository clients*)
- `MongoRuntimeRepositoryProvider` - Provides tenant repositories for CK model migrations
- `TenantContext` and `SystemContext` - Per-tenant and installation-wide context: CK model imports, migration
  triggers, tenant create, attach and delete
- `TenantOwnershipStore` - Database ownership marker of a tenant database
- `TenantUpgradeGuard` - Read-only check of what a CK model update would break
- `CrateDbStreamDataRepository` - Stream data archives and rollups in CrateDB

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `src/Runtime.Engine.MongoDb/Blueprints/**`, `tests/Runtime.Engine.MongoDb.IntegrationTests/Blueprint*.cs` | `docs/blueprints.md` |
| `src/Runtime.Engine.MongoDb/Serialization/**` | `docs/bson-serialization.md` |
| `src/Runtime.Engine.MongoDb/Repositories/Entities/**`, `src/Runtime.Contracts.MongoDb/Repositories/Entities/**` | `docs/ck-meta-model-persistence.md` |
| `src/Runtime.Engine.MongoDb/Services/**`, `src/Runtime.Engine.MongoDb/CkCache/**`, `src/Runtime.Engine.MongoDb/Blueprints/**`, `src/Runtime.Engine.MongoDb/Fixups/**`, `src/Runtime.Engine.MongoDb/EmbeddedCkModelImportGuard.cs` | `docs/ck-model-import.md` |
| `tests/Runtime.Engine.MongoDb.IntegrationTests/InterfaceQuery/**`, `src/Runtime.Engine.MongoDb/Repositories/Query/RtInterfaceQueryExecutor.cs` | `docs/interface-query.md` |
| `src/Runtime.Engine.MongoDb/Repositories/MongoDb/MongoLinkedBinaryDataSource*.cs` | `docs/linked-binaries-after-commit.md` |
| `src/Runtime.Engine.MongoDb/Configuration/**`, `src/Runtime.Engine.MongoDb/Repositories/MongoDb/**` | `docs/observability-command-profiling.md` |
| `src/Runtime.Engine.MongoDb/Repositories/Query/**`, `src/Runtime.Engine.MongoDb/RuntimeQueries/**` | `docs/permissions-and-interface-query.md` |
| `src/Runtime.Engine.MongoDb/RuntimeQueries/**`, `src/Runtime.Engine.MongoDb/Repositories/Query/**`, `src/Runtime.Engine.MongoDb/Repositories/MongoDb/SortIndexHint.cs`, `src/Runtime.Engine.MongoDb/Repositories/MongoDb/PagingTiebreakGate.cs` | `docs/runtime-query-paging.md` |
| `src/Runtime.Engine.MongoDb/Repositories/**`, `src/Runtime.Contracts.MongoDb/Repositories/**`, `src/Runtime.Engine.MongoDb/RuntimeQueries/**` | `docs/runtime-write-semantics.md` |
| `src/Runtime.Engine.CrateDb/**`, `src/Runtime.Engine.MongoDb/StreamData/**` | `docs/streamdata-archive-schema-and-activation.md` |
| `src/Runtime.Engine.CrateDb/QueryBuilder/**`, `src/Runtime.Engine.CrateDb/Copy/**`, `src/Runtime.Engine.MongoDb/StreamData/**` | `docs/streamdata-queries-and-export.md` |
| `src/Runtime.Engine.CrateDb/**`, `src/Runtime.Engine.MongoDb/StreamData/**` | `docs/streamdata-rollup-source-generation-read.md` |
| `src/Runtime.Engine.CrateDb/**`, `src/Runtime.Engine.MongoDb/StreamData/**` | `docs/streamdata-rollups.md` |
| `src/Runtime.Engine.CrateDb/**`, `src/Runtime.Engine.MongoDb/StreamData/**`, `tests/StreamData.UnitTests/**`, `src/Runtime.Contracts.MongoDb/Configuration/StreamDataInstanceConfiguration.cs`, `tests/Runtime.Engine.MongoDb.IntegrationTests/StreamDataWriteLoss/**` | `docs/streamdata-write-path.md` |
| `src/Runtime.Engine.MongoDb/Repositories/Tenant*/**`, `src/Runtime.Contracts.MongoDb/TenantOwnership/**`, `src/**/*Context.cs`, `src/**/TenantDeletionHandle.cs` | `docs/tenant-database-safety.md` |
| `src/Runtime.Contracts.MongoDb/TenantLifecycle/**`, `src/Runtime.Engine.MongoDb/Repositories/TenantLifecycle/**` | `docs/tenant-lifecycle-reconciler.md` |
| `src/Runtime.Contracts.MongoDb/TenantLifecycle/**`, `src/Runtime.Engine.MongoDb/Repositories/TenantLifecycle/**` | `docs/tenant-lifecycle-stores.md` |
| `src/Runtime.Engine.MongoDb/Repositories/MongoDb/**`, `src/Runtime.Engine.MongoDb/Services/**`, `src/Runtime.Contracts.MongoDb/Services/**` | `docs/tenant-registry-and-clients.md` |
| `src/Runtime.Contracts.MongoDb/UpgradeGuard/**`, `src/Runtime.Engine.MongoDb/UpgradeGuard/**` | `docs/tenant-upgrade-guard.md` |
| `tests/**`, `azure-pipelines.yml`, `azure-pipelines-performance.yml` | `docs/test-configuration.md` |
<!-- <<< end generated: routing -->

## Build & test

```bash
# Build with local NuGet packages (development)
dotnet build -c DebugL

# Build in Release mode against a published release (version must be passed explicitly)
dotnet build -c Release -p:OctoVersion=3.5.1

# Run integration tests (without the load-sensitive Performance and Stress tests)
dotnet test tests/Runtime.Engine.MongoDb.IntegrationTests -c DebugL --filter "Category!=LoadSensitive"

# Local full suite without the load-sensitive tests (`docs/test-configuration.md`, "Local full suite")
dotnet test Octo.ConstructionKit.Engine.MongoDb.sln -c DebugL --filter "Category!=LoadSensitive"
```

Versions come only from the pipeline: `Directory.Build.props` has no release fallback, so a
Debug/Release build without `-p:OctoVersion=X.Y.Z` (or `-p:OctoNugetPrivateServer=<feed>` for the main
line `0.1.*`) fails fast with MSBuild error `OCTO0001`. Use `-c DebugL` locally.

Test setup, suite layout and the CI time budget: `docs/test-configuration.md`.

## Before you commit

Run the local full suite (see Build & test) and fix every failure before committing.

## Rules

These rules bind new and changed code. Known exceptions in existing code are named in the linked docs.

- **This repository is public.** No work item ids, internal hostnames, customer or tenant names, or secrets in docs and
  in new or changed code comments and test names (work item ids stay in `CHANGELOG.md` entries); remove an existing one
  when you touch its line.
- **Tenant database safety.** A tenant delete runs `DeleteChildTenantMetadataAsync`, then commit, then
  `DropTenantDatabaseAsync`; stream data tables go per archive, only with `dropStreamData`. Create and
  attach pass `EnsureTenantNamespaceAvailableAsync` and the `TenantOwnershipStore` marker, with uniform, reason-free
  conflicts. Exception: the database does not enforce tenant id uniqueness, a duplicate is only logged
  ([docs/tenant-database-safety.md](docs/tenant-database-safety.md);
  `TenantDeletedNotificationOrderTests.TwoPhaseDelete_*` shows the order on the happy path, it does not guard against
  a caller that drops before the commit; `DropTenantStreamDataTests`, `DeleteOwnershipGuardTests`,
  `TenantNamespaceGuardTests`, `TenantOwnershipCrossInstanceTests`, `RestoreOwnershipGuardTests`).
- **A read or write path applies the caller's data permissions, and a hidden record answers like a missing one.**
  A new path takes its filter from `TenantRepository.CreateSecurityFilterAsync`, and a changed rule keeps
  `TenantRepository.CreateEntityNotFoundException` in step with `MongoDbDataSourceCollection`. Known exception:
  aggregations are not returned for a restricted deep graph, which prunes as a post-step
  ([docs/permissions-and-interface-query.md](docs/permissions-and-interface-query.md);
  `DataPermissionEvaluatorDifferentialTests`, `DataPermissionHiddenEqualsMissingTests`, `DeepGraphDataPermissionTests`).
- **A CK model validation refusal carries a number from the engine message catalog.** `CkModelValidationMessages`
  uses 91, 126, 145 (system reference mismatch) and 146 (dependency not resolvable) of `MessageCodes.json`, never a
  number of its own and never a free-text discriminator parameter
  ([docs/runtime-write-semantics.md](docs/runtime-write-semantics.md); `ValidateCkModelTests`).
- **A failed commit or write concern keeps its cause.** Rethrow the driver exception unchanged after a failed
  `OctoSession.CommitTransactionAsync`; map a write-concern-only failure to
  `OperationFailedException.DatabaseOperationFailed` with the driver exception inside; repository clients keep
  `w: majority` and read concern majority
  ([docs/runtime-write-semantics.md](docs/runtime-write-semantics.md),
  [docs/tenant-registry-and-clients.md](docs/tenant-registry-and-clients.md); `OctoSessionTransactionCallbackTests`,
  `MongoDbWriteExceptionMappingTests`, `MongoRepositoryClientConsistencySettingsTests`).
- **Every MongoDB call of a runtime query goes through the time limiter.** A new call in the query classes runs through
  `RuntimeQueryTimeLimiter` (as `SingleOriginQuery.AggregateToListAsync` and `RtInterfaceQueryExecutor.RunLimitedAsync`
  do); overrides are call-site scopes (`RuntimeQueryTimeLimit.Override` / `Disable`), never a global switch. Known
  exception: the association and deep-graph queries are not covered yet
  ([docs/runtime-write-semantics.md](docs/runtime-write-semantics.md); `RuntimeQueryTimeLimitTests`).
- **A sorted page is total and an index hint is safe.** A page of a type query sorts by the caller's keys plus the `_id`
  tiebreak. Known exceptions: an index order `PagingTiebreakGate` keeps, and a `$geoNear` or text search keeping its own
  order. A hint names only an index that `SortIndexHint.Provides` accepts (never partial, sparse, hidden or
  collated) and is never set inside a transaction ([docs/runtime-query-paging.md](docs/runtime-query-paging.md);
  `PagedQueryTiebreakTests`, `TiebreakIndexShapeTests`, `SortIndexHintTests`, `PagedQuerySortHintTests`).
- **An interface query validates before it touches a collection** (negative `skip`/`take` throws
  `ArgumentOutOfRangeException`, everything else `InterfaceQueryException`); a collection left with no allowed type is
  not queried at all ([docs/interface-query.md](docs/interface-query.md); `GetRtEntitiesByInterfaceAsyncTests`).
- **Hidden archive columns never leave through a backup or export.** New copy, export and restore code decides through
  `CrateDbStreamDataRepository.FindHiddenColumnExclusion`. Known exception: a column made Hidden by a CK model that
  cannot be read, and not yet recorded, can still end up in a backup
  ([docs/streamdata-write-path.md](docs/streamdata-write-path.md), "Backups leave Hidden columns out";
  `HiddenColumnExclusionTests`, `ArchiveHiddenColumnBackupIntegrationTests`).
- **A dump carries only columns the archive definition owns, or columns the restore refuses.** The leftovers of computed
  columns (a removed computed column, an earlier formula version) are left out through `ArchiveColumnOwnership`. A column
  is a leftover only on positive evidence; every other unowned column stays in the dump, so the restore still refuses it.
  Provisioning names computed columns by their active and pending versions
  ([docs/streamdata-write-path.md](docs/streamdata-write-path.md), "Backups leave the leftovers of computed columns out";
  `ArchiveColumnOwnershipTests`, `ArchiveComputedColumnLeftoverBackupIntegrationTests`).
- **The physical table of an activated archive only grows.** Reconciliation adds declared columns and never drops or
  retypes one. A column added to an activated raw or time-range archive goes through the engine's
  `IArchiveLifecycleService.AddArchiveColumnAsync` (from a seed through `ArchiveDefinitionGuard.TakeColumnAdditions`),
  never into `Columns` alone
  ([docs/streamdata-archive-schema-and-activation.md](docs/streamdata-archive-schema-and-activation.md),
  [docs/streamdata-write-path.md](docs/streamdata-write-path.md); `ArchiveColumnReconciliationTests`,
  `ArchiveAddColumnIntegrationTests`, `ArchiveDefinitionGuardIntegrationTests`).
- **A `TimeSpan` attribute is stored as Int64 ticks.** Any new place that coerces an attribute value to `TimeSpan` also
  accepts the bare-integer ticks string, or it rejects already imported data
  ([docs/bson-serialization.md](docs/bson-serialization.md); `TimeSpanSerializerTests`).
- **A wall-clock budget or stress test carries `Category=Performance` or `Category=Stress`** and
  `Category=LoadSensitive`, which keeps it out of the PR build and out of the local full suite;
  `LoadSensitiveTraitTests` checks the pair in the integration test assembly only
  ([docs/test-configuration.md](docs/test-configuration.md)).
- **A tenant restore attaches only published service-managed models.** `TenantBackupService.RestoreTenantAsync` runs
  `RestoreSystemModelGuard` before anything destructive (scratch database) and again before the attach: a `System` /
  `System.*` model whose content differs from the catalog's model of the same id, an unpublished or too new version, foreign
  element rows or an unreadable catalog refuse the restore (`RESTORE_SYSTEM_MODEL_REFUSED`), a genuine older one is raised
  to the system tenant's version. The resolved closure (`dependencies`) of a range-retaining model is a build artefact:
  it is validated (same names, same major, inside the declared range, published), not compared
  (`RestoreSystemModelClosure`, AB#10061); never compare it exactly again. A restore that replaces an existing tenant
  checks and raises in a staging database and drops the existing tenant only afterwards (AB#10017): nothing that reads
  the catalog may run after that drop. New restore or attach paths keep that order
  ([docs/ck-model-import.md](docs/ck-model-import.md), "Restore check of service-managed models";
  `RestoreSystemModelCheckTests`, `RestoreSystemModelDeciderTests`).
- **A new CK meta-model field survives the Mongo round trip:** entity, `ProcessCk*` write and `TryLookupCkModelAsync`
  read-back, or an entry with a reason in `CkModelReflectionComparer.AllowList`
  ([docs/ck-meta-model-persistence.md](docs/ck-meta-model-persistence.md); `CkMetaModelReflectionGateTests`).
