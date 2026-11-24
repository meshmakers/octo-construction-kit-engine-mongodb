# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the MongoDB implementation of the OctoMesh Runtime Engine. It provides:
- MongoDB-based persistence for runtime entities
- Query engine with support for field filters, navigation properties, and aggregations
- Integration with the Construction Kit (CK) model system

### Entry points

Where to start reading; the list is not complete (the classes sit in the folders named in the routing table below).

- `TenantRepository` - Main repository for tenant-specific data operations
- `SingleOriginRtQuery<T>` - Query engine for single-origin queries with field filters and navigation
- `MultipleOriginHierarchicalDeepRtGraphQuery` - Deep graph queries following parent-child hierarchy
- `RtInterfaceQueryExecutor` - Interface query: one query per collection, merged in the application
- `RtPathEvaluator` and `NavigationEnd` - Attribute paths and navigation ends; defined in the engine
  (`octo-construction-kit-engine`, Runtime.Contracts), used here
- `MongoRepositoryClient` - Base class that registers BSON conventions and class maps and builds the driver client
  (write concern majority / 30 s, read concern majority, see *Write and read concern of the repository clients*)
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
| `src/Runtime.Engine.MongoDb/Services/**`, `src/Runtime.Engine.MongoDb/CkCache/**`, `src/Runtime.Engine.MongoDb/Blueprints/**`, `src/Runtime.Engine.MongoDb/Fixups/**` | `docs/ck-model-import.md` |
| `tests/Runtime.Engine.MongoDb.IntegrationTests/InterfaceQuery/**` | `docs/interface-query.md` |
| `src/Runtime.Engine.MongoDb/Repositories/MongoDb/MongoLinkedBinaryDataSource*.cs` | `docs/linked-binaries-after-commit.md` |
| `src/Runtime.Engine.MongoDb/Configuration/**`, `src/Runtime.Engine.MongoDb/Repositories/MongoDb/**` | `docs/observability-command-profiling.md` |
| `src/Runtime.Engine.MongoDb/Repositories/Query/**`, `src/Runtime.Engine.MongoDb/RuntimeQueries/**` | `docs/permissions-and-interface-query.md` |
| `src/Runtime.Engine.MongoDb/RuntimeQueries/**`, `src/Runtime.Engine.MongoDb/Repositories/Query/**` | `docs/runtime-query-paging.md` |
| `src/Runtime.Engine.MongoDb/Repositories/**`, `src/Runtime.Contracts.MongoDb/Repositories/**` | `docs/runtime-write-semantics.md` |
| `src/Runtime.Engine.CrateDb/**`, `src/Runtime.Engine.MongoDb/StreamData/**` | `docs/streamdata-archive-schema-and-activation.md` |
| `src/Runtime.Engine.CrateDb/QueryBuilder/**`, `src/Runtime.Engine.CrateDb/Copy/**`, `src/Runtime.Engine.MongoDb/StreamData/**` | `docs/streamdata-queries-and-export.md` |
| `src/Runtime.Engine.CrateDb/**`, `src/Runtime.Engine.MongoDb/StreamData/**` | `docs/streamdata-rollup-source-generation-read.md` |
| `src/Runtime.Engine.CrateDb/**`, `src/Runtime.Engine.MongoDb/StreamData/**` | `docs/streamdata-rollups.md` |
| `src/Runtime.Engine.CrateDb/**`, `src/Runtime.Engine.MongoDb/StreamData/**`, `tests/StreamData.UnitTests/**` | `docs/streamdata-write-path.md` |
| `src/Runtime.Engine.MongoDb/Repositories/Tenant*/**`, `src/Runtime.Contracts.MongoDb/TenantOwnership/**` | `docs/tenant-database-safety.md` |
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

# Local full suite without the load-sensitive tests (see "Local full suite" below)
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
- **A tenant database is dropped only after its metadata delete is committed.** New delete paths call
  `DeleteChildTenantMetadataAsync`, commit, then `DropTenantDatabaseAsync`; stream data tables are dropped per archive,
  and only when the caller passes `dropStreamData`, never "everything in the tenant's schema"
  ([docs/tenant-database-safety.md](docs/tenant-database-safety.md); `TenantDeletedNotificationOrderTests`,
  `DeleteOwnershipGuardTests`).
- **Tenant create and attach pass the namespace gate and the ownership marker.** `EnsureTenantNamespaceAvailableAsync`
  runs before any side effect; its two conflict messages stay uniform and reason-free (the reason goes to the log); the
  `TenantOwnershipStore` marker is never written into a system database. Known exception: the database does not
  enforce tenant id uniqueness, a duplicate is only logged
  ([docs/tenant-database-safety.md](docs/tenant-database-safety.md); `TenantNamespaceGuardTests`,
  `TenantOwnershipCrossInstanceTests`, `RestoreOwnershipGuardTests`).
- **Every read and write path applies the caller's data permissions, and a hidden record answers like a missing one.** A
  new read path (type query, deep graph, interface query) builds on the shared security filter, a changed rule keeps
  `TenantRepository.CreateEntityNotFoundException` in step with `MongoDbDataSourceCollection`. Known exception:
  aggregations are not returned for a restricted deep graph
  ([docs/permissions-and-interface-query.md](docs/permissions-and-interface-query.md);
  `DataPermissionEvaluatorDifferentialTests`, `DataPermissionHiddenEqualsMissingTests`, `DeepGraphDataPermissionTests`).
- **A failed commit or write concern keeps its cause.** Rethrow the driver exception unchanged after a failed
  `OctoSession.CommitTransactionAsync`; map a write-concern-only failure to
  `OperationFailedException.DatabaseOperationFailed` with the driver exception inside; repository clients keep
  `w: majority` and read concern majority
  ([docs/runtime-write-semantics.md](docs/runtime-write-semantics.md),
  [docs/tenant-registry-and-clients.md](docs/tenant-registry-and-clients.md); `OctoSessionTransactionCallbackTests`,
  `MongoDbWriteExceptionMappingTests`).
- **Every MongoDB call of a runtime query goes through the time limiter.** A new call in the query classes runs through
  `RuntimeQueryTimeLimiter` (as `SingleOriginQuery.AggregateToListAsync` and `RtInterfaceQueryExecutor.RunLimitedAsync`
  do); overrides are call-site scopes (`RuntimeQueryTimeLimit.Override` / `Disable`), never a global switch. Known
  exception: the association and deep-graph queries are not covered yet
  ([docs/runtime-write-semantics.md](docs/runtime-write-semantics.md); `RuntimeQueryTimeLimitTests`).
- **A sorted page is total and an index hint is safe.** A page sorts by the caller's keys plus the `_id` tiebreak;
  a hint names only an index that provides the sort and is complete (never partial, sparse, hidden or collated), never
  inside a transaction ([docs/runtime-query-paging.md](docs/runtime-query-paging.md); `SortIndexHintTests`,
  `PagedQueryTiebreakTests`).
- **An interface query validates before it touches a collection** and refuses with `InterfaceQueryException`; a
  collection left with no allowed type is not queried at all
  ([docs/interface-query.md](docs/interface-query.md); `GetRtEntitiesByInterfaceAsyncTests`).
- **Hidden archive columns never leave through a backup or export.** New copy, export and restore code decides through
  `CrateDbStreamDataRepository.FindHiddenColumnExclusion`. Known exception: a column made Hidden by a CK model that
  cannot be read, and not yet recorded, can still end up in a backup
  ([docs/streamdata-write-path.md](docs/streamdata-write-path.md), "Backups leave Hidden columns out";
  `HiddenColumnExclusionTests`).
- **The physical table of an activated archive only grows.** Reconciliation adds declared columns and never drops or
  retypes one; a new column goes through the archive lifecycle, never into `Columns` alone
  ([docs/streamdata-archive-schema-and-activation.md](docs/streamdata-archive-schema-and-activation.md),
  [docs/streamdata-write-path.md](docs/streamdata-write-path.md); `ArchiveColumnReconciliationTests`,
  `ArchiveDefinitionGuardIntegrationTests`).
- **A `TimeSpan` attribute is stored as Int64 ticks.** Any new place that coerces an attribute value to `TimeSpan` also
  accepts the bare-integer ticks string, or it rejects already imported data
  ([docs/bson-serialization.md](docs/bson-serialization.md); `TimeSpanSerializerTests`).
- **A wall-clock budget or stress test carries `Category=Performance` or `Category=Stress`**, which keeps it out of the
  PR build and, with `Category=LoadSensitive`, out of the local full suite
  ([docs/test-configuration.md](docs/test-configuration.md); `LoadSensitiveTraitTests`).
