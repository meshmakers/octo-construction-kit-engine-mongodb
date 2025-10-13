# Project Overview

## Project Name
Octo Construction Kit Engine - MongoDB Implementation

## Purpose
This repository provides the MongoDB-based runtime engine for the OctoMesh data mesh platform. It implements data persistence, querying, and model management for the Construction Kit runtime using MongoDB as the underlying database.

## Tech Stack
- **Language**: C# with latest major version
- **Framework**: .NET 9.0
- **Database**: MongoDB 3.5.0 with MongoDB.Driver
- **Testing**: xUnit v3 with coverlet for code coverage
- **Key Dependencies**:
  - Meshmakers.Octo.Runtime.Engine (OctoMesh runtime core)
  - Meshmakers.Octo.ConstructionKit.Models.System (data models)
  - System.Reactive (Rx for streaming updates)
  - MathParser.org-mXparser (formula evaluation)
  - Microsoft.Extensions.* (dependency injection, caching, health checks)

## Repository Structure
- **src/**: Source code
  - `Runtime.Contracts.MongoDb/`: Contracts and interfaces for MongoDB runtime
  - `Runtime.Engine.MongoDb/`: Main MongoDB engine implementation
- **tests/**: Test projects
  - `Runtime.Engine.MongoDb.Tests/`: Unit tests
  - `Persistence.SystemTests/`: System/integration tests
  - `TestCkModel/`: Test data model
  - `SharedTests/`: Shared test utilities
- **samples/**: Sample applications
  - `WebService.Sample/`: ASP.NET Core web service demonstrating usage
- **devops-build/**: Build and deployment scripts
- **assets/**: Package icons and resources

## Key Architectural Components
1. **Repositories**: MongoDB data access layer with generic repository pattern
2. **Query Engine**: Advanced querying with aggregation pipeline support
3. **Serialization**: Custom BSON serializers for OctoMesh data types
4. **Sessions**: OctoAdminSession and OctoUserSession for data access contexts
5. **CK Model Repository**: Manages Construction Kit model metadata and compilation
6. **Formulas**: Expression evaluation using mXparser
7. **Services**: ModelLoader, IndexState, RepositoryOps, TenantBackup

## Configuration Modes
- **Debug**: Standard development build
- **DebugL**: Local development with version 999.0.0 and local NuGet packages from ../nuget
- **Release**: Production build with versioning

## Important Notes
- All builds use nullable reference types
- TreatWarningsAsErrors is enabled
- Implicit usings are enabled
- CRLF line endings (Windows development)
