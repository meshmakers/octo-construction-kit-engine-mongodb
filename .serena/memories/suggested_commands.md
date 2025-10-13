# Suggested Commands for Development

## Building

### Build the entire solution
```bash
dotnet build Octo.ConstructionKit.Engine.MongoDb.sln --configuration DebugL
```

### Build for Release
```bash
dotnet build Octo.ConstructionKit.Engine.MongoDb.sln --configuration Release
```

### Build specific project
```bash
dotnet build src/Runtime.Engine.MongoDb/Runtime.Engine.MongoDb.csproj --configuration DebugL
```

## Testing

### Run all tests in the solution
```bash
dotnet test Octo.ConstructionKit.Engine.MongoDb.sln --configuration DebugL
```

### Run tests for a specific project
```bash
dotnet test tests/Runtime.Engine.MongoDb.Tests/Runtime.Engine.MongoDb.Tests.csproj --configuration DebugL
```

### Run system tests
```bash
dotnet test tests/Persistence.SystemTests/Persistence.SystemTests.csproj --configuration DebugL
```

### Run a single test by name
```bash
dotnet test --filter "FullyQualifiedName~ClassName.TestMethodName" --configuration DebugL
```

### Run tests with code coverage
```bash
dotnet test --collect:"XPlat Code Coverage" --configuration DebugL
```

## Running the Sample

### Run the web service sample
```bash
dotnet run --project samples/WebService.Sample/WebService.Sample.csproj --configuration DebugL
```

## NuGet Package Management

### Restore packages
```bash
dotnet restore Octo.ConstructionKit.Engine.MongoDb.sln
```

### Pack projects for local development
```bash
dotnet pack src/Runtime.Contracts.MongoDb/Runtime.Contracts.MongoDb.csproj --configuration DebugL --output ../nuget
dotnet pack src/Runtime.Engine.MongoDb/Runtime.Engine.MongoDb.csproj --configuration DebugL --output ../nuget
```

## Windows-Specific Utility Commands

### List directories
```bash
dir
dir /s  # recursive
```

### Find files
```bash
dir /s /b *.cs  # find all .cs files recursively
```

### Search in files (using findstr)
```bash
findstr /s /i "searchterm" *.cs
```

### Git commands (work as on Unix)
```bash
git status
git log --oneline -n 10
git diff
```

## Important Notes
- Always use **DebugL** configuration for local development (not Debug or Release)
- The DebugL configuration uses version 999.0.0 and references local NuGet packages from ../nuget
- Line endings are CRLF (Windows)
