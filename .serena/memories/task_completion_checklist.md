# Task Completion Checklist

When completing a development task, ensure the following steps are performed:

## Building
1. **Always build with DebugL configuration** for local development:
   ```bash
   dotnet build Octo.ConstructionKit.Engine.MongoDb.sln --configuration DebugL
   ```
2. Ensure the build completes without errors or warnings (TreatWarningsAsErrors is enabled)

## Testing
1. **Run all affected tests**:
   ```bash
   dotnet test Octo.ConstructionKit.Engine.MongoDb.sln --configuration DebugL
   ```
2. If only specific tests are relevant:
   ```bash
   dotnet test tests/Runtime.Engine.MongoDb.Tests --configuration DebugL
   ```
3. For system tests (if applicable):
   ```bash
   dotnet test tests/Persistence.SystemTests --configuration DebugL
   ```
4. Ensure all tests pass before considering the task complete

## Code Quality
1. **Address all compiler warnings** - warnings are treated as errors
2. **Follow naming conventions** as specified in .editorconfig
3. **Enable nullable reference types** - handle all potential null cases
4. **Use async/await** consistently for I/O operations

## Local Package Testing
If changes affect public APIs or contracts:
1. Pack the modified projects to local NuGet feed:
   ```bash
   dotnet pack src/Runtime.Contracts.MongoDb/Runtime.Contracts.MongoDb.csproj --configuration DebugL --output ../nuget
   dotnet pack src/Runtime.Engine.MongoDb/Runtime.Engine.MongoDb.csproj --configuration DebugL --output ../nuget
   ```
2. Test with consuming projects if available

## Git Workflow
1. **Stage relevant changes**:
   ```bash
   git add <files>
   ```
2. **Review changes** before committing:
   ```bash
   git diff --staged
   ```
3. **Commit with descriptive message** following project conventions
4. Check recent commits for message style:
   ```bash
   git log --oneline -n 5
   ```

## Documentation
1. Update code comments if behavior changes
2. XML documentation is not strictly required (CS1591 suppressed), but helpful for public APIs
3. Update README.md if adding new features or changing architecture

## Important Notes
- **DebugL configuration** uses version 999.0.0 and local NuGet packages
- The project uses .NET 9.0 and latest major C# version
- MongoDB 3.5.0 is the target database version
- System is Windows-based (CRLF line endings)
