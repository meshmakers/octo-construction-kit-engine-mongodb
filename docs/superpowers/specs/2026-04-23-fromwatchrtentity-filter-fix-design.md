# FromWatchRtEntity@1 change-stream filter fix

## Problem

`FromWatchRtEntity@1` trigger subscriptions ignore their configured `fieldFilters` and `beforeFieldFilters`. The trigger fires on every `Update` of the configured CK type regardless of pre- or post-image state. Two compounding defects in `octo-construction-kit-engine-mongodb`:

**Bug 1 — `Inject` ignores its `fieldName` parameter.** `BuildExtensions.Inject<TDocument, TInnerDocument>(this FilterDefinitionBuilder<TDocument>, string fieldName, FilterDefinition<TInnerDocument>)` takes a `fieldName` argument but never uses it — the body hardcodes `"fullDocument."` when calling `PrefixFieldNames`. The two call sites in `Subscription.cs` pass `"fullDocument"` and `"fullDocumentBeforeChange"`, but both end up prefixed with `"fullDocument."`, so the before-filter evaluates against the post-image.

**Bug 2 — pre/post filters OR'd instead of AND'd.** In `MongoDbDataSourceCollection.WatchAsync`, when both `documentFilter` and `documentBeforeFilter` are non-null, they are combined with `Filter.Or(...)`. Pre- and post-image filters are independent constraints that must both hold; `Or` lets any event through. Expected: `And`.

**Silent failure when pre-image capture is disabled.** MongoDB only populates `fullDocumentBeforeChange` when the collection has `changeStreamPreAndPostImages: { enabled: true }` — driven by `CkTypeDto.EnableChangeStreamPreAndPostImages` on the collection-root CK type. After Bug 2 is fixed, a subscription with `BeforeFieldFilterCriteria` against a root without pre-image capture enabled would silently emit zero events. `CkTypeGraph` does not currently surface this flag, so `TenantRepository.WatchRtEntitiesAsync` cannot guard against this configuration.

## Goals

1. `fieldFilters` and `beforeFieldFilters` on `FromWatchRtEntity@1` work: both must match for an event to fire, and each filter evaluates against the correct image.
2. Subscriptions with `BeforeFieldFilterCriteria` against a collection root without pre-image capture fail fast with an actionable exception instead of emitting zero events.
3. The `"fullDocument"` and `"fullDocumentBeforeChange"` strings become named constants instead of scattered magic strings.
4. Regression coverage via unit tests and a full integration suite.

## Non-goals

- Changing the `FromWatchRtEntity@1` pipeline node API or YAML schema.
- Refactoring the broader watch/subscription surface beyond what serves the fix.
- Enabling pre-image capture automatically. Enabling the flag remains an explicit modelling decision on the CK type.

## Architecture

Two repos change in strict dependency order:

1. **octo-construction-kit-engine** — surface `EnableChangeStreamPreAndPostImages` on `CkTypeGraph` so downstream layers can read the flag off the graph without going back to the raw DTO.
2. **octo-construction-kit-engine-mongodb** — three fixes (Bug 1, Bug 2, constants), the pre-image guard, and the test suites.

The MongoDB repo depends on the CK engine bump, so the build order is CK engine → pack → `../nuget` → MongoDB. `Invoke-BuildAll -configuration DebugL` handles this automatically.

## Components

### octo-construction-kit-engine

**`CkTypeGraph` (`src/ConstructionKit.Contracts/DependencyGraph/CkTypeGraph.cs`)**

- Add `public bool EnableChangeStreamPreAndPostImages { get; }` (get-only, matches the surrounding `IsAbstract` / `IsFinal` / `IsStreamType` / `IsCollectionRoot` flags).
- Set it from `ckTypeDto.EnableChangeStreamPreAndPostImages` in the `CkCompiledTypeDto` constructor.
- Accept it as a new parameter in the `[JsonConstructor]` (appended to the tail of the parameter list) and set it there so existing callers that pass positional arguments break at compile time and are surfaced in code review.
- `CkModelGraph.GetOrCreateType` requires no change; it passes the DTO through to the constructor.

**`CkCompiledTypeDto` → `CkTypeGraph` mapping (`src/ConstructionKit.Engine/Services/CompilerService.cs`)**

No change. `EnableChangeStreamPreAndPostImages` is already copied from the YAML-sourced `CkTypeDto` into `CkCompiledTypeDto` at line 267.

**Serialization round-trip**

The `[JsonConstructor]` is used when `ICkCacheService` restores a cache from JSON. Round-trip tests must confirm the new flag survives save/restore.

### octo-construction-kit-engine-mongodb

**`Runtime.Engine.MongoDb.Constants` (`src/Runtime.Engine.MongoDb/Constants.cs`)**

Add two public consts:

```csharp
public const string ChangeStreamFullDocument = "fullDocument";
public const string ChangeStreamFullDocumentBeforeChange = "fullDocumentBeforeChange";
```

**`BuildExtensions.Inject` (`src/Runtime.Engine.MongoDb/Repositories/MongoDb/Generic/BuildExtensions.cs`)**

Fix Bug 1. Validate `fieldName` and use it to build the prefix:

```csharp
ArgumentValidation.ValidateString(nameof(fieldName), fieldName);
var prefix = fieldName + ".";
var prefixedFilter = PrefixFieldNames(renderedFilter, prefix);
```

`ArgumentValidation.ValidateString` is the repo's existing guard (used throughout `MongoDbDataSourceCollection`) and rejects null/empty/whitespace. Fix the stale XML doc (currently references `ChangeStreamDocument<TEntity>` and claims the prefix is always `"fullDocument."`).

`PrefixFieldNames` itself is unchanged — it correctly prefixes non-operator field names and recurses into operator arrays/documents.

**`Subscription.cs` (`src/Runtime.Engine.MongoDb/Repositories/Query/Subscription.cs`)**

Replace the two magic strings at lines 39 and 42 with the new constants:

```csharp
Builders<ChangeStreamDocument<TEntity>>.Filter.Inject(
    Constants.ChangeStreamFullDocument, filterDefinitions)
// ...
Builders<ChangeStreamDocument<TEntity>>.Filter.Inject(
    Constants.ChangeStreamFullDocumentBeforeChange, beforeFilterDefinitions)
```

**`BuildExtensions.ComposeWatchFilter` (new internal static helper in `src/Runtime.Engine.MongoDb/Repositories/MongoDb/Generic/BuildExtensions.cs`)**

Extract the watch-filter composition logic so the And-vs-Or behaviour is directly unit-testable:

```csharp
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

**`MongoDbDataSourceCollection.WatchAsync` (`src/Runtime.Engine.MongoDb/Repositories/MongoDb/MongoDbDataSourceCollection.cs`)**

Fix Bug 2 by replacing the three-way `if/else if/else if` branch with a single call to `ComposeWatchFilter`:

```csharp
var combined = BuildExtensions.ComposeWatchFilter(documentFilter, documentBeforeFilter);
if (combined != null)
{
    pipeline = pipeline.Match(combined);
}
```

Removing the branches also removes the structural temptation that hid Bug 2.

**`TenantRepository.WatchRtEntitiesAsync` pre-image guard (`src/Runtime.Engine.MongoDb/Repositories/MongoDb/TenantRepository.cs:773`)**

Insert the guard after `ckTypeGraph` is loaded and before the `Subscription<TEntity>` is constructed, only when `watchStreamFilter.BeforeFieldFilterCriteria != null`:

1. Resolve the defining collection root id: `ckTypeGraph.DefiningCollectionRootCkTypeId ?? ckTypeGraph.CkTypeId` (a root type resolves to itself).
2. Look up the root's graph via `ckCacheService.GetCkType(TenantId, rootCkTypeId)`.
3. If `root.EnableChangeStreamPreAndPostImages == false`, throw `OperationFailedException.PreImageCaptureNotEnabled(rootCkTypeId)`.

`CkTypeGraph` itself stays a passive data projection; the defining-root walk lives in the caller so `CkTypeGraph` doesn't need a handle to `ICkCacheService`.

**`OperationFailedException` factory (`src/Runtime.Contracts.MongoDb/OperationFailedException.cs`)**

```csharp
public static Exception PreImageCaptureNotEnabled(CkId<CkTypeId> rootCkTypeId)
{
    return new OperationFailedException(
        $"CK type '{rootCkTypeId}' does not have EnableChangeStreamPreAndPostImages enabled; " +
        $"BeforeFieldFilterCriteria cannot be evaluated. Enable the flag on the collection-root CK type.");
}
```

## Data flow (post-fix)

A subscription with both pre- and post-image filters flows like this:

1. Caller → `TenantRepository.WatchRtEntitiesAsync(ckTypeId, watchStreamFilter)`.
2. Guard: when `BeforeFieldFilterCriteria` is set, walk to the defining collection root via `ICkCacheService`; throw `OperationFailedException.PreImageCaptureNotEnabled` if the root does not have pre-image capture enabled. No silent zero-event subscription.
3. `Subscription<TEntity>` is built. The two `FieldFilterResolver`s each produce a `FilterDefinition<TEntity>`.
4. `Subscription.WatchRtEntitiesAsync` calls `Builders<ChangeStreamDocument<TEntity>>.Filter.Inject(Constants.ChangeStreamFullDocument, afterFilter)` and `Inject(Constants.ChangeStreamFullDocumentBeforeChange, beforeFilter)`.
5. `Inject` now prefixes field names with the actual `fieldName + "."` — the before-filter targets `fullDocumentBeforeChange.<path>` and the after-filter targets `fullDocument.<path>`.
6. `MongoDbDataSourceCollection.WatchAsync` composes both filters with `Filter.And(...)` — both constraints must hold for an event to pass.

## Error handling

- Guard failure throws `OperationFailedException.PreImageCaptureNotEnabled` before any MongoDB round-trip. The exception message names the root CK type so the caller can fix the model.
- `Inject` and the new composition path do not introduce new failure modes; rendering errors continue to surface as MongoDB driver exceptions.
- The guard only fires when `BeforeFieldFilterCriteria` is set. Subscriptions with only an after-filter continue to work on CK types without pre-image capture.

## Test strategy

Unit tests + full integration suite.

### Unit tests — `octo-construction-kit-engine`

**`CkTypeGraphTests.cs` (new, under `tests/ConstructionKit.Contracts.Tests/DependencyGraph/`)**

- `EnableChangeStreamPreAndPostImages_is_false_by_default`
- `EnableChangeStreamPreAndPostImages_is_propagated_from_dto_constructor`
- `EnableChangeStreamPreAndPostImages_round_trips_through_json_constructor` — covers both positive and default-false round-trip via the `[JsonConstructor]`.

Round-trip through `ICkCacheService.SaveCacheAsync` / `RestoreCacheAsync` is covered by the integration test layer (a `CkCacheInvalidationAfterImportTests`-style scenario) rather than duplicated here.

### Unit tests — `octo-construction-kit-engine-mongodb`

Location: `tests/StreamData.UnitTests/` (existing project for pure MongoDB helpers).

**`BuildExtensionsInjectTests.cs`**

Render filters via `FilterDefinition<T>.Render` and assert on the resulting `BsonDocument`:

- `Inject_prefixes_simple_equality_filter_with_fullDocument`
- `Inject_prefixes_simple_equality_filter_with_fullDocumentBeforeChange` — regression for Bug 1: asserts that `fullDocumentBeforeChange` is actually used, not `fullDocument`.
- `Inject_prefixes_all_fields_in_And_filter`
- `Inject_prefixes_all_fields_in_Or_filter`
- `Inject_prefixes_fields_in_nested_operators` (e.g., `$and` containing `$or`)
- `Inject_preserves_operator_names_starting_with_dollar`
- `Inject_handles_In_and_Nin_operators` (arrays)
- `Inject_handles_nested_document_values`
- `Inject_handles_empty_filter`
- `Inject_throws_ArgumentException_when_fieldName_is_null_or_whitespace` — `Inject` validates `fieldName` with `ArgumentValidation.ValidateString` (matching the repo's existing guard pattern).

**`WatchFilterCompositionTests.cs`**

Extract the watch-filter composition logic into an internal static helper so the And-vs-Or behaviour is directly testable without a MongoDB client:

```csharp
internal static FilterDefinition<ChangeStreamDocument<TDocument>>? ComposeWatchFilter<TDocument>(
    FilterDefinition<ChangeStreamDocument<TDocument>>? afterFilter,
    FilterDefinition<ChangeStreamDocument<TDocument>>? beforeFilter);
```

Tests:

- `Compose_returns_null_when_both_filters_null`
- `Compose_returns_after_when_only_after_filter_set`
- `Compose_returns_before_when_only_before_filter_set`
- `Compose_returns_And_when_both_filters_set` — regression for Bug 2: render the result and assert the top-level operator is `$and`, not `$or`.
- `Compose_And_preserves_both_branches` — asserts the rendered `$and` array contains both filter documents intact.

`MongoDbDataSourceCollection.WatchAsync` is refactored to call this helper, so the three-way branch disappears from the public method.

**`ConstantsChangeStreamFieldsTests.cs`**

Small pin:

- `ChangeStreamFullDocument_has_expected_value` (`"fullDocument"`)
- `ChangeStreamFullDocumentBeforeChange_has_expected_value` (`"fullDocumentBeforeChange"`)

Guards against accidental renames, since the strings must match MongoDB's wire protocol exactly.

### Integration tests — `octo-construction-kit-engine-mongodb`

Location: `tests/Runtime.Engine.MongoDb.IntegrationTests/WatchRtEntitiesFilterTests.cs` (new).

Existing fixtures are reused:

- `[Collection("Sequential")]` collection attribute.
- `ImportTestCkModelFixture` (or a derived `WatchRtEntitiesFixture` if the test CK model needs changes) sets up the system tenant and imports a test CK model via Testcontainers MongoDB with replica set.
- Testcontainer replica set already satisfies change-stream prerequisites.

Two dedicated CK types are added to `TestCkModel/ConstructionKit/types/` to isolate the watch scenarios from existing tests:

- `watchTarget.yaml` — collection root with `EnableChangeStreamPreAndPostImages: true`; has a couple of simple string attributes (e.g., `Name`, `Status`) to drive field-filter assertions.
- `watchTargetNoPreImage.yaml` — collection root with the flag absent/false; used for the guard negative test.

No existing test CK types are mutated, so current tests are not affected.

Tests:

- `Watch_with_no_filters_fires_on_every_update` — baseline.
- `Watch_with_after_filter_only_fires_when_after_matches`
- `Watch_with_after_filter_does_not_fire_when_after_does_not_match`
- `Watch_with_before_filter_only_fires_when_before_matches` — regression for Bug 1 wired end-to-end through MongoDB.
- `Watch_with_before_and_after_filters_fires_when_both_match` — regression for Bug 2: update must pass both pre- and post-filters for the event to surface.
- `Watch_with_before_and_after_filters_does_not_fire_when_only_after_matches`
- `Watch_with_before_and_after_filters_does_not_fire_when_only_before_matches`
- `Watch_throws_when_BeforeFieldFilterCriteria_set_and_pre_image_capture_disabled` — asserts `OperationFailedException.PreImageCaptureNotEnabled`.
- `Watch_with_only_AfterFieldFilter_works_on_type_without_pre_image_capture` — negative guard check: guard must not fire when `BeforeFieldFilterCriteria == null`.
- `Watch_on_derived_type_uses_collection_root_flag_for_guard` — using the existing `TestCkModel` hierarchy (e.g., `StateOrProvince` derives from `AdministrativeArea`), with the flag set on the root, verify the guard accepts the subscription on the derived type. Mirror negative test uses a derived type whose root lacks the flag.

Each test subscribes, inserts/updates an entity, and either awaits an event with a small timeout (e.g., 2 seconds) or explicitly asserts no event arrives within the timeout window.

### Build / verify

- `Invoke-BuildAll -configuration DebugL` (order handled automatically).
- `dotnet test -c DebugL` in both repos.
- Explicitly verify that existing JSON-snapshot assertions on `CkTypeGraph` (if any — confirmed during implementation) are updated for the new field.

## Risks and trade-offs

- **Adding a flag to `CkTypeGraph` is a binary-compat change** for the `[JsonConstructor]`. Appending at the end of the parameter list limits the blast radius; any caller that passes positional arguments will break at compile time rather than silently drop the flag. Named-argument callers are unaffected.
- **Existing subscriptions that depended on the buggy `Or` behavior** would regress after Bug 2 is fixed. Acceptable: Or-behavior is never what a user configuring both filters intended.
- **New CK types in `TestCkModel`** (`watchTarget`, `watchTargetNoPreImage`) keep the change isolated from existing fixtures. Existing tests that iterate all CK types should be re-run to confirm no unexpected couplings.
- **Change-stream timing in integration tests is inherently async.** Tests use bounded timeouts (2s default) and an explicit "no event" negative assertion for the blocked-update cases; flakiness risk is accepted and mitigated by the replica-set Testcontainer already in use.
