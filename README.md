# octo-construction-kit-engine-mongodb

MongoDB-based runtime engine for the OCTO Construction Kit. This library provides the data access layer and repository implementations for storing and managing Construction Kit (CK) models and runtime entities in MongoDB.

## Overview

This project contains two main assemblies:

| Assembly                      | Purpose                                                          |
|-------------------------------|------------------------------------------------------------------|
| **Runtime.Contracts.MongoDb** | Contracts, interfaces, and entity definitions                    |
| **Runtime.Engine.MongoDb**    | MongoDB repository implementations, query handlers, and services |

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              Application Layer                              │
│                    (OCTO Services: Identity, Bot, Asset, etc.)              │
└─────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                              Context Layer                                  │
│  ┌─────────────────────┐    ┌─────────────────────┐                         │
│  │    SystemContext    │───▶│    TenantContext    │                         │
│  │  (System Tenant)    │    │  (Child Tenants)    │                         │
│  └─────────────────────┘    └─────────────────────┘                         │
│         │                            │                                      │
│         │  - Tenant Management       │  - CK Model Import                   │
│         │  - Backup/Restore          │  - Configuration                     │
│         │  - System Operations       │  - Index Management                  │
└─────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                             Repository Layer                                │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │                        TenantRepository                              │   │
│  │  - CRUD for CK entities (Types, Attributes, Enums, Records, Roles)   │   │
│  │  - CRUD for RT entities (Runtime instances)                          │   │
│  │  - Association management                                            │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
│                                      │                                      │
│  ┌────────────────────┐    ┌────────────────────┐    ┌──────────────────┐   │
│  │ DatabaseCkModel-   │    │ MongoDbRepository- │    │ MongoDbDataSource│   │
│  │ Repository         │    │ DataSource         │    │ Collection       │   │
│  │ (CK Import/Export) │    │ (Data Access)      │    │ (CRUD Ops)       │   │
│  └────────────────────┘    └────────────────────┘    └──────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           Query & Mutation Layer                            │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │                         Engine<TEntity>                              │   │
│  │  ┌─────────────────────┐        ┌─────────────────────┐              │   │
│  │  │   Query<TEntity>    │        │  Mutation<TEntity>  │              │   │
│  │  │  - Filtering        │        │  - Insert/Update    │              │   │
│  │  │  - Sorting          │        │  - Delete/Replace   │              │   │
│  │  │  - Aggregation      │        │  - Bulk Operations  │              │   │
│  │  │  - Text Search      │        │                     │              │   │
│  │  └─────────────────────┘        └─────────────────────┘              │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
│                                      │                                      │
│  ┌────────────────────────────────────────────────────────────────────┐     │
│  │                     FieldFilterResolver                            │     │
│  │  Converts application filters → MongoDB filter definitions         │     │
│  └────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                          MongoDB Generic Layer                              │
│  ┌─────────────────────┐    ┌─────────────────────┐                         │
│  │  MongoRepository    │    │ MongoRepositoryClient│                        │
│  │  - Collection Mgmt  │    │  - Connection Pool   │                        │
│  │  - Index Creation   │    │  - Session Mgmt      │                        │
│  └─────────────────────┘    └─────────────────────┘                         │
│                                      │                                      │
│  ┌────────────────────────────────────────────────────────────────────┐     │
│  │                    MongoDB Builder System                          │     │
│  │  FilterDefinition, ArrayDefinition, DocumentDefinition, etc.       │     │
│  └────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                              MongoDB Driver                                 │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Component Responsibilities

### Context Layer

| Component        | Responsibility                                            |
|------------------|-----------------------------------------------------------|
| `ISystemContext` | System-level operations, system tenant access             |
| `ITenantContext` | Tenant lifecycle, CK model imports, configuration         |
| `SystemContext`  | Implements system tenant with backup/restore capabilities |
| `TenantContext`  | Manages child tenants, provides repository access         |

### Repository Layer

| Component                      | Responsibility                                    |
|--------------------------------|---------------------------------------------------|
| `ITenantRepository`            | Main data access interface for CK and RT entities |
| `TenantRepository`             | Full CRUD implementation for all entity types     |
| `IDatabaseCkModelRepository`   | CK model versioning and import/export             |
| `DatabaseCkModelRepository`    | Model validation, import with distributed locking |
| `IMongoDbRepositoryDataSource` | Collection access and session management          |
| `MongoDbRepositoryDataSource`  | MongoDB collection wrappers, index management     |

### Query & Mutation System

| Component             | Responsibility                                      |
|-----------------------|-----------------------------------------------------|
| `Engine<TEntity>`     | Base class for query/mutation operations            |
| `Query<TEntity>`      | Read operations with filtering, sorting, pagination |
| `Mutation<TEntity>`   | Write operations (insert, update, delete, replace)  |
| `FieldFilterResolver` | Converts application filters to MongoDB queries     |

**Specialized Query Classes:**
- `SingleOriginRtQuery` - Queries on single runtime entity type
- `MultipleOriginDirectAssociationsRtQuery` - Direct association queries
- `MultipleOriginIndirectAssociationsRtQuery` - Deep association traversal
- `MultipleOriginHierarchicalDeepRtGraphQuery` - Hierarchical graph queries

### Service Layer

| Service                            | Responsibility                                       |
|------------------------------------|------------------------------------------------------|
| `ModelLoaderService`               | Loads CK models into cache with semaphore protection |
| `IndexStateService`                | Tracks index creation state per collection           |
| `RepositoryOpsService`             | MongoDB shell operations (mongodump, mongorestore)   |
| `TenantBackupService`              | Tenant backup and restore operations                 |
| `RepositoryDistributedLockService` | Distributed locking via `SysLock` entity             |

### Serialization Layer

Custom BSON serializers for OCTO types:
- `CkIdSerializer` - Construction Kit ID serialization
- `OctoObjectIdSerializer` - Runtime entity ID serialization
- `OctoObjectSerializer` / `OctoObjectListSerializer` - Complex object serialization
- `RtEntityDiscriminatorConvention` - Polymorphic entity handling

## Key Features

### Construction Kit Model Management

The engine handles importing, storing, and querying CK models in MongoDB. CK models define the schema for runtime entities including:

- Types (`CkType`)
- Attributes (`CkAttribute`)
- Enums (`CkEnum`)
- Records (`CkRecord`)
- Association Roles (`CkAssociationRole`)

### Distributed Locking for Parallel Safety

When multiple services start simultaneously, they may all attempt to import the System CK model at the same time. To prevent write conflicts and ensure data consistency, the engine uses a distributed locking mechanism.

#### How it works

The `RepositoryDistributedLockService` uses a `SysLock` entity stored in MongoDB to coordinate access:

```
Service A                              Service B
    │                                      │
    ▼                                      ▼
AcquireModelImportLockAsync            AcquireModelImportLockAsync
    │                                      │
    ▼                                      ▼
SysLock Insert (atomic)                SysLock Insert → DuplicateKey!
    │                                      │
    ✓ Lock acquired                        ▼ Waits for lock (retry)
    │                                      │
    ▼                                      │
Import CK Model...                     │
    │                                      │
    ▼                                      │
Lock released (Dispose)                │
                                           ▼
                                       Lock acquired
                                           │
                                           ▼
                                       IsExistingAsync → YES!
                                           │
                                           ▼
                                       Skip (model already exists)
```

#### Lock Features

- **Atomic locking** via MongoDB's `DuplicateKey` exception handling
- **Automatic retry** with configurable attempts and delay
- **TTL-based expiry** (10 minutes) to handle crashed services
- **Heartbeat mechanism** to extend locks for long-running operations
- **Stale lock recovery** to claim abandoned locks

#### Usage

```csharp
// In DatabaseCkModelRepository.ExecuteImport
await using var importLock = await mongoDbRepositoryDataSource
    .AcquireModelImportLockAsync(compiledModel.ModelId.Name);

// Safe to perform import - we have exclusive access
await InsertModelWithImportingState(compiledModel, mongoDbRepositoryDataSource);
// ... rest of import logic
// Lock automatically released when disposed
```

### Model States

CK models go through the following states during import:

| State           | Value | Description                                         |
|-----------------|-------|-----------------------------------------------------|
| `Importing`     | 0     | Model is currently being imported                   |
| `Available`     | 1     | Model is ready for use                              |
| `ResolveFailed` | 2     | Model failed to resolve due to missing dependencies |

## Project Structure

```
src/
├── Runtime.Contracts.MongoDb/           # Contracts & Entity Definitions
│   ├── Repositories/
│   │   └── Entities/
│   │       ├── CkModel.cs               # CK model entity with ModelState
│   │       ├── CkType.cs                # Type definition with indexes
│   │       ├── CkAttribute.cs           # Attribute definition
│   │       ├── CkEnum.cs                # Enum definition
│   │       ├── CkRecord.cs              # Record definition
│   │       ├── CkAssociationRole.cs     # Association role definition
│   │       └── SysLock.cs               # Distributed lock entity
│   ├── Configuration/
│   │   └── OctoSystemConfiguration.cs   # System configuration
│   └── Services/
│       ├── ISystemContext.cs            # System context interface
│       ├── ITenantContext.cs            # Tenant context interface
│       └── ITenantRepository.cs         # Repository interface
│
└── Runtime.Engine.MongoDb/              # Implementations
    ├── SystemContext.cs                 # System tenant implementation
    ├── TenantContext.cs                 # Tenant management implementation
    │
    ├── Repositories/
    │   ├── MongoDb/
    │   │   ├── TenantRepository.cs                    # Main repository
    │   │   ├── DatabaseCkModelRepository.cs           # CK model import
    │   │   ├── MongoDbRepositoryDataSource.cs         # Data source
    │   │   ├── MongoDbDataSourceCollection.cs         # Collection wrapper
    │   │   ├── RepositoryDistributedLockService.cs    # Distributed locking
    │   │   ├── ICkMongoDbRepositoryDataSource.cs      # CK data source interface
    │   │   │
    │   │   └── Generic/                               # Generic MongoDB layer
    │   │       ├── MongoRepository.cs                 # Collection management
    │   │       ├── MongoRepositoryClient.cs           # Connection management
    │   │       ├── AdminMongoRepositoryClient.cs      # Admin operations
    │   │       └── Builders/                          # MongoDB query builders
    │   │           ├── FilterDefinition.cs
    │   │           ├── ArrayDefinition.cs
    │   │           └── ...
    │   │
    │   └── Query/                                     # Query & Mutation System
    │       ├── Engine.cs                              # Base engine class
    │       ├── Query.cs                               # Read operations
    │       ├── Mutation.cs                            # Write operations
    │       ├── SingleOriginRtQuery.cs                 # RT entity queries
    │       ├── MultipleOriginDirectAssociationsRtQuery.cs
    │       └── FieldFilterResolver.cs                 # Filter conversion
    │
    ├── Services/
    │   ├── ModelLoaderService.cs                      # Model caching
    │   ├── IndexStateService.cs                       # Index tracking
    │   ├── RepositoryOpsService.cs                    # MongoDB shell ops
    │   └── TenantBackupService.cs                     # Backup/restore
    │
    ├── Serialization/                                 # BSON Serializers
    │   ├── CkIdSerializer.cs
    │   ├── OctoObjectIdSerializer.cs
    │   └── RtEntityDiscriminatorConvention.cs
    │
    └── CkCache/                                       # Model Caching
        └── CkCacheService.cs
```

## Data Flow

### CK Model Import Flow

```
┌──────────────┐     ┌───────────────────┐     ┌─────────────────────┐
│ CatalogService│────▶│ TenantContext.    │────▶│ DatabaseCkModel-    │
│ (get model)  │     │ ImportCkModelAsync│     │ Repository          │
└──────────────┘     └───────────────────┘     └─────────────────────┘
                                                         │
                     ┌───────────────────────────────────┘
                     ▼
        ┌─────────────────────────┐
        │ AcquireModelImportLock  │◀─── SysLock (distributed lock)
        └─────────────────────────┘
                     │
                     ▼
        ┌─────────────────────────┐
        │ InsertModelWithImporting│◀─── ModelState = Importing
        │ State                   │
        └─────────────────────────┘
                     │
                     ▼
        ┌─────────────────────────┐
        │ Validate & Import       │◀─── Types, Attributes, Enums, etc.
        │ Model Elements          │
        └─────────────────────────┘
                     │
                     ▼
        ┌─────────────────────────┐
        │ UpdateModelState        │◀─── ModelState = Available
        └─────────────────────────┘
                     │
                     ▼
        ┌─────────────────────────┐
        │ Release Lock (Dispose)  │
        └─────────────────────────┘
```

### CRUD Operations Flow

```
┌──────────────┐     ┌──────────────┐     ┌─────────────────┐
│ Application  │────▶│ TenantRepo-  │────▶│ Query/Mutation  │
│              │     │ sitory       │     │ Engine          │
└──────────────┘     └──────────────┘     └─────────────────┘
                                                   │
                     ┌─────────────────────────────┘
                     ▼
        ┌─────────────────────────┐
        │ FieldFilterResolver     │◀─── Converts app filters
        └─────────────────────────┘
                     │
                     ▼
        ┌─────────────────────────┐
        │ MongoDB Builder System  │◀─── FilterDefinition, etc.
        └─────────────────────────┘
                     │
                     ▼
        ┌─────────────────────────┐
        │ MongoDbDataSource-      │◀─── IOctoSession (transaction)
        │ Collection              │
        └─────────────────────────┘
                     │
                     ▼
        ┌─────────────────────────┐
        │ MongoDB Driver          │
        └─────────────────────────┘
```

## Building

```bash
dotnet build
```

## Testing

```bash
dotnet test
```