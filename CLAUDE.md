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
