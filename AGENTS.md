# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the MongoDB implementation of the OctoMesh Runtime Engine. It provides:
- MongoDB-based persistence for runtime entities
- Query engine with support for field filters, navigation properties, and aggregations
- Integration with the Construction Kit (CK) model system

### Key Classes

- `TenantRepository` - Main repository for tenant-specific data operations
- `SingleOriginRtQuery<T>` - Query engine for single-origin queries with field filters and navigation
- `MultipleOriginHierarchicalDeepRtGraphQuery` - Deep graph queries following parent-child hierarchy
- `RtPathEvaluator` - Tokenizes and evaluates attribute paths including navigation properties
- `NavigationEnd` - Represents the end of a navigation (association target)
- `MongoRepositoryClient` - Base class that registers BSON conventions and class maps and builds the driver client (write concern majority / 30 s, read concern majority, see *Write and read concern of the repository clients*)
- `MongoRuntimeRepositoryProvider` - Provides tenant repositories for CK model migrations
- `TenantContext` - Per-tenant context managing CK model imports and migration triggers

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
| `src/Runtime.Engine.MongoDb/Repositories/TenantOwnership/**`, `src/Runtime.Contracts.MongoDb/TenantOwnership/**`, `src/Runtime.Engine.MongoDb/Repositories/TenantLifecycle/**` | `docs/tenant-database-safety.md` |
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

The rules of this repository are the sections of the routed docs; read the matching file before you change code it covers.
