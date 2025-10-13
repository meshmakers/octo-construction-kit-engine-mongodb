# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

This repository provides the MongoDB-based runtime engine for the OctoMesh data mesh platform. It implements data persistence, querying, and model management for the Construction Kit runtime using MongoDB as the underlying database.

**Target Framework**: .NET 9.0
**Language**: C# (latest major version)
**Database**: MongoDB 3.5.0

## Development Commands

### Building

**IMPORTANT**: Always use the `DebugL` configuration for local development. The "L" suffix stands for "local" and uses version 999.0.0 with local NuGet packages from `../nuget`.

```bash
# Build the entire solution (use DebugL for local dev)
dotnet build Octo.ConstructionKit.Engine.MongoDb.sln --configuration DebugL

# Build for production
dotnet build Octo.ConstructionKit.Engine.MongoDb.sln --configuration Release

# Build specific project
dotnet build src/Runtime.Engine.MongoDb/Runtime.Engine.MongoDb.csproj --configuration DebugL
```

### Testing

```bash
# Run all tests
dotnet test Octo.ConstructionKit.Engine.MongoDb.sln --configuration DebugL

# Run tests for a specific project
dotnet test tests/Runtime.Engine.MongoDb.Tests/Runtime.Engine.MongoDb.Tests.csproj --configuration DebugL

# Run system tests
dotnet test tests/Persistence.SystemTests/Persistence.SystemTests.csproj --configuration DebugL

# Run a single test by name
dotnet test --filter "FullyQualifiedName~ClassName.TestMethodName" --configuration DebugL

# Run tests with code coverage
dotnet test --collect:"XPlat Code Coverage" --configuration DebugL
```

### Running the Sample

```bash
# Run the web service sample
dotnet run --project samples/WebService.Sample/WebService.Sample.csproj --configuration DebugL
```

### NuGet Package Management

```bash
# Restore packages
dotnet restore Octo.ConstructionKit.Engine.MongoDb.sln

# Pack projects for local development
dotnet pack src/Runtime.Contracts.MongoDb/Runtime.Contracts.MongoDb.csproj --configuration DebugL --output ../nuget
dotnet pack src/Runtime.Engine.MongoDb/Runtime.Engine.MongoDb.csproj --configuration DebugL --output ../nuget
```

## Architecture Overview

### Project Structure

- **src/Runtime.Contracts.MongoDb**: Contracts and interfaces for MongoDB runtime
- **src/Runtime.Engine.MongoDb**: Main MongoDB engine implementation
  - `Repositories/`: MongoDB data access layer with generic repository pattern
  - `Query/`: Advanced querying with aggregation pipeline support
  - `Serialization/`: Custom BSON serializers for OctoMesh data types
  - `Services/`: ModelLoader, IndexState, RepositoryOps, TenantBackup
  - `Formulas/`: Expression evaluation using mXparser
- **tests/Runtime.Engine.MongoDb.Tests**: Unit tests (xUnit)
- **tests/Persistence.SystemTests**: System/integration tests
- **tests/TestCkModel**: Test data model
- **samples/WebService.Sample**: ASP.NET Core web service demonstrating usage

### Key Architectural Components

1. **Repositories**: MongoDB data access using repository pattern
   - `MongoRepository`: Base repository with collection management
   - `DatabaseCkModelRepository`: Manages Construction Kit model metadata
   - Admin and User repository access contexts

2. **Sessions**: Data access contexts
   - `OctoAdminSession`: Administrative data operations
   - `OctoUserSession`: User-scoped data operations

3. **Query Engine**: Advanced querying with MongoDB aggregation pipeline builders
   - Custom pipeline stage definitions
   - Field filter resolvers
   - Support for single and multiple origin queries

4. **Serialization**: Custom BSON serializers for domain types
   - CkId, ModelId, OctoObjectId, OctoTypeId, etc.
   - RtEntity discriminator conventions

5. **Services**:
   - `ModelLoaderService`: Loads and manages CK models
   - `IndexStateService`: Manages MongoDB indexes
   - `TenantBackupService`: Backup and restore operations
   - `RepositoryOpsService`: Common repository operations

### Key Dependencies

- **Meshmakers.Octo.Runtime.Engine**: OctoMesh runtime core
- **Meshmakers.Octo.ConstructionKit.Models.System**: Data models
- **MongoDB.Driver** 3.5.0: MongoDB client
- **System.Reactive**: Rx for streaming updates
- **MathParser.org-mXparser**: Formula evaluation
- **xUnit**: Test framework

## Code Style and Conventions

### Naming Conventions

- **Classes/Methods/Properties**: PascalCase
- **Interfaces**: IPascalCase (prefix with `I`)
- **Private Fields**: _camelCase (prefix with underscore)
- **Local Variables/Parameters**: camelCase
- **Type Parameters**: TPascalCase (prefix with `T`)

### C# Preferences

- **var**: Do NOT use `var`; prefer explicit types
- **Nullable Reference Types**: Enabled project-wide
- **Braces**: Always use braces for control flow statements
- **Async/Await**: Use consistently for I/O operations; method names end with "Async"
- **Expression Bodies**: Use for properties, accessors, and indexers; NOT for methods or constructors
- **Pattern Matching**: Prefer pattern matching over `as`/`is` with explicit casts

### Formatting

- **Indentation**: 4 spaces
- **Line Length**: 120 characters max
- **Line Endings**: CRLF (Windows)
- **Braces**: New line before open brace for all constructs
- **Using Directives**: Outside namespace, System directives first

### Code Quality

- **TreatWarningsAsErrors**: true (all warnings must be addressed)
- **Implicit Usings**: Enabled
- **readonly Fields**: Use for fields that don't change (warning enforced)

## Configuration Modes

- **Debug**: Standard development build
- **DebugL**: Local development with version 999.0.0 and local NuGet feed from `../nuget` (USE THIS FOR LOCAL DEV)
- **Release**: Production build with proper versioning

## Windows Development

This project is developed on Windows. Common commands:

```bash
# List files
dir
dir /s  # recursive

# Find files
dir /s /b *.cs

# Search in files
findstr /s /i "searchterm" *.cs

# Git commands work as normal
git status
git log --oneline -n 10
```

## When Task is Complete

1. **Build** with DebugL configuration and ensure no warnings/errors
2. **Run tests** to ensure all pass
3. **Pack to local NuGet** if API changes were made
4. **Follow commit message conventions** (check recent commits with `git log`)
