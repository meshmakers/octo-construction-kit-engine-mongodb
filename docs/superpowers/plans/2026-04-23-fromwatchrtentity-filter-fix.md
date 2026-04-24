# FromWatchRtEntity@1 Filter Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix two MongoDB change-stream filter bugs and add a pre-image-capture guard so `FromWatchRtEntity@1` pipeline triggers correctly honour their configured `fieldFilters` and `beforeFieldFilters`.

**Architecture:** Two-repo change in strict dependency order — `octo-construction-kit-engine` (surface `EnableChangeStreamPreAndPostImages` on `CkTypeGraph`), then `octo-construction-kit-engine-mongodb` (fix `BuildExtensions.Inject` to honour its `fieldName` arg, replace the `Or` in `WatchAsync` with `And`, add named constants for `fullDocument` / `fullDocumentBeforeChange`, add a fail-fast guard in `TenantRepository.WatchRtEntitiesAsync`).

**Tech Stack:** .NET 10, xUnit v3, FluentAssertions, Testcontainers.MongoDb (replica-set), MongoDB C# driver, PowerShell `Invoke-BuildAll` for cross-repo builds.

**Spec:** `octo-construction-kit-engine-mongodb/docs/superpowers/specs/2026-04-23-fromwatchrtentity-filter-fix-design.md`

---

## Repo paths

All paths in this plan are relative to the monorepo root `/Users/reimar/dev/meshmakers/branches/main`. Always use `git -C <repo-folder>` for git — never `cd` into sub-repos (see `CLAUDE.md`).

- **CK engine repo:** `octo-construction-kit-engine`
- **MongoDB repo:** `octo-construction-kit-engine-mongodb`

## Build/test loop (load once)

Before starting work, load the PowerShell profile in your terminal session:

```powershell
pwsh
. ./octo-tools/modules/profile.ps1
```

This gives you `Invoke-BuildAll`, `Invoke-Build`, `Sync-AllGitRepos`, etc.

**Standard build commands for this plan:**

```bash
# Build just the CK engine repo (needed after Task 1)
Invoke-Build -repositoryPath ./octo-construction-kit-engine -configuration DebugL

# Build everything in dependency order (after Task 1 before anything in the MongoDB repo)
Invoke-BuildAll -configuration DebugL

# Run tests in the CK engine repo (from /Users/reimar/dev/meshmakers/branches/main)
dotnet test octo-construction-kit-engine/tests/ConstructionKit.Engine.Tests/ConstructionKit.Engine.Tests.csproj -c DebugL

# Run StreamData unit tests
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL

# Run MongoDB integration tests (uses Testcontainers, Docker must be running)
dotnet test octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/Runtime.Engine.MongoDb.IntegrationTests.csproj -c DebugL
```

After changes in `octo-construction-kit-engine`, **always re-run `Invoke-BuildAll`** before rebuilding the MongoDB repo — it repacks the CK engine NuGet packages into `../nuget/` where the MongoDB repo picks them up (see `CLAUDE.md` → Build System).

---

## Phase A — CK engine: surface the flag on CkTypeGraph

### Task 1: Expose `EnableChangeStreamPreAndPostImages` on `CkTypeGraph`

**Files:**
- Modify: `octo-construction-kit-engine/src/ConstructionKit.Contracts/DependencyGraph/CkTypeGraph.cs`
- Create: `octo-construction-kit-engine/tests/ConstructionKit.Engine.Tests/DependencyGraph/CkTypeGraphTests.cs`

- [ ] **Step 1: Write the failing test**

Create `octo-construction-kit-engine/tests/ConstructionKit.Engine.Tests/DependencyGraph/CkTypeGraphTests.cs`:

```csharp
using System.Text.Json;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.ConstructionKit.Engine.Tests.DependencyGraph;

public class CkTypeGraphTests
{
    private static readonly CkId<CkTypeId> SampleTypeId =
        new(new CkModelId("Sample", new CkVersion(1, 0, 0)), new CkTypeId("WatchTarget"));

    [Fact]
    public void EnableChangeStreamPreAndPostImages_is_false_when_dto_flag_default()
    {
        var dto = new CkCompiledTypeDto
        {
            TypeId = new CkTypeId("WatchTarget"),
        };

        var graph = new CkTypeGraph(SampleTypeId, dto);

        Assert.False(graph.EnableChangeStreamPreAndPostImages);
    }

    [Fact]
    public void EnableChangeStreamPreAndPostImages_is_propagated_from_dto()
    {
        var dto = new CkCompiledTypeDto
        {
            TypeId = new CkTypeId("WatchTarget"),
            EnableChangeStreamPreAndPostImages = true,
        };

        var graph = new CkTypeGraph(SampleTypeId, dto);

        Assert.True(graph.EnableChangeStreamPreAndPostImages);
    }

    [Fact]
    public void EnableChangeStreamPreAndPostImages_round_trips_through_json_constructor()
    {
        var dto = new CkCompiledTypeDto
        {
            TypeId = new CkTypeId("WatchTarget"),
            EnableChangeStreamPreAndPostImages = true,
        };
        var original = new CkTypeGraph(SampleTypeId, dto);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<CkTypeGraph>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.EnableChangeStreamPreAndPostImages);
    }

    [Fact]
    public void EnableChangeStreamPreAndPostImages_round_trips_when_false()
    {
        var dto = new CkCompiledTypeDto
        {
            TypeId = new CkTypeId("WatchTarget"),
        };
        var original = new CkTypeGraph(SampleTypeId, dto);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<CkTypeGraph>(json);

        Assert.NotNull(restored);
        Assert.False(restored!.EnableChangeStreamPreAndPostImages);
    }
}
```

- [ ] **Step 2: Run the tests — they must fail**

```bash
dotnet test octo-construction-kit-engine/tests/ConstructionKit.Engine.Tests/ConstructionKit.Engine.Tests.csproj -c DebugL --filter "FullyQualifiedName~CkTypeGraphTests"
```

Expected: tests fail to compile because `EnableChangeStreamPreAndPostImages` does not yet exist on `CkTypeGraph`.

- [ ] **Step 3: Add the property to `CkTypeGraph`**

Edit `octo-construction-kit-engine/src/ConstructionKit.Contracts/DependencyGraph/CkTypeGraph.cs`.

In the `CkCompiledTypeDto` constructor (`public CkTypeGraph(CkId<CkTypeId> ckTypeId, CkCompiledTypeDto ckTypeDto)` at line 26), add this assignment next to the other flag copies (after the `IsCollectionRoot = ckTypeDto.IsCollectionRoot;` line):

```csharp
EnableChangeStreamPreAndPostImages = ckTypeDto.EnableChangeStreamPreAndPostImages;
```

In the `[JsonConstructor]` (line 63), **append** `bool enableChangeStreamPreAndPostImages` as the last parameter:

```csharp
[JsonConstructor]
public CkTypeGraph(CkId<CkTypeId> ckTypeId, bool isAbstract, bool isFinal, bool isCollectionRoot, bool isStreamType,
    IReadOnlyCollection<CkGraphTypeInheritance> baseTypes,
    CkId<CkTypeId>? derivedFromCkTypeId,
    CkId<CkTypeId>? definingCollectionRootCkTypeId,
    IReadOnlyCollection<CkGraphTypeInheritance> derivedTypes,
    IReadOnlyCollection<CkTypeAttributeDto> definedAttributes,
    IReadOnlyDictionary<CkId<CkAttributeId>, CkTypeAttributeGraph> allAttributes,
    IReadOnlyCollection<CkTypeIndexDto> indexes, CkGraphDirectedAssociations associations, string description,
    bool enableChangeStreamPreAndPostImages)
    : base(definedAttributes, allAttributes)
{
    // ... existing body ...
    EnableChangeStreamPreAndPostImages = enableChangeStreamPreAndPostImages;
}
```

Add the public property next to the other `IsXxx` flags (after the `IsStreamType { get; set; }` declaration):

```csharp
/// <summary>
///     Gets a value indicating whether the change stream should include pre- and post-images
///     for this type's collection. Required on the collection-root CK type for
///     <c>fullDocumentBeforeChange</c> filters to evaluate.
/// </summary>
public bool EnableChangeStreamPreAndPostImages { get; }
```

- [ ] **Step 4: Run the tests — they must pass**

```bash
dotnet test octo-construction-kit-engine/tests/ConstructionKit.Engine.Tests/ConstructionKit.Engine.Tests.csproj -c DebugL --filter "FullyQualifiedName~CkTypeGraphTests"
```

Expected: all 4 `CkTypeGraphTests` pass.

- [ ] **Step 5: Build the whole CK engine repo and run its full test suite**

```bash
Invoke-Build -repositoryPath ./octo-construction-kit-engine -configuration DebugL
dotnet test octo-construction-kit-engine -c DebugL --filter "FullyQualifiedName!~SystemTests"
```

Expected: no new failures. System tests (which require running services) are excluded.

If any serializer test (`JsonSerializerTests`, `YamlSerializerTests`, `BlueprintYamlSerializerTests`) fails with a snapshot/round-trip difference caused by the new property, inspect the failure and update the snapshot to include the new field.

- [ ] **Step 6: Commit**

```bash
git -C octo-construction-kit-engine add src/ConstructionKit.Contracts/DependencyGraph/CkTypeGraph.cs tests/ConstructionKit.Engine.Tests/DependencyGraph/CkTypeGraphTests.cs
git -C octo-construction-kit-engine commit -m "New: Surface EnableChangeStreamPreAndPostImages on CkTypeGraph

Expose CkTypeDto.EnableChangeStreamPreAndPostImages through CkTypeGraph so the
MongoDB runtime layer can guard against subscriptions with BeforeFieldFilterCriteria
against collection roots that don't have pre-image capture enabled.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 7: Rebuild the CK engine NuGet package into `../nuget`**

```bash
Invoke-BuildAll -configuration DebugL
```

Expected: build succeeds; `./nuget/` now contains updated `Meshmakers.Octo.ConstructionKit.Contracts.999.0.0.nupkg` etc. This is required before any MongoDB-repo work.

---

## Phase B — MongoDB repo: named constants for change-stream field names

### Task 2: Add `ChangeStreamFullDocument` / `ChangeStreamFullDocumentBeforeChange` constants

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Constants.cs`
- Create: `octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/ChangeStreamFieldConstantsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/ChangeStreamFieldConstantsTests.cs`:

```csharp
using Meshmakers.Octo.Runtime.Engine.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests;

public class ChangeStreamFieldConstantsTests
{
    [Fact]
    public void ChangeStreamFullDocument_matches_mongodb_field_name()
    {
        Assert.Equal("fullDocument", Constants.ChangeStreamFullDocument);
    }

    [Fact]
    public void ChangeStreamFullDocumentBeforeChange_matches_mongodb_field_name()
    {
        Assert.Equal("fullDocumentBeforeChange", Constants.ChangeStreamFullDocumentBeforeChange);
    }
}
```

- [ ] **Step 2: Run the test — it must fail**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL --filter "FullyQualifiedName~ChangeStreamFieldConstantsTests"
```

Expected: compile error because the two constants don't exist.

- [ ] **Step 3: Add the constants**

Edit `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Constants.cs`. Add this block at the end of the `public static class Constants` body (just before the closing brace of the class):

```csharp
// ********************************************************************
// MongoDB change-stream document field names
// ********************************************************************

/// <summary>
/// Name of the post-image field in a MongoDB change-stream document
/// (<c>ChangeStreamDocument.FullDocument</c>).
/// </summary>
public const string ChangeStreamFullDocument = "fullDocument";

/// <summary>
/// Name of the pre-image field in a MongoDB change-stream document
/// (<c>ChangeStreamDocument.FullDocumentBeforeChange</c>). Only populated when the
/// collection has <c>changeStreamPreAndPostImages: { enabled: true }</c>.
/// </summary>
public const string ChangeStreamFullDocumentBeforeChange = "fullDocumentBeforeChange";
```

- [ ] **Step 4: Run the tests — they must pass**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL --filter "FullyQualifiedName~ChangeStreamFieldConstantsTests"
```

Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add src/Runtime.Engine.MongoDb/Constants.cs tests/StreamData.UnitTests/ChangeStreamFieldConstantsTests.cs
git -C octo-construction-kit-engine-mongodb commit -m "New: Add change-stream document field name constants

Replace scattered magic strings 'fullDocument' and 'fullDocumentBeforeChange'
with named constants on Runtime.Engine.MongoDb.Constants. Preparation for
fixing the Inject field-name regression.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase C — Bug 1: `BuildExtensions.Inject` honours its `fieldName` argument

### Task 3: Fix `BuildExtensions.Inject` to use the `fieldName` parameter

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/Generic/BuildExtensions.cs`
- Create: `octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/BuildExtensionsInjectTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/BuildExtensionsInjectTests.cs`:

```csharp
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests;

public class BuildExtensionsInjectTests
{
    private sealed record Inner(string Name, int Age);
    private sealed record Outer(Inner Inner);

    private static BsonDocument Render<T>(FilterDefinition<T> filter)
    {
        var registry = BsonSerializer.SerializerRegistry;
        var renderArgs = new RenderArgs<T>(registry.GetSerializer<T>(), registry);
        return filter.Render(renderArgs);
    }

    [Fact]
    public void Inject_prefixes_simple_equality_filter_with_fullDocument()
    {
        var innerFilter = Builders<Inner>.Filter.Eq(i => i.Name, "alice");

        var injected = Builders<Outer>.Filter.Inject(
            Constants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        Assert.True(rendered.Contains("fullDocument.Name"));
        Assert.Equal("alice", rendered["fullDocument.Name"].AsString);
    }

    [Fact]
    public void Inject_prefixes_simple_equality_filter_with_fullDocumentBeforeChange()
    {
        // Regression for Bug 1: before this fix, Inject ignored its fieldName
        // argument and hardcoded "fullDocument.", so a filter intended for the
        // pre-image was silently evaluated against the post-image.
        var innerFilter = Builders<Inner>.Filter.Eq(i => i.Name, "alice");

        var injected = Builders<Outer>.Filter.Inject(
            Constants.ChangeStreamFullDocumentBeforeChange, innerFilter);

        var rendered = Render(injected);
        Assert.True(rendered.Contains("fullDocumentBeforeChange.Name"),
            $"Expected pre-image prefix, got: {rendered}");
        Assert.False(rendered.Contains("fullDocument.Name"),
            "Must not evaluate pre-image filter against post-image field");
    }

    [Fact]
    public void Inject_prefixes_all_fields_in_And_filter()
    {
        var innerFilter = Builders<Inner>.Filter.And(
            Builders<Inner>.Filter.Eq(i => i.Name, "alice"),
            Builders<Inner>.Filter.Gt(i => i.Age, 30));

        var injected = Builders<Outer>.Filter.Inject(
            Constants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        var andArray = rendered["$and"].AsBsonArray;
        Assert.Equal(2, andArray.Count);
        Assert.True(andArray[0].AsBsonDocument.Contains("fullDocument.Name"));
        Assert.True(andArray[1].AsBsonDocument.Contains("fullDocument.Age"));
    }

    [Fact]
    public void Inject_prefixes_all_fields_in_Or_filter()
    {
        var innerFilter = Builders<Inner>.Filter.Or(
            Builders<Inner>.Filter.Eq(i => i.Name, "alice"),
            Builders<Inner>.Filter.Eq(i => i.Name, "bob"));

        var injected = Builders<Outer>.Filter.Inject(
            Constants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        var orArray = rendered["$or"].AsBsonArray;
        Assert.Equal(2, orArray.Count);
        Assert.True(orArray[0].AsBsonDocument.Contains("fullDocument.Name"));
        Assert.True(orArray[1].AsBsonDocument.Contains("fullDocument.Name"));
    }

    [Fact]
    public void Inject_prefixes_fields_in_nested_operators()
    {
        var innerFilter = Builders<Inner>.Filter.And(
            Builders<Inner>.Filter.Or(
                Builders<Inner>.Filter.Eq(i => i.Name, "alice"),
                Builders<Inner>.Filter.Eq(i => i.Name, "bob")),
            Builders<Inner>.Filter.Gte(i => i.Age, 18));

        var injected = Builders<Outer>.Filter.Inject(
            Constants.ChangeStreamFullDocumentBeforeChange, innerFilter);

        var rendered = Render(injected);
        var andArray = rendered["$and"].AsBsonArray;
        var orBranch = andArray[0].AsBsonDocument["$or"].AsBsonArray;
        Assert.True(orBranch[0].AsBsonDocument.Contains("fullDocumentBeforeChange.Name"));
        Assert.True(andArray[1].AsBsonDocument.Contains("fullDocumentBeforeChange.Age"));
    }

    [Fact]
    public void Inject_handles_In_operator_with_array_value()
    {
        var innerFilter = Builders<Inner>.Filter.In(i => i.Name, new[] { "alice", "bob" });

        var injected = Builders<Outer>.Filter.Inject(
            Constants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        Assert.True(rendered.Contains("fullDocument.Name"));
        Assert.Equal(BsonType.Array, rendered["fullDocument.Name"].AsBsonDocument["$in"].BsonType);
    }

    [Fact]
    public void Inject_handles_empty_filter()
    {
        var innerFilter = Builders<Inner>.Filter.Empty;

        var injected = Builders<Outer>.Filter.Inject(
            Constants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        Assert.Empty(rendered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Inject_throws_when_fieldName_is_null_empty_or_whitespace(string? fieldName)
    {
        var innerFilter = Builders<Inner>.Filter.Eq(i => i.Name, "alice");

        Assert.ThrowsAny<ArgumentException>(
            () => Builders<Outer>.Filter.Inject(fieldName!, innerFilter));
    }
}
```

- [ ] **Step 2: Run the tests — they must fail**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL --filter "FullyQualifiedName~BuildExtensionsInjectTests"
```

Expected: the `fullDocumentBeforeChange` test fails (Bug 1 — the before-prefix is wrong). The null/empty/whitespace `Inject_throws_*` tests also fail because no validation exists. Other tests may pass or fail depending on which field the hardcoded prefix happens to match.

- [ ] **Step 3: Fix `BuildExtensions.Inject`**

Edit `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/Generic/BuildExtensions.cs`. Add `using Meshmakers.Common.Shared;` at the top of the file (next to the existing usings). Then replace the `Inject` method (lines 11–31) with:

```csharp
/// <summary>
/// Injects an existing <see cref="FilterDefinition{TInnerDocument}"/> into a
/// <see cref="FilterDefinition{TDocument}"/> by prefixing every field name with
/// <paramref name="fieldName"/> + ".".
/// Used to re-target a filter written against an entity type onto a wrapper
/// document (e.g. a MongoDB change-stream document's <c>fullDocument</c> or
/// <c>fullDocumentBeforeChange</c> member).
/// </summary>
/// <typeparam name="TDocument">Outer document type (e.g. ChangeStreamDocument&lt;T&gt;).</typeparam>
/// <typeparam name="TInnerDocument">Inner document type the filter was built for.</typeparam>
/// <param name="this">Filter builder for <typeparamref name="TDocument"/>.</param>
/// <param name="fieldName">Non-empty field path to prefix (e.g. "fullDocument" or "fullDocumentBeforeChange").</param>
/// <param name="filter">The inner filter to re-target.</param>
internal static FilterDefinition<TDocument> Inject<TDocument, TInnerDocument>(
    this FilterDefinitionBuilder<TDocument> @this, string fieldName, FilterDefinition<TInnerDocument> filter)
{
    ArgumentValidation.ValidateString(nameof(fieldName), fieldName);

    var renderArgs = new RenderArgs<TInnerDocument>(
        BsonSerializer.SerializerRegistry.GetSerializer<TInnerDocument>(),
        BsonSerializer.SerializerRegistry);
    var renderedFilter = filter.Render(renderArgs);

    var prefixedFilter = PrefixFieldNames(renderedFilter, fieldName + ".");

    return new BsonDocumentFilterDefinition<TDocument>(prefixedFilter);
}
```

Do not modify `PrefixFieldNames` — it is correct.

- [ ] **Step 4: Run the tests — they must pass**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL --filter "FullyQualifiedName~BuildExtensionsInjectTests"
```

Expected: all 10 tests pass.

- [ ] **Step 5: Update `Subscription.cs` to use the new constants**

Edit `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/Query/Subscription.cs`. Replace the two magic strings at lines 39 and 42:

```csharp
return rtCollection.WatchAsync(updateTypes,
    filterDefinitions == null
        ? null
        : () => Builders<ChangeStreamDocument<TEntity>>.Filter.Inject(
            Constants.ChangeStreamFullDocument, filterDefinitions),
    beforeFilterDefinitions == null
        ? null
        : () => Builders<ChangeStreamDocument<TEntity>>.Filter.Inject(
            Constants.ChangeStreamFullDocumentBeforeChange, beforeFilterDefinitions),
    cancellationToken);
```

Add `using Meshmakers.Octo.Runtime.Engine.MongoDb;` at the top of `Subscription.cs` if it isn't already there (it's needed for `Constants`).

- [ ] **Step 6: Build the MongoDB repo to confirm the rename compiles**

```bash
Invoke-Build -repositoryPath ./octo-construction-kit-engine-mongodb -configuration DebugL
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add \
    src/Runtime.Engine.MongoDb/Repositories/MongoDb/Generic/BuildExtensions.cs \
    src/Runtime.Engine.MongoDb/Repositories/Query/Subscription.cs \
    tests/StreamData.UnitTests/BuildExtensionsInjectTests.cs
git -C octo-construction-kit-engine-mongodb commit -m "Fix: BuildExtensions.Inject honours its fieldName parameter

Inject hardcoded the 'fullDocument.' prefix even when callers passed
'fullDocumentBeforeChange', causing change-stream pre-image filters on
FromWatchRtEntity@1 subscriptions to silently evaluate against the post-image
and miss events. Thread the fieldName parameter through PrefixFieldNames and
validate it is non-empty. Subscription.cs now uses the named constants.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase D — Bug 2: pre- and post-image filters are AND'd, not OR'd

### Task 4: Extract `ComposeWatchFilter` helper with unit tests

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/Generic/BuildExtensions.cs`
- Create: `octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/WatchFilterCompositionTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/WatchFilterCompositionTests.cs`:

```csharp
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests;

public class WatchFilterCompositionTests
{
    private sealed record Doc(string Name);

    private static BsonDocument Render(FilterDefinition<ChangeStreamDocument<Doc>> filter)
    {
        var registry = BsonSerializer.SerializerRegistry;
        var renderArgs = new RenderArgs<ChangeStreamDocument<Doc>>(
            registry.GetSerializer<ChangeStreamDocument<Doc>>(), registry);
        return filter.Render(renderArgs);
    }

    private static FilterDefinition<ChangeStreamDocument<Doc>> MakeAfter(string name)
        => Builders<ChangeStreamDocument<Doc>>.Filter.Eq("fullDocument.Name", name);

    private static FilterDefinition<ChangeStreamDocument<Doc>> MakeBefore(string name)
        => Builders<ChangeStreamDocument<Doc>>.Filter.Eq("fullDocumentBeforeChange.Name", name);

    [Fact]
    public void Compose_returns_null_when_both_filters_null()
    {
        var result = BuildExtensions.ComposeWatchFilter<Doc>(null, null);

        Assert.Null(result);
    }

    [Fact]
    public void Compose_returns_after_filter_when_only_after_is_set()
    {
        var after = MakeAfter("alice");

        var result = BuildExtensions.ComposeWatchFilter(after, null);

        Assert.NotNull(result);
        var rendered = Render(result!);
        Assert.True(rendered.Contains("fullDocument.Name"));
        Assert.False(rendered.Contains("$and"));
    }

    [Fact]
    public void Compose_returns_before_filter_when_only_before_is_set()
    {
        var before = MakeBefore("alice");

        var result = BuildExtensions.ComposeWatchFilter<Doc>(null, before);

        Assert.NotNull(result);
        var rendered = Render(result!);
        Assert.True(rendered.Contains("fullDocumentBeforeChange.Name"));
        Assert.False(rendered.Contains("$and"));
    }

    [Fact]
    public void Compose_uses_And_not_Or_when_both_filters_set()
    {
        // Regression for Bug 2: the previous code used Filter.Or which let events
        // through when only one image matched.
        var after = MakeAfter("alice");
        var before = MakeBefore("bob");

        var result = BuildExtensions.ComposeWatchFilter(after, before);

        Assert.NotNull(result);
        var rendered = Render(result!);
        Assert.True(rendered.Contains("$and"), $"Expected $and at top level, got: {rendered}");
        Assert.False(rendered.Contains("$or"), $"Must not use $or: {rendered}");
    }

    [Fact]
    public void Compose_And_preserves_both_branches()
    {
        var after = MakeAfter("alice");
        var before = MakeBefore("bob");

        var result = BuildExtensions.ComposeWatchFilter(after, before);
        var rendered = Render(result!);

        var andArray = rendered["$and"].AsBsonArray;
        Assert.Equal(2, andArray.Count);
        var branches = string.Join(";", andArray);
        Assert.Contains("fullDocument.Name", branches);
        Assert.Contains("fullDocumentBeforeChange.Name", branches);
    }
}
```

- [ ] **Step 2: Run the tests — they must fail**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL --filter "FullyQualifiedName~WatchFilterCompositionTests"
```

Expected: compile error — `ComposeWatchFilter` doesn't exist yet.

Note: the unit tests call `BuildExtensions.ComposeWatchFilter` — so for the tests to access it, it must be `internal` and the test project must see internals. The test project already references `Runtime.Engine.MongoDb.csproj`; add `InternalsVisibleTo` if needed (Step 3 covers this).

- [ ] **Step 3: Add `ComposeWatchFilter` to `BuildExtensions`**

Edit `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/Generic/BuildExtensions.cs`. Add this method after the existing `Inject`/`PrefixFieldNames` pair (before `BuildIdFilter`):

```csharp
/// <summary>
/// Composes optional post-image and pre-image change-stream filters into a single
/// filter. When both are set they are combined with <c>$and</c> — the pre- and
/// post-image constraints are independent and must both hold for an event to pass.
/// </summary>
internal static FilterDefinition<ChangeStreamDocument<TDocument>>? ComposeWatchFilter<TDocument>(
    FilterDefinition<ChangeStreamDocument<TDocument>>? afterFilter,
    FilterDefinition<ChangeStreamDocument<TDocument>>? beforeFilter)
{
    var filters = new List<FilterDefinition<ChangeStreamDocument<TDocument>>>();
    if (afterFilter != null) filters.Add(afterFilter);
    if (beforeFilter != null) filters.Add(beforeFilter);
    return filters.Count switch
    {
        0 => null,
        1 => filters[0],
        _ => Builders<ChangeStreamDocument<TDocument>>.Filter.And(filters),
    };
}
```

Grant the unit-test assembly access to internals. Check if `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Runtime.Engine.MongoDb.csproj` already declares `InternalsVisibleTo` for the StreamData unit test assembly (assembly name `Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests`). If not, add to the csproj:

```xml
<ItemGroup>
    <InternalsVisibleTo Include="Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests" />
</ItemGroup>
```

- [ ] **Step 4: Run the tests — they must pass**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL --filter "FullyQualifiedName~WatchFilterCompositionTests"
```

Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add \
    src/Runtime.Engine.MongoDb/Repositories/MongoDb/Generic/BuildExtensions.cs \
    src/Runtime.Engine.MongoDb/Runtime.Engine.MongoDb.csproj \
    tests/StreamData.UnitTests/WatchFilterCompositionTests.cs
git -C octo-construction-kit-engine-mongodb commit -m "New: Add ComposeWatchFilter helper with AND composition

Preparation for fixing Bug 2 — change-stream pre/post-image filters must be
combined with \$and, not \$or. Extract the composition into a testable helper
on BuildExtensions with unit-test coverage, then wire it into
MongoDbDataSourceCollection.WatchAsync in a follow-up commit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 5: Wire `ComposeWatchFilter` into `MongoDbDataSourceCollection.WatchAsync`

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/MongoDbDataSourceCollection.cs`

- [ ] **Step 1: Replace the three-way if/else in `WatchAsync`**

Edit `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/MongoDbDataSourceCollection.cs`. Replace lines 824–836 (the block that starts with `if (documentFilter != null && documentBeforeFilter != null)` and ends with the third `else if`) with:

```csharp
var combinedFilter = BuildExtensions.ComposeWatchFilter(documentFilter, documentBeforeFilter);
if (combinedFilter != null)
{
    pipeline = pipeline.Match(combinedFilter);
}
```

Leave the filter-function invocation lines (811–822) untouched.

- [ ] **Step 2: Build the MongoDB repo**

```bash
Invoke-Build -repositoryPath ./octo-construction-kit-engine-mongodb -configuration DebugL
```

Expected: build succeeds.

- [ ] **Step 3: Run the full StreamData unit-test suite to make sure nothing regressed**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL
```

Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add src/Runtime.Engine.MongoDb/Repositories/MongoDb/MongoDbDataSourceCollection.cs
git -C octo-construction-kit-engine-mongodb commit -m "Fix: Combine change-stream pre/post-image filters with AND not OR

WatchAsync previously OR'd the post-image and pre-image filters when both were
set, letting events through whenever either image matched. Pre/post-image
filters are independent constraints that must both hold, so combine them with
\$and via the new BuildExtensions.ComposeWatchFilter helper.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase E — Pre-image-capture guard

### Task 6: Add `OperationFailedException.PreImageCaptureNotEnabled` factory

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/src/Runtime.Contracts.MongoDb/OperationFailedException.cs`
- Create: `octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/OperationFailedExceptionFactoryTests.cs`

- [ ] **Step 1: Write the failing test**

Create `octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/OperationFailedExceptionFactoryTests.cs`:

```csharp
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests;

public class OperationFailedExceptionFactoryTests
{
    [Fact]
    public void PreImageCaptureNotEnabled_message_names_the_root_type_and_flag()
    {
        var rootId = new CkId<CkTypeId>(
            new CkModelId("Sample", new CkVersion(1, 0, 0)),
            new CkTypeId("WatchTarget"));

        var ex = OperationFailedException.PreImageCaptureNotEnabled(rootId);

        Assert.IsType<OperationFailedException>(ex);
        Assert.Contains("WatchTarget", ex.Message);
        Assert.Contains("EnableChangeStreamPreAndPostImages", ex.Message);
        Assert.Contains("BeforeFieldFilterCriteria", ex.Message);
    }
}
```

- [ ] **Step 2: Run the test — it must fail**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL --filter "FullyQualifiedName~OperationFailedExceptionFactoryTests"
```

Expected: compile error — `PreImageCaptureNotEnabled` doesn't exist.

- [ ] **Step 3: Add the factory**

Edit `octo-construction-kit-engine-mongodb/src/Runtime.Contracts.MongoDb/OperationFailedException.cs`. Add this static factory in the list next to `CkTypeHasNoDefiningCollectionRoot`:

```csharp
public static Exception PreImageCaptureNotEnabled(CkId<CkTypeId> rootCkTypeId)
{
    return new OperationFailedException(
        $"CK type '{rootCkTypeId}' does not have EnableChangeStreamPreAndPostImages enabled; " +
        $"BeforeFieldFilterCriteria cannot be evaluated. " +
        $"Enable the flag on the collection-root CK type.");
}
```

- [ ] **Step 4: Run the test — it must pass**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/StreamData.UnitTests/StreamData.UnitTests.csproj -c DebugL --filter "FullyQualifiedName~OperationFailedExceptionFactoryTests"
```

Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add \
    src/Runtime.Contracts.MongoDb/OperationFailedException.cs \
    tests/StreamData.UnitTests/OperationFailedExceptionFactoryTests.cs
git -C octo-construction-kit-engine-mongodb commit -m "New: Add PreImageCaptureNotEnabled exception factory

Preparation for the TenantRepository.WatchRtEntitiesAsync guard. Message
names the offending collection-root CK type and points to the fix.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 7: Add the pre-image-capture guard to `TenantRepository.WatchRtEntitiesAsync`

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/TenantRepository.cs`

Coverage for this guard comes from the integration tests in Phase F; no unit test is added here because the method is heavily dependent on live tenant/CK-cache state.

- [ ] **Step 1: Insert the guard**

Edit `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/TenantRepository.cs`. Locate the private `WatchRtEntitiesAsync<TEntity>` at line 773 and insert the guard between the `ckTypeGraph` load and the `Subscription<TEntity>` construction:

```csharp
private async Task<IUpdateStream<TEntity>> WatchRtEntitiesAsync<TEntity>(RtCkId<CkTypeId> ckTypeId,
    WatchStreamFilter watchStreamFilter, CancellationToken cancellationToken = default)
    where TEntity : RtEntity, new()
{
    var ckCacheService = await GetCkCacheServiceAsync();
    var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);

    if (watchStreamFilter.BeforeFieldFilterCriteria != null)
    {
        var rootCkTypeId = ckTypeGraph.DefiningCollectionRootCkTypeId ?? ckTypeGraph.CkTypeId;
        var rootGraph = ckCacheService.GetCkType(TenantId, rootCkTypeId);
        if (!rootGraph.EnableChangeStreamPreAndPostImages)
        {
            throw OperationFailedException.PreImageCaptureNotEnabled(rootCkTypeId);
        }
    }

    var subscription = new Subscription<TEntity>(ckCacheService, TenantId, ckTypeGraph,
        mongoDbRepositoryDataSource);

    // ... remaining body unchanged ...
```

Leave the rest of the method body (`if (watchStreamFilter.BeforeFieldFilterCriteria != null) subscription.AddBeforeFieldFilterCriteria(...)` etc.) exactly as it is.

- [ ] **Step 2: Build the MongoDB repo**

```bash
Invoke-Build -repositoryPath ./octo-construction-kit-engine-mongodb -configuration DebugL
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add src/Runtime.Engine.MongoDb/Repositories/MongoDb/TenantRepository.cs
git -C octo-construction-kit-engine-mongodb commit -m "Fix: Fail fast when BeforeFieldFilterCriteria set but pre-image capture disabled

MongoDB only populates fullDocumentBeforeChange when the collection has
changeStreamPreAndPostImages enabled. Without the fix for Bug 2 this silently
downgraded subscriptions; with the fix it would silently emit zero events.
Walk to the defining collection root via ICkCacheService and throw
OperationFailedException.PreImageCaptureNotEnabled when the flag is off.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase F — Integration tests (Testcontainers MongoDB replica set)

These tests require Docker to be running locally (or `USE_LOCAL_MONGODB=true` against a local replica-set instance — see `octo-construction-kit-engine-mongodb/CLAUDE.md`).

### Task 8: Add test CK types for watch scenarios

**Files:**
- Create: `octo-construction-kit-engine-mongodb/tests/TestCkModel/ConstructionKit/types/watchTarget.yaml`
- Create: `octo-construction-kit-engine-mongodb/tests/TestCkModel/ConstructionKit/types/watchTargetNoPreImage.yaml`

- [ ] **Step 1: Create `watchTarget.yaml` (root with pre-image capture on, plus a derived type)**

```yaml
$schema: "https://schemas.meshmakers.cloud/construction-kit-elements.schema.json"
types:
  - typeId: WatchTarget
    description: Root type used for FromWatchRtEntity change-stream integration tests. Pre-image capture is enabled so fullDocumentBeforeChange filters work.
    derivedFromCkTypeId: ${System}/Entity
    enableChangeStreamPreAndPostImages: true
    attributes:
      - id: ${this}/Name
        name: Name
      - id: ${this}/Status
        name: Status

  - typeId: WatchTargetDerived
    description: Derived from WatchTarget. Shares the collection and therefore inherits pre-image capture.
    derivedFromCkTypeId: ${this}/WatchTarget
    attributes:
      - id: ${this}/Extra
        name: Extra
```

- [ ] **Step 2: Create `watchTargetNoPreImage.yaml` (root without pre-image capture)**

```yaml
$schema: "https://schemas.meshmakers.cloud/construction-kit-elements.schema.json"
types:
  - typeId: WatchTargetNoPreImage
    description: Root type without pre-image capture. Used to verify that BeforeFieldFilterCriteria is rejected with OperationFailedException.PreImageCaptureNotEnabled.
    derivedFromCkTypeId: ${System}/Entity
    attributes:
      - id: ${this}/Name
        name: Name
```

- [ ] **Step 3: Build the TestCkModel project**

```bash
dotnet build octo-construction-kit-engine-mongodb/tests/TestCkModel/TestCkModel.csproj -c DebugL
```

Expected: compilation succeeds and the CK compiler accepts the new types (the post-build step compiles the YAML via the CK compiler MSBuild task).

- [ ] **Step 4: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add \
    tests/TestCkModel/ConstructionKit/types/watchTarget.yaml \
    tests/TestCkModel/ConstructionKit/types/watchTargetNoPreImage.yaml
git -C octo-construction-kit-engine-mongodb commit -m "New: Add watch-target test CK types for change-stream tests

WatchTarget has enableChangeStreamPreAndPostImages=true and a derived type
to exercise the defining-collection-root guard logic. WatchTargetNoPreImage
is a separate root without the flag for the negative guard test.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 9: Add `WatchRtEntitiesFilterTests` — baseline and after-only tests

**Files:**
- Create: `octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs`

- [ ] **Step 1: Inspect the fixture patterns used by existing tests**

Read one existing integration test to learn the fixture wire-up. Run:

```bash
cat octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/GetRtEntitiesByTypeAsyncTests.cs | head -60
```

Note the `[Collection("Sequential")]` attribute, the `IClassFixture<TFixture>` pattern, and how the test obtains an `ITenantRepository` from the fixture.

- [ ] **Step 2: Write the test file with a helper and the first two tests**

Create `octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs`:

```csharp
using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public sealed class WatchRtEntitiesFilterTests : IClassFixture<ImportTestCkModelFixture>
{
    private static readonly TimeSpan EventWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NoEventWindow = TimeSpan.FromSeconds(1);

    private readonly ImportTestCkModelFixture _fixture;

    public WatchRtEntitiesFilterTests(ImportTestCkModelFixture fixture)
    {
        _fixture = fixture;
    }

    private static RtCkId<CkTypeId> WatchTargetId =>
        new(new CkModelId("Test", new CkVersion(1, 0, 0)), new CkTypeId("WatchTarget"));

    private static RtCkId<CkTypeId> WatchTargetDerivedId =>
        new(new CkModelId("Test", new CkVersion(1, 0, 0)), new CkTypeId("WatchTargetDerived"));

    private static RtCkId<CkTypeId> WatchTargetNoPreImageId =>
        new(new CkModelId("Test", new CkVersion(1, 0, 0)), new CkTypeId("WatchTargetNoPreImage"));

    private async Task<ITenantRepository> GetRepoAsync() =>
        await _fixture.GetTenantRepositoryAsync();

    private static async Task<(RtEntity? evt, bool gotEvent)> ReceiveOneEventAsync(
        IUpdateStream<RtEntity> stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var e in stream.ReadAllAsync(cts.Token))
            {
                return (e.Entity, true);
            }
        }
        catch (OperationCanceledException) { }
        return (null, false);
    }

    [Fact]
    public async Task Watch_with_no_filters_fires_on_update()
    {
        var repo = await GetRepoAsync();
        var entity = new RtEntity { CkTypeId = WatchTargetId };
        entity.SetAttribute("Name", "alpha");
        await repo.CreateOrUpdateRtEntityAsync(entity);

        var stream = await repo.WatchRtEntitiesAsync(
            WatchTargetId,
            new WatchStreamFilter { UpdateTypes = UpdateTypes.Update });

        // Mutate after subscription is open
        entity.SetAttribute("Name", "beta");
        await repo.CreateOrUpdateRtEntityAsync(entity);

        var (_, gotEvent) = await ReceiveOneEventAsync(stream, EventWaitTimeout);
        gotEvent.Should().BeTrue("an unfiltered watch must fire on every update");
    }

    [Fact]
    public async Task Watch_with_after_filter_fires_when_after_matches()
    {
        var repo = await GetRepoAsync();
        var entity = new RtEntity { CkTypeId = WatchTargetId };
        entity.SetAttribute("Name", "alpha");
        await repo.CreateOrUpdateRtEntityAsync(entity);

        var afterFilter = new FieldFilterCriteria();
        afterFilter.Add("Status", FieldFilterOperator.Equals, "active");

        var stream = await repo.WatchRtEntitiesAsync(
            WatchTargetId,
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                FieldFilterCriteria = afterFilter,
            });

        entity.SetAttribute("Status", "active");
        await repo.CreateOrUpdateRtEntityAsync(entity);

        var (_, gotEvent) = await ReceiveOneEventAsync(stream, EventWaitTimeout);
        gotEvent.Should().BeTrue("update sets Status=active, matching the after-filter");
    }

    [Fact]
    public async Task Watch_with_after_filter_does_not_fire_when_after_does_not_match()
    {
        var repo = await GetRepoAsync();
        var entity = new RtEntity { CkTypeId = WatchTargetId };
        entity.SetAttribute("Name", "alpha");
        await repo.CreateOrUpdateRtEntityAsync(entity);

        var afterFilter = new FieldFilterCriteria();
        afterFilter.Add("Status", FieldFilterOperator.Equals, "active");

        var stream = await repo.WatchRtEntitiesAsync(
            WatchTargetId,
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                FieldFilterCriteria = afterFilter,
            });

        entity.SetAttribute("Status", "inactive");
        await repo.CreateOrUpdateRtEntityAsync(entity);

        var (_, gotEvent) = await ReceiveOneEventAsync(stream, NoEventWindow);
        gotEvent.Should().BeFalse("after-filter must suppress events whose post-image doesn't match");
    }
}
```

Notes:
- `ImportTestCkModelFixture` is the existing fixture used by several tests — confirm it imports the TestCkModel that now includes WatchTarget. If it doesn't expose `GetTenantRepositoryAsync`, use whichever accessor the other integration tests use (`_fixture.TenantRepository`, `_fixture.GetSystemContext().GetTenantRepositoryAsync(...)`, etc.) — copy the pattern from `GetRtEntitiesByTypeAsyncTests.cs`.
- `FieldFilterCriteria.Add` / `FieldFilterOperator` names are what the rest of the codebase uses; if the API has different method names in the current checkout, adjust to match (do not invent new ones).
- `IUpdateStream<RtEntity>.ReadAllAsync(cancellationToken)` is the expected consumer pattern. If the actual interface differs (e.g. returns an `IAsyncEnumerable<ChangeEvent<RtEntity>>`), adjust the helper and the assertion shape accordingly.

- [ ] **Step 3: Run the three tests**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/Runtime.Engine.MongoDb.IntegrationTests.csproj -c DebugL --filter "FullyQualifiedName~WatchRtEntitiesFilterTests"
```

Expected: all 3 pass. First run may take 30–60s while Testcontainers pulls the MongoDB image.

- [ ] **Step 4: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs
git -C octo-construction-kit-engine-mongodb commit -m "New: Add baseline and after-filter integration tests for WatchRtEntities

Covers the unfiltered happy-path and the post-image-only field filter.
Foundation for the Bug 1 / Bug 2 / guard regression tests.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 10: Add before-filter integration tests (Bug 1 regression)

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs`

- [ ] **Step 1: Append before-filter tests to the test class**

Add these methods inside `WatchRtEntitiesFilterTests` (after the after-filter tests):

```csharp
[Fact]
public async Task Watch_with_before_filter_fires_when_before_matches()
{
    // Regression for Bug 1: before the fix, Inject applied 'fullDocument.' even
    // for the before-filter, so this test would have matched against the post-image.
    var repo = await GetRepoAsync();
    var entity = new RtEntity { CkTypeId = WatchTargetId };
    entity.SetAttribute("Status", "pending");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var beforeFilter = new FieldFilterCriteria();
    beforeFilter.Add("Status", FieldFilterOperator.Equals, "pending");

    var stream = await repo.WatchRtEntitiesAsync(
        WatchTargetId,
        new WatchStreamFilter
        {
            UpdateTypes = UpdateTypes.Update,
            BeforeFieldFilterCriteria = beforeFilter,
        });

    entity.SetAttribute("Status", "active");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var (_, gotEvent) = await ReceiveOneEventAsync(stream, EventWaitTimeout);
    gotEvent.Should().BeTrue(
        "pre-image Status=pending matches the before-filter regardless of post-image value");
}

[Fact]
public async Task Watch_with_before_filter_does_not_fire_when_before_does_not_match()
{
    var repo = await GetRepoAsync();
    var entity = new RtEntity { CkTypeId = WatchTargetId };
    entity.SetAttribute("Status", "active");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var beforeFilter = new FieldFilterCriteria();
    beforeFilter.Add("Status", FieldFilterOperator.Equals, "pending");

    var stream = await repo.WatchRtEntitiesAsync(
        WatchTargetId,
        new WatchStreamFilter
        {
            UpdateTypes = UpdateTypes.Update,
            BeforeFieldFilterCriteria = beforeFilter,
        });

    entity.SetAttribute("Status", "done");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var (_, gotEvent) = await ReceiveOneEventAsync(stream, NoEventWindow);
    gotEvent.Should().BeFalse("pre-image Status=active does not match before-filter Status=pending");
}
```

- [ ] **Step 2: Run the before-filter tests**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/Runtime.Engine.MongoDb.IntegrationTests.csproj -c DebugL --filter "FullyQualifiedName~WatchRtEntitiesFilterTests.Watch_with_before"
```

Expected: both pass.

- [ ] **Step 3: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs
git -C octo-construction-kit-engine-mongodb commit -m "New: Add pre-image-filter integration tests (Bug 1 regression)

Positive and negative cases for BeforeFieldFilterCriteria — proves the filter
now targets fullDocumentBeforeChange rather than leaking to the post-image.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 11: Add combined before+after filter integration tests (Bug 2 regression)

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs`

- [ ] **Step 1: Append combined-filter tests**

```csharp
[Fact]
public async Task Watch_with_before_and_after_filters_fires_when_both_match()
{
    // Regression for Bug 2: before the fix, the two filters were OR'd so this
    // also fired when only one side matched.
    var repo = await GetRepoAsync();
    var entity = new RtEntity { CkTypeId = WatchTargetId };
    entity.SetAttribute("Status", "pending");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var beforeFilter = new FieldFilterCriteria();
    beforeFilter.Add("Status", FieldFilterOperator.Equals, "pending");
    var afterFilter = new FieldFilterCriteria();
    afterFilter.Add("Status", FieldFilterOperator.Equals, "active");

    var stream = await repo.WatchRtEntitiesAsync(
        WatchTargetId,
        new WatchStreamFilter
        {
            UpdateTypes = UpdateTypes.Update,
            BeforeFieldFilterCriteria = beforeFilter,
            FieldFilterCriteria = afterFilter,
        });

    entity.SetAttribute("Status", "active");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var (_, gotEvent) = await ReceiveOneEventAsync(stream, EventWaitTimeout);
    gotEvent.Should().BeTrue("both pre- and post-image filters match");
}

[Fact]
public async Task Watch_with_before_and_after_filters_does_not_fire_when_only_after_matches()
{
    var repo = await GetRepoAsync();
    var entity = new RtEntity { CkTypeId = WatchTargetId };
    entity.SetAttribute("Status", "done");  // pre-image does NOT match before-filter
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var beforeFilter = new FieldFilterCriteria();
    beforeFilter.Add("Status", FieldFilterOperator.Equals, "pending");
    var afterFilter = new FieldFilterCriteria();
    afterFilter.Add("Status", FieldFilterOperator.Equals, "active");

    var stream = await repo.WatchRtEntitiesAsync(
        WatchTargetId,
        new WatchStreamFilter
        {
            UpdateTypes = UpdateTypes.Update,
            BeforeFieldFilterCriteria = beforeFilter,
            FieldFilterCriteria = afterFilter,
        });

    entity.SetAttribute("Status", "active");  // post-image matches
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var (_, gotEvent) = await ReceiveOneEventAsync(stream, NoEventWindow);
    gotEvent.Should().BeFalse(
        "only post-image matches; with AND composition the event must be suppressed");
}

[Fact]
public async Task Watch_with_before_and_after_filters_does_not_fire_when_only_before_matches()
{
    var repo = await GetRepoAsync();
    var entity = new RtEntity { CkTypeId = WatchTargetId };
    entity.SetAttribute("Status", "pending");  // pre-image matches
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var beforeFilter = new FieldFilterCriteria();
    beforeFilter.Add("Status", FieldFilterOperator.Equals, "pending");
    var afterFilter = new FieldFilterCriteria();
    afterFilter.Add("Status", FieldFilterOperator.Equals, "active");

    var stream = await repo.WatchRtEntitiesAsync(
        WatchTargetId,
        new WatchStreamFilter
        {
            UpdateTypes = UpdateTypes.Update,
            BeforeFieldFilterCriteria = beforeFilter,
            FieldFilterCriteria = afterFilter,
        });

    entity.SetAttribute("Status", "done");  // post-image does NOT match after-filter
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var (_, gotEvent) = await ReceiveOneEventAsync(stream, NoEventWindow);
    gotEvent.Should().BeFalse(
        "only pre-image matches; with AND composition the event must be suppressed");
}
```

- [ ] **Step 2: Run the combined-filter tests**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/Runtime.Engine.MongoDb.IntegrationTests.csproj -c DebugL --filter "FullyQualifiedName~Watch_with_before_and_after"
```

Expected: all 3 pass.

- [ ] **Step 3: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs
git -C octo-construction-kit-engine-mongodb commit -m "New: Add combined pre/post-image filter integration tests (Bug 2 regression)

Asserts AND composition: both filters must match for events to pass.
Covers the three cases — both match, only post matches, only pre matches.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 12: Add guard integration tests

**Files:**
- Modify: `octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs`

- [ ] **Step 1: Append guard tests**

```csharp
[Fact]
public async Task Watch_throws_when_BeforeFieldFilterCriteria_set_and_pre_image_capture_disabled()
{
    var repo = await GetRepoAsync();

    var beforeFilter = new FieldFilterCriteria();
    beforeFilter.Add("Name", FieldFilterOperator.Equals, "x");

    Func<Task> act = () => repo.WatchRtEntitiesAsync(
        WatchTargetNoPreImageId,
        new WatchStreamFilter
        {
            UpdateTypes = UpdateTypes.Update,
            BeforeFieldFilterCriteria = beforeFilter,
        });

    await act.Should().ThrowAsync<OperationFailedException>()
        .Where(e => e.Message.Contains("WatchTargetNoPreImage")
                    && e.Message.Contains("EnableChangeStreamPreAndPostImages"));
}

[Fact]
public async Task Watch_with_only_AfterFieldFilter_works_on_type_without_pre_image_capture()
{
    // Guard must only fire when BeforeFieldFilterCriteria is set.
    var repo = await GetRepoAsync();
    var entity = new RtEntity { CkTypeId = WatchTargetNoPreImageId };
    entity.SetAttribute("Name", "alpha");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var afterFilter = new FieldFilterCriteria();
    afterFilter.Add("Name", FieldFilterOperator.Equals, "beta");

    var stream = await repo.WatchRtEntitiesAsync(
        WatchTargetNoPreImageId,
        new WatchStreamFilter
        {
            UpdateTypes = UpdateTypes.Update,
            FieldFilterCriteria = afterFilter,
        });

    entity.SetAttribute("Name", "beta");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var (_, gotEvent) = await ReceiveOneEventAsync(stream, EventWaitTimeout);
    gotEvent.Should().BeTrue(
        "guard must not fire when only an after-filter is configured");
}

[Fact]
public async Task Watch_on_derived_type_uses_collection_root_flag_for_guard()
{
    // WatchTargetDerived inherits from WatchTarget (root has the flag set).
    // Subscription with before-filter must be accepted and fire.
    var repo = await GetRepoAsync();
    var entity = new RtEntity { CkTypeId = WatchTargetDerivedId };
    entity.SetAttribute("Status", "pending");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var beforeFilter = new FieldFilterCriteria();
    beforeFilter.Add("Status", FieldFilterOperator.Equals, "pending");

    var stream = await repo.WatchRtEntitiesAsync(
        WatchTargetDerivedId,
        new WatchStreamFilter
        {
            UpdateTypes = UpdateTypes.Update,
            BeforeFieldFilterCriteria = beforeFilter,
        });

    entity.SetAttribute("Status", "active");
    await repo.CreateOrUpdateRtEntityAsync(entity);

    var (_, gotEvent) = await ReceiveOneEventAsync(stream, EventWaitTimeout);
    gotEvent.Should().BeTrue(
        "guard walks to the collection root WatchTarget which has the flag set");
}
```

- [ ] **Step 2: Run the guard tests**

```bash
dotnet test octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/Runtime.Engine.MongoDb.IntegrationTests.csproj -c DebugL --filter "FullyQualifiedName~Watch_throws_when_BeforeFieldFilterCriteria|FullyQualifiedName~Watch_with_only_AfterFieldFilter|FullyQualifiedName~Watch_on_derived_type_uses_collection_root"
```

Expected: all 3 pass.

- [ ] **Step 3: Commit**

```bash
git -C octo-construction-kit-engine-mongodb add tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs
git -C octo-construction-kit-engine-mongodb commit -m "New: Add pre-image-capture guard integration tests

Covers the three guard scenarios: fail-fast when BeforeFieldFilterCriteria is
set on a root without pre-image capture, guard no-op when only an after-filter
is set, and guard walks to the defining collection root on derived types.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase G — Full verification

### Task 13: Run the full build and test suite across both repos

- [ ] **Step 1: Full build in dependency order**

```bash
Invoke-BuildAll -configuration DebugL
```

Expected: both repos build cleanly. No warnings treated as errors.

- [ ] **Step 2: Run all CK engine tests (excluding SystemTests)**

```bash
dotnet test octo-construction-kit-engine -c DebugL --filter "FullyQualifiedName!~SystemTests"
```

Expected: no failures.

- [ ] **Step 3: Run all MongoDB repo tests (excluding SystemTests)**

```bash
dotnet test octo-construction-kit-engine-mongodb -c DebugL --filter "FullyQualifiedName!~SystemTests"
```

Expected: no failures. Watch tests take longer because they wait on change-stream events; the whole integration suite may take 2–5 minutes.

- [ ] **Step 4: Review the commit series**

```bash
git -C octo-construction-kit-engine log --oneline origin/main..HEAD
git -C octo-construction-kit-engine-mongodb log --oneline origin/main..HEAD
```

Expected:

- `octo-construction-kit-engine`: one commit (`New: Surface EnableChangeStreamPreAndPostImages on CkTypeGraph`).
- `octo-construction-kit-engine-mongodb`: nine commits (Constants → Bug 1 → ComposeWatchFilter helper → WatchAsync refactor → exception factory → guard → test CK types → 4 integration test commits).

---

## Cross-cutting reminders

- **Never chain bash commands with `&&`, `||`, `;`** — the user's memory insists on separate Bash tool calls (see CLAUDE.md).
- **Never `cd` into sub-repos** — always `git -C <repo-folder>`.
- **Commit messages** follow `<Fix|New>: <description>` with the Co-Authored-By trailer. If an Azure DevOps work item is referenced, prefix with `AB#<number>`.
- **Pre-image capture is a collection-level MongoDB setting.** Flipping the YAML flag after a collection is already created requires re-running `MongoDbRepositoryDataSource.CreateCollectionIfNotExistsAsync` logic; Testcontainers avoids this problem because each test run starts from a fresh replica set.
- **If the integration tests are flaky** (change-stream events are asynchronous), bump `EventWaitTimeout` — do not widen the `NoEventWindow` negative-assertion window (that makes no-event assertions weaker).

---

## Self-review

**Spec coverage:**

- Bug 1 fix (Inject honours fieldName) → Task 3 (unit tests) + Tasks 10, 11, 12 (integration).
- Bug 2 fix (And not Or) → Tasks 4 (helper + unit tests), 5 (wire-up), 11 (integration).
- Constants for `"fullDocument"` / `"fullDocumentBeforeChange"` → Task 2, consumed in Task 3.
- `CkTypeGraph.EnableChangeStreamPreAndPostImages` → Task 1.
- `OperationFailedException.PreImageCaptureNotEnabled` → Task 6.
- Guard in `TenantRepository.WatchRtEntitiesAsync` → Task 7, covered by Task 12.
- Test CK types `WatchTarget` / `WatchTargetDerived` / `WatchTargetNoPreImage` → Task 8.
- Derived-type guard behaviour → Task 12's third test.
- Full build/test verification → Task 13.

**Placeholder scan:** No "TBD", "implement later", or undefined references. Every code step includes full code. File paths, class names, method names, and property names are consistent across tasks (e.g., `EventWaitTimeout`, `NoEventWindow`, `ReceiveOneEventAsync`, `WatchTargetId` — all declared once and reused).

**Type consistency:** `ComposeWatchFilter<TDocument>` signature in Task 4 matches its call site in Task 5 (`BuildExtensions.ComposeWatchFilter(documentFilter, documentBeforeFilter)` — both `FilterDefinition<ChangeStreamDocument<TDocument>>?`). `Inject`'s signature (`string fieldName, FilterDefinition<TInnerDocument> filter`) is unchanged — only its body. `PreImageCaptureNotEnabled(CkId<CkTypeId>)` matches its call in Task 7.

**Scope:** Single cohesive feature touching two repos — appropriate for one plan; not decomposable into independent sub-projects without awkward cross-repo coupling.
