---
description: "Runtime write behaviour: GridFS reads, write-concern failures, transactions, re-import, RecordArray update, rtVersion, time limits"
applies_to: src/Runtime.Engine.MongoDb/Repositories/**, src/Runtime.Contracts.MongoDb/Repositories/**, src/Runtime.Engine.MongoDb/RuntimeQueries/**
---

## GridFS read preference and read-your-writes

`MongoRepository.GetGridFsBucket()` reads with `SecondaryPreferred`, writes with `w:majority`. A secondary can lag behind
a write that was acknowledged by a majority, so a consumer that reads a file right after another process wrote it (the
asset repository caches a CK model, `octo-bot-services` imports it in the same second) may get
`GridFSFileNotFoundException`. `GetPrimaryGridFsBucket()` (`IRepositoryInternal`) reads from the primary.
`MongoLinkedBinaryDataSource` asks the primary **only on a miss**: `DownloadBinaryAsync` (by id) and
`GetTemporaryBinaryAsync` (cache file by name). Regular reads keep using secondaries; only a miss costs a primary read.
A file that is missing on the primary too still throws `EntityNotFoundException` (`IDownloadStreamHandler`).
`GetGridFsBucket()` itself is unchanged (the orphan sweeper and the other binary operations keep using it).
Tests: `MongoLinkedBinaryDataSourceReadYourWritesTests` (unit), `TemporaryBinaryTests.Cache_UploadThenImmediateDownload_FindsTheFile`
(integration, replica set).

## Write-concern-only failures keep their cause

`MongoDbDataSourceCollection.CreateWriteException` maps a `MongoWriteException` of a single-document write. When only the write
concern failed (the `wtimeout` of the hard-coded `w:majority`, see `MongoRepositoryClient`; any other write-concern error),
`MongoWriteException.WriteError` is `null` and `WriteConcernError` is set: the write is applied on the primary but not confirmed by
a majority. The mapping used to dereference the missing write error and threw a `NullReferenceException` from inside the `catch`,
so the cause was lost. It now returns `OperationFailedException.DatabaseOperationFailed` with the driver exception as the inner
exception (duplicate key and other write errors unchanged), and the CK method host of `octo-common-services` (`CkMethodWriteOutcome`)
answers such a call `METHOD_OUTCOME_UNKNOWN` instead of `METHOD_HANDLER_FAILED` (rule R3). Bulk writes (`MongoBulkWriteException`,
wrapped by `OperationFailedException.BulkImportError`) and commands/commits (`MongoWriteConcernException`, not wrapped) kept their
cause already. Test: `StreamData.UnitTests/MongoDbWriteExceptionMappingTests`.

## A failed commit is never masked by an abort

`OctoSession.CommitTransactionAsync` sets `_commitAttempted` before it calls the driver. When the driver commit throws (a
`w:majority` wtimeout under load, a write concern error, a cancelled token), the session stops counting as active, the
rollback side effects are kept (outcome unknown) and the **original exception is rethrown unchanged** (type,
message, error labels such as `UnknownTransactionCommitResult`). After a commit attempt `AbortTransactionAsync` returns
without calling the driver (Debug log) and `Dispose` does not abort either: the driver refuses an abort after a commit
("Cannot call AbortTransaction after calling CommitTransaction"), and in the usual caller block
`try { ...; Commit } catch { Abort; throw; }` (e.g. `TenantContext.EnableStreamDataAsync`) that `InvalidOperationException`
used to replace the real commit error. Callers therefore need no guard of their own. `IsTransactionActive` is `false` after a
failed commit; a retry starts a new session (`DatabaseCkModelRepository.RunInTransactionWithRetryAsync` does). Tests:
`StreamData.UnitTests/OctoSessionTransactionCallbackTests` (`Commit_ThatThrows_*`, `Abort_AfterASuccessfulCommit_*`).

## Re-importing an archived entity

The default delete (GraphQL `deleteEntities`, `DeleteOptions` default) is `DeleteStrategies.Archive`: the document
stays in the collection with its `_id` (= rtId), `rtState = Archived`, and its associations are archived too. Only
`DeleteStrategies.Erase` removes it. What that means for a later write of the same data:

| Write | Archived document with the same rtId | Archived document holding a unique CK-index value (other rtId) |
|---|---|---|
| `InsertOnly` import (`octo-cli ImportRt`, default) | **replaced** by the incoming entity, active again (`BulkImportAsync`: `ReplaceOne` with filter `_id` AND `rtState = Archived`, upsert). An **active** document with that rtId still fails, with a message naming the state. | `UniqueNotDeleted`: value is free. Plain `Unique`: still blocked. |
| `Upsert` import (`-w`, blueprint apply/update) | replaced, active again (unchanged) | same as left |
| after `Erase` | nothing there, plain insert | nothing there |

Rules:

- The replace is one atomic bulk statement (no read-then-write race). It applies to `RtEntity` documents only;
  associations get fresh ids and are inserted as before. The archived edges of the old document stay archived;
  edges the import names are inserted (InsertOnly) or handled by the existing Upsert path.
- A plain `Unique` index has no state filter, so it keeps blocking the value of an archived entity. That is
  unchanged on purpose (CK behaviour; changing it needs a product decision). CK authors should declare business keys
  as `UniqueNotDeleted`. When it blocks, the failure is
  `Bulk import failed: Cannot write entity of type '...' with rtId '...': the value { ... } violates the unique index
  '...' of collection '...'. The blocking document 'id' is in state Archived. ... Erase the archived entity ... or let
  the CK type declare the index as UniqueNotDeleted.` (`MongoDbDataSourceCollection.DescribeBulkDuplicateKeyAsync`;
  the key text is parsed from the server message by `DuplicateKeyMessageParser`, the lookup runs outside the aborted
  transaction and falls back to the raw message if it fails).
- A seed item that an operator deleted (archived) comes back with the next Upsert / blueprint update and with an
  InsertOnly import of the same rtId. Whether that is wanted is an open product question, tracked there.
- Tests: `ImportAfterArchiveTests` (rtId, Upsert, Erase, to-one and N:M edges, `Unique` vs `UniqueNotDeleted`),
  `DuplicateKeyMessageParserTests`.

## Atomic update of one RecordArray item

Engine contract and rules: engine `octo-construction-kit-engine/docs/runtime-semantics.md`, "Atomic update of one RecordArray item". This repository executes it:
`MongoDbDataSourceCollection.UpdateOneWithRecordArrayItemsAsync` (called by `BulkRtMutation` for an update info with
`RecordArrayItemUpdates`) builds ONE `updateOne` with `RecordArrayItemUpdateStatement`:

```
filter : { _id, attributes.<arr>: { $type: "array" } ...,
           (item-only) $or: [ { attributes.<arr>: { $elemMatch: { "attributes.<matchMember>": <value> } } } ... ] }
update : { $set: { "attributes.<arr>.$[i0].attributes.<member>": <value>, ...,   // + the regular attribute $sets
                   rtChangedDateTime }, $inc: { rtVersion: 1 } }
options: arrayFilters: [ { "i0.attributes.<matchMember>": <value> } ... ]
```

- A stored record is `{ ckRecordId, attributes: { <camelCaseMember>: value } }`, hence the `.attributes.` segment in the item path.
- **No read, no version check, no retry.** Different items of one entity never interfere; the same item keeps the last write.
  Two transactions that overlap on one document still fail fast with MongoDB's `TransientTransactionError` (as for any update);
  the caller runs the transaction again. The write itself no longer depends on a value read earlier.
- `$[iN]` needs the array path to exist, so the filter requires every referenced attribute to be an array. **Item-only** update:
  it also requires one matching item - otherwise `matchedCount` is 0, nothing is written (no `rtVersion` bump) and the method
  returns `false` (engine warning 27). **Mixed** update (regular attributes in the same info): the regular attributes are written
  even when the array is missing or null (fallback, see the repair rule below; a missing entity throws as for any update).
  An entry that matches no item while others match is a silent no-op for that entry.
- `rtVersion` is incremented once and `rtChangedDateTime` set by the shared `ApplyUpdate`; the change stream shows a normal
  `update` event (`updatedFields`: `rtVersion`, `rtChangedDateTime`, `attributes.<arr>.<index>.attributes.<member>`).
- **`attributes: null` repair only on the fallback.** The item statement needs no repair: its filter requires every
  referenced array to be `$type: array`, which a stored `attributes: null` can never satisfy, so the statement either does not
  match or runs on an object `attributes`. Happy path (array present) = **1 command** (it was 2 with a repair `updateOne` before
  the statement). Fallback (mixed update, array missing or null) = the statement plus ONE ordered `BulkWrite` `[repair, plain update]`
  like `UpdateOneAsync` = **2 commands** (was 3); `MatchedCount == 0` there still means "entity not found". The repair is NOT folded
  into the item statement's own bulk write on purpose: the method returns whether the item statement matched, and an aggregate
  `BulkWriteResult.MatchedCount` cannot tell a repair-only match from an item match (same reason `UpdateOneIfGuardMatchesAsync`
  keeps its repair separate). Item-only updates: 1 command, never a repair.
- Tests (real MongoDB): `RecordArrayItemUpdateTests` (60 iterations of two/three parallel updates of different items with and
  without transaction, same item, no match, mixed, change stream, Secret member refused, invalid updates) and
  `DataPermissionEnforcementTests.RecordArrayItemUpdate_FollowsTheWritePermissionsOfTheEntity`. The command counts above are
  asserted with `MongoRequestScope.Begin(out var stats)` + `stats.CommandCount` (`MixedUpdate_OnTheHappyPath_CostsOneMongoDbCommand`,
  `…OnAStoredNullAttributes_RepairsInTheFallbackBulkWrite`, `…OnAMissingArray…`, `…OnAMissingEntity_StillThrowsNotFound`,
  `ItemOnlyUpdate_WithoutMatch_CostsOneCommand_AndNeverRepairs`). The test model
  (`tests/TestCkModel`) got the record `DataPoint` and the type `ItemUpdateHolder` for it.

## Expected rtVersion

Engine contract and rules: engine `octo-construction-kit-engine/docs/runtime-semantics.md`, "Expected rtVersion - optimistic concurrency for update and replace". This
repository executes it in `MongoDbDataSourceCollection` (called by `BulkRtMutation` for an update or replace info with
`ExpectedRtVersion`):

```
update  : updateOne  { _id, rtVersion: <n> }  { $set ..., $inc: { rtVersion: 1 } }          (UpdateOneIfVersionMatchesAsync)
replace : replaceOne { _id, rtVersion: <n> }  <document with rtVersion = n + 1>              (ReplaceByIdIfVersionMatchesAsync)
```

- The version is part of the filter, so check and write are **one atomic statement**; `MatchedCount == 0` means "not applied". One more
  `countDocuments` (limit 1) then tells a missing document (`EntityNotFoundException`, as for any update) from a version mismatch
  (`false`, which the engine turns into `RtVersionConflictException`).
- Version 0 also matches a document without an `rtVersion` field (persisted before the field existed reads as 0).
- **Inside a transaction** a writer that overlaps another transaction on the same document does not see a filter mismatch but fails
  with MongoDB's `WriteConflict` (code 112, `TransientTransactionError`); both methods report that as the same conflict (`false`), and
  the transaction is already aborted, so the retry must start a new session / transaction. If the other writer committed before this
  transaction started, the filter mismatch path applies.
- `UpdateOneIfVersionMatchesAsync` applies the `attributes: null` repair first (separate statement, as
  `UpdateOneIfGuardMatchesAsync`).
- A replace without an expected version still goes through `ReplaceManyAsync` / `BulkImportAsync` unchanged; entities with an expected
  version in the same batch are written one by one (the bulk upsert cannot compare a version).
- Tests (real MongoDB): `ExpectedRtVersionTests` - match (version n -> n+1), stale version for update and replace (distinct
  `RtVersionConflictException`, nothing written), missing entity (not-found, not a conflict), bulk `ApplyChangesAsync` with one stale
  entity (transaction writes nothing), unversioned replace unchanged, and two parallel read-modify-write increments (each writer retries until
  success, generous cap, so the test asserts correctness and not timing), repeated 50 times for update and for replace (counter always
  exactly +2, version +2, at least one `RtVersionConflictException` surfaced). The counter is `GuardCount` of the test model's
  `SeedGuardEntity` (the CK round-trip gate compares the whole test model, so no type was added). `ConditionalUpdateTests` (`AttributeNewerThanGuard`) is unchanged.

## Validate a CK model without importing it

`ITenantContext.ValidateCkModelAsync(CkCompiledModelRoot)` → `OperationResult` (via `IDatabaseCkModelRepository.ValidateModelAsync`) runs the
checks the import runs **before it writes**, against the models installed in the tenant, and writes nothing (no import lock, no `Importing` row,
no element rows, no notification, no re-validation; tenant data source, not the admin one). Shared with the import, so there is one rule:
`DatabaseCkModelRepository.FindEngineRefusal` (CK language, message 91; `minEngineVersion`, message 126) and `FindSystemReferenceMismatchAsync`
(message 145 `CkModelSystemReferenceMismatch`: `modelId`, `referencedModelId`, `installedModelId`); the import throws `ModelValidationException` with exactly the same text as before
(`CkModelValidationMessages`). Then `IRepositoryModelResolver.HardResolveAsync` against the tenant; the resolver reports element errors as messages
but **throws** for a dependency the tenant does not have, which becomes message 146 `CkModelDependencyNotResolvable` (`modelId`, `detail` = the resolver's text naming the dependency; never an exception to
the caller). Consumer: asset repository `Models/ValidateCk` / MCP `validate_ck_model`. Tests: `CkModelImportGuard/ValidateCkModelTests` (valid model
not imported and the CK collections unchanged, missing dependency (146), 145 and 91 with the import's text, 126). All numbers come from the
engine message catalog (`MessageCodes.json`); there is no number outside it.

## Runtime query time limit

`RuntimeQueries/`: a host that calls `AddRuntimeQueryTimeLimit()` (the asset repository) sends every runtime-entity query
and count by type, by id, by type with navigation and by CK interface to MongoDB with `maxTimeMS` =
`RuntimeQueryOptions.MaxTimeMs` (section `RuntimeQuery`, default 120,000, 0 = off), per MongoDB operation (page and
count of a paged query each get the full limit). Rules to keep:

- **One path per operation.** `SingleOriginQuery.AggregateToListAsync` and `RtInterfaceQueryExecutor.RunLimitedAsync`
  run every pipeline and count of a runtime query through `RuntimeQueryTimeLimiter`, which puts the limit into
  `AggregateOptions` / `CountOptions`, turns `MongoExecutionTimeoutException` into `RuntimeQueryTimeoutException` and
  logs a warning above 80 % of the limit. A new MongoDB call in these query classes goes through them too; the hinted page read and the capped count of
  do (hint and limit in one `AggregateOptions`, the retry without the hint as well).
- **Opt-in per host.** `TenantRepository.CreateRuntimeQueryTimeLimiter` returns null (no limit, the code path of
  before) when the host did not register `RuntimeQueryTimeLimitSettings` and no override scope is active. CK queries
  never get a limiter.
- **Overrides are call-site scopes**, never a global switch: `RuntimeQueryTimeLimit.Override(TimeSpan)` /
  `Disable()` (`AsyncLocal`, holds across `await` and for tasks started inside, restores the previous limit on dispose).
  In this repository: the CK migrations of `TenantContext` (`RunCkModelMigrationsForImportAsync`,
  `RetryPendingMigrationsAsync`) and the blueprint of a new child tenant run inside `Disable()`.
- **Not covered yet**: the association and deep-graph queries (`MultipleOrigin*`).
- Tests: `StreamData.UnitTests/RuntimeQueries/RuntimeQueryTimeLimitTests`,
  `Repositories/Query/RuntimeQueryTimeLimitIntegrationTests` (a `$range`/`$unwind` fan-out that MongoDB stops with
  `MaxTimeMSExpired`; server-side JavaScript is interrupted with another error and is not a valid fixture),
  `InterfaceQuery/InterfaceQueryTimeLimitTests`.


## JSON ImportRt against MongoDB

`ImportRtJsonAwaitIntegrationTests` (own collection and fixture, throw-away child tenants, CK model `JsonAwait` built in C#)
pins the engine fix against a real MongoDB: a JSON `ImportRt` (`IImportRtModelCommand.ImportAsync` /
`ImportAsCallerAsync` with `application/json`) returns only after its entities are committed (readable at once), a record
member that only a CK method may write is carried over by record key, a keyless (also derived) record array with a stored
protected value is refused with message 141 and writes nothing, and a duplicate key in the file fails the call and commits no
entity of the file. Before the engine fix all five were red (the JSON bulks were written from an `async void` handler after the
call had committed, and record values arrived as JSON objects). The engine side: engine `octo-construction-kit-engine/docs/blueprint-engine.md`, "JSON import writes its
bulks inside the awaited call".


## Caller ImportRt keeps protected record members

Engine rule: engine `octo-construction-kit-engine/docs/blueprint-engine.md`, "Omission inside records: merge". `ImportRtRecordMemberPreservationIntegrationTests`
(own collection, throw-away tenant per test, C#-built `ckLanguage: 2` model `RecMem` imported through `ImportCkModelAsync`, so
records are resolved by the engine like any installed model) pins it against MongoDB: the stored values are written through the
repository, a caller `ImportAsCallerAsync` upsert (YAML) omits the `MethodOnly` member, and the document read back keeps it - single
record (position), record array (record key, reordered, a new element keeps nothing), a member that exists only on a record derived
from the declared one; a keyless record array (also of a derived record) with a stored value is refused with message 141 and the
stored document is unchanged. One limit of the import path the test works around, outside the scope of that test:

- the YAML reader of `ImportRt` (`RtRecordConverter`) reads only scalar member values inside a record, so nested records cannot be
  written by a YAML file (covered by the engine unit tests);
