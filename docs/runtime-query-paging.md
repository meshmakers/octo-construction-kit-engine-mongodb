---
description: "Runtime query paging: deep sorted pages and the page order of runtime queries"
applies_to: src/Runtime.Engine.MongoDb/RuntimeQueries/**, src/Runtime.Engine.MongoDb/Repositories/Query/**
---

## Deep sorted pages: the index hint (AB#9138)

Since AB#8896 a page (`$match`, `$sort`, `$skip`, `$limit`) is planned by MongoDB as a whole. For a deep page no
candidate plan returns results early, so the multi-planner ran every candidate through its trial on every request, and
the plan cache never kept the winner (the cached works value comes from page 1). On sbeg (909,000 `EdaMessage`, skip
50,000) that took 56-67 s; the execution itself took ~90 ms, and with a hint 52 ms. Rules:

- **Hint the index that provides the sort.** `SingleOriginQuery.ChoosePageHintAsync` hints the page read
  (`AggregateOptions.Hint`) to the first index `SortIndexHint.Choose` finds: keys `ckTypeId` followed by the sort keys,
  further keys allowed, all directions equal or all reversed. Never a partial, sparse, hidden or collated index, never
  text/hashed/2dsphere keys: a hint must not return fewer documents or force an index the query cannot use. Both CK
  shapes qualify (legacy `{ckTypeId, attr, rtState}`, AB#8960 `{ckTypeId, attr, _id, rtState}`), and the System Entity
  index `{ckTypeId, _id, rtState}` serves a sort by rtId.
- **Only deep pages** (`OctoSystemConfiguration.PagingIndexHintMinSkip`, default 1000; 0 always, negative never): below
  it the trial ends early (skip 1,000: 14 ms without, 1 ms with the hint).
- **A filter with an index of its own** (`SortIndexHint.HasFilterIndex`: an index leading, after `ckTypeId`, with a
  filtered field other than `ckTypeId`, `rtState` and the first sort key) is a real alternative for the planner: few
  matches read through it and sorted are faster than the walk along the sort index (2,000 matches: 32 ms planner,
  406 ms hinted). The page is then hinted only with at least `PagingIndexHintMinMatches` matches (default 50,000):
  counted exactly when the caller asked for the total - that count then runs **before** the page and is reused (the
  asset repository's `TotalCoveringThePage` keeps the total at least `offset + items`, in both orders) - otherwise with a
  capped count (`ExecuteCappedCount`, `$limit` before `$count`, 2-21 ms over 900,000 entities).
- **No hint** without a sort, inside a transaction (a hint naming an index dropped meanwhile fails the command, which
  aborts the transaction - no retry possible; GraphQL queries run without a transaction), and for pipelines with stages
  around the match (`$geoNear`, text search, association filters: `PageSortFollowsMatch`).
- **Index list** from `CollectionIndexCache` (`listIndexes` per database + collection, cached 5 minutes per process;
  every index create/drop of `MongoDbDataSourceCollection` invalidates it: an entry carries the invalidation generation it
  was read in and is used only while that generation is current, so a list read across an invalidation is never reused,
  even when it was stored after it; a failure means no hint and is remembered for 10 s only). The page is hinted by the index's **key pattern**, so
  an index dropped or rebuilt under its name with other keys by another process fails the hint (codes 2/27): the entry is
  invalidated and the page is read once more without a hint. An index whose key pattern another index shares (a collated
  or partial twin) is never chosen: the server refuses a key-pattern hint that matches several indexes. The tiebreak
  gate of AB#8960 reads the same cache (one `listIndexes`, one invalidation). With the `_id` tiebreak the page sorts by
  the caller's keys and `_id` (`Query.PageSortKeys`), so only an index that provides `_id` after them is hinted (the
  tiebreak shape `{ckTypeId, attr, _id, rtState}`, found by its keys whatever its name).
- **Settings are process-wide**: `TenantContext` hands `PagingIndexHintMinSkip` / `PagingIndexHintMinMatches` to
  `SortIndexHint` (one system configuration per process, like the schema instance prefix); a value of 0 or less for the
  matches hints without counting.

Measured with `PagedQuerySortHintPerformanceTests` (`Category=Performance`; 900,000 entities, page 10, 8 candidate plans,
median of 3, local Testcontainer; planner -> hint):

| Case | Skip | Planner | Hint | Engine (decision included) |
|---|---|---|---|---|
| sort rtId | 0 / 100 / 1,000 / 10,000 | 2 / 3 / 14 / 158 ms | 1 / 1 / 1 / 7 ms | |
| sort rtId | 50,000 | 616 ms | 21 ms | 17 ms (with total 141 ms) |
| sort CK attribute asc / desc | 50,000 | 742 / 666 ms | 22 / 20 ms | |
| filter without index (half), sort rtId | 50,000 | 3,350 ms | 487 ms | |
| indexed range filter, 2,000 matches | 1,000 | 32 ms | 406 ms | 8 ms, planner kept (capped count 2 ms) |
| indexed range filter, 20,000 matches | 10,000 | 20 ms | 405 ms | 30 ms, planner kept (capped count 9 ms) |
| indexed range filter, 100,000 matches | 50,000 | 1,781 ms | 426 ms | 433 ms, hinted (capped count 21 ms) |

Tests: `SortIndexHintTests` (unit: index shapes, exclusions, filter fields, competing indexes),
`CollectionIndexCacheTests` (unit: reuse until invalidated, a list read across an invalidation is not reused),
`PagedQuerySortHintTests` (explain: deep page hinted without SORT - neither in the winning plan nor as an aggregation-level
`$sort` -, ascending/descending, rtId; shallow, unsorted and unprovided sorts without hint; transaction; stale hint
retried; indexed selective filter keeps the planner and counts once).

## Page order of runtime queries: the _id tiebreak (AB#8960)

`$skip`/`$limit` are only deterministic over a total order. Over equal sort keys (or without a sort) MongoDB returns the
ties in an order that can change from one page to the next: 300 continents with the same `Name`, paged by 7, came back
as 300 items of which only 35 were distinct. Rules:

- **A page of a type query sorts totally.** `SingleOriginQuery.CreatePagePipelineStageDefinitions(skip, take,
  idTiebreak)` ends with the `$sort` of `Query.CreatePagingSortDefinition`: the caller's keys, then `_id` in the
  direction of the last caller key (so one index `{..., attr, _id}` serves the page forwards and backwards), unless a
  caller key already is `_id` (`rtId`, `IsSortUnique`); without a caller sort `_id` ascending alone. Without a caller
  sort a `$geoNear` (nearest first) or a text search (best match first, CK queries) keeps its own order and gets no
  `$sort` (`PreStagesDefineOrder`). The enrichment read of the page uses the same sort, so ties keep their page order.
- **Only where no index order is lost (the gate).** `PagingTiebreakGate` decides per database, collection and caller
  sort. A page sorted by keys whose order an index provides that the tiebreak would break pages without it (the
  order before AB#8960, from that index): a CK Ascending index in the legacy shape that provides the complete caller
  sort (`SortIndexHint.Provides`; with more keys than it provides the page sorts in memory anyway and gets the tiebreak),
  and a CK Unique index with exactly the engine's type filter when the sort starts with all of its keys, all in its
  directions or all reversed (measured for a single key: IXSCAN without the tiebreak, a SORT over every document with
  it; the combination is unique within the index's types anyway) - `TiebreakIndexShape.LeadingKeyWithoutTiebreak` /
  `UniqueKeysWithoutTiebreak`. A compound unique index sorted by its first key only gets the tiebreak (its first key
  can repeat). A UniqueNotDeleted index (partial filter on `rtState`; it never served the page order), a unique index
  with any other partial filter or none (`ClassifyUniquePartialFilter`) and a sparse, hidden or collated unique index
  (a sparse one leaves out the documents without the key, which tie) do not count. Every other page
  gets the tiebreak right away, also on existing tenants: unsorted pages (the System Entity index
  `{ckTypeId, _id, rtState}` is on every collection) and pages sorted by an attribute no such index leads with (a
  blocking sort before as well). The answer comes from `listIndexes` through `CollectionIndexCache` (AB#9138, cached per
  process for 5 min, invalidated with its generation by every index change of the same process) - no marker, no read
  per query; it is not read for
  `take` 0. Several instances: after another instance upgraded, this one pages the legacy way for at most 5 minutes
  (safe); after a legacy index came back (rollback), this one keeps the tiebreak against it for at most 5 minutes (the
  regression below for that time). A `listIndexes` failure (any exception but a cancellation) means no tiebreak for
  sorted pages until the next check and never fails the query. Edge case of the Unique rule: entities of types outside
  the index's partial filter (a sort over a base type) are not totally ordered - as before AB#8960 (follow-up AB#9215).
- **Index shape.** CK Ascending indexes are `{ckTypeId, attr..., _id, rtState}` and named `<Type>_<n>_id`
  (`TiebreakIndexShape`, `MongoDbRepositoryDataSource.PrepareAndCreateIndex`): `_id` right after the attribute fields,
  before `rtState` wherever it stands (a `$ne` range; a key after a range cannot provide a sort), also for indexes with
  several field groups. An index that holds `_id` already (System Entity `{CkTypeId, _id}`) keeps its shape and name.
  Unique, UniqueNotDeleted and text indexes and the system-level indexes `_9000` (`rtState`) / `_9001`
  (`ckTypeId, rtCreatedBy`) keep theirs; CK has no TTL, wildcard or 2dsphere index type. The TTL indexes of the lock,
  query-result-cache and method-snapshot collections, the GridFS owner index, the RtAssociation and tenant-lifecycle
  indexes are outside the CK definition and unchanged.
- **Legacy indexes are kept until the explicit upgrade.** A CK import (also the System model import inside
  `TryFindTenantContextAsync`, which reaches every collection) and `ITenantContext.UpdateIndexesAsync` keep an existing
  legacy index and create only missing indexes (in the new shape) - an import must never rebuild the indexes of a large
  collection while a tenant is resolved. `ITenantContext.UpgradeTiebreakIndexesAsync` (data source
  `UpgradeTiebreakIndexesAsync`, collection by collection, each under `index_update_lock` of its own - see "Tiebreak
  upgrade holds the lock per collection" below) builds `<Type>_<n>_id` next to
  `<Type>_<n>` and drops the legacy index only in the cleanup at the end of the collection, after the build: the
  collection never lacks the index. A failed build keeps the legacy index. Idempotent. An engine before AB#8960 (rollback)
  sorts its pages from the new index (prefix) and, on its next index update, rebuilds `<Type>_<n>` and then drops
  `<Type>_<n>_id` the same way. Both interface members are default interface methods (only NSubstitute fakes implement
  `ITenantContext` outside this repository); the caller that runs the upgrade per tenant is a story of its own.
- **Index updates never leave a gap (AC 8, AC 13).** `CreateOrUpdateIndex` never drops an index before its replacement is
  built. An index with the right definition under another name is kept (adopted: no drop, no rebuild). The definition
  is compared by `SameDefinition`: a text index by its language (default `en`) and its paths with their weights (a set:
  `listIndexes` groups them by weight; before AB#8960 the missing default language made every text index look changed,
  so it was dropped and rebuilt on every import), every other index by its keys in order
  (a definition with several field groups is built as one key list and read back as one group). A Unique or
  UniqueNotDeleted index must also name exactly the types of the definition in its partial filter
  (`CkTypeIndexWithName.PartialFilterTypeIds`): after a descendant was added, or the type moved to another model, the
  index is replaced like an index with other keys, so the new types are unique-checked. An index
  with options the engine never sets on a CK index of its type (`CkTypeIndexWithName.HasForeignOptions`: collation,
  sparse, hidden, TTL, a partial filter on a non-unique index, a unique index without exactly the engine's partial
  filter `ckTypeId $in` (plus `rtState $in [null, 0]` for UniqueNotDeleted), `TiebreakIndexShape.ClassifyUniquePartialFilter`)
  is never adopted, whatever its keys. If such an index
  blocks our build (`IndexOptionsConflict`: same keys, other options only), it stays, a warning is logged and ours is
  tracked as failed - an index is only dropped once its replacement is built. Both unique types carry a partial filter
  (their types); `MongoDbDataSourceCollection.GetIndexListAsync` reads an index as UniqueNotDeleted only when its partial
  filter has a condition on `rtState` (`TiebreakIndexShape.HasStateCondition`, not a text search: a type id such as
  `Model/StartState` contains the text) (before AB#8960 every partial unique index was read as UniqueNotDeleted, so a CK Unique index was
  dropped and rebuilt on every import). The gate counts an Ascending index as legacy only when its keys start with
  `ckTypeId`, so a hand-made index whose default name (`attributes.code_1`) matches the name pattern gets the tiebreak. An index that
  holds the expected name with another definition (a CK update gave the slot other keys) stays until the cleanup at the
  end of the collection; the new index is built next to it under `AlternateIndexName`: `<expected name>_<6 hex>`, the
  SHA-256 of the definition (type, language, key paths and weights, the type set of a unique index), so repeated
  updates find the same name. MongoDB
  can neither rename an index nor hold the same keys under two names, so keeping the expected name would cost a second
  build. A failed build keeps the old index - also the slot's index that an earlier update built under its
  alternate name, when the expected name is free by now. Exception: a text index (a collection holds only one) is still dropped
  before the changed one is built - the existing text index whatever its name (it may be kept under the name of an
  earlier slot). If the expected name is held by an index kept for another slot
  (`IndexOptionsConflict` 85 / `IndexKeySpecsConflict` 86), the build falls back to the alternate name. Index state
  tracking uses the real name (the replaced one is removed). Names are therefore not a contract: the tiebreak shape is
  decided by `_id` in the keys, the gate recognises CK indexes by `..._<n>` with the optional `_<6 hex>`
  (`TiebreakIndexShape.CkIndexName`). Tests: `TiebreakIndexUpgradeTests.CkUpdate_ChangingTheFieldsOfATiebreakIndex_*`
  (60,000 entities, two changes of one slot, listIndexes observed during each import), `..._IsKept_NotRebuilt`,
  `CkUpdate_FailedBuildOfTheChangedIndex_KeepsTheOldIndex`.
- **Two types, one key spec (AB#9342).** A CK Ascending index holds the field `ckTypeId`, not the type value, so two types
  of one collection that index the same attributes produce the same key spec, and MongoDB holds one non-unique index per
  key spec and options (a second one under another name is refused with 85). `UpdateIndexesForCollectionRoot` keeps a
  per-collection-run record of the Ascending indexes kept or built (`SharedIndexKeySpecs`, key spec -> name): the first
  type keeps or builds the index, every later type with the same keys is served by it (no createIndexes, state `Applied`
  under the shared name, its own expected name's state removed, one information line). A legacy index kept by an update
  without the upgrade is recorded too and serves every type with its keys (state `Applied` under its name), so the second
  type no longer builds a tiebreak index next to it (that mixed state left the legacy index forever: the upgrade's build of
  the other type's tiebreak index failed with 85). A legacy index is kept only while no tiebreak index with its keys is
  held in the run: on a collection that holds both the legacy index of one type and the tiebreak index of the other, the
  tiebreak index serves both types and the cleanup drops the legacy index, also in an update without the upgrade (as for a
  single type whose tiebreak index exists; nothing is built). The result does not depend on the order of the types. Unique/UniqueNotDeleted (per-type partial filter) and text indexes (merged) are never
  recorded, nor indexes with foreign options. `IndexStateService.RemoveIndexState` removes the stored state of the type
  too, not only the one tracked in the run. Tests: `SharedIndexKeySpecTests` (fresh tenant, mixed legacy/tiebreak state,
  shared legacy index, a stored `Failed` state removed, unique indexes of two types; each in both type orders).
- **Index states name the indexes that exist (AB#9391).** `CkType.IndexStates` is bookkeeping for operators and the
  status view; the indexes themselves do not depend on it. `IndexStateService` tracks one collection run
  (`UpdateIndexesForCollectionRoot`, `BeginTracking(collection, types of the collection)`) and applies these rules when the
  `finally` of the run persists the states:
  - **Reconcile.** After the cleanup the run reads the final index list once (`Reconcile`): every `Applied` state of a type
    of the collection (base chain, root, descendants) whose index is not in it is removed, stored or tracked - whatever
    dropped it (the cleanup's plain `DropIndexAsync`, a replaced text index, an earlier run aborted before it persisted its
    states). Without the list (read failed: a warning is logged) the states are persisted as tracked and a stale `Applied`
    state can stay until the next run that reads the list. Every index that serves a definition is recorded `Applied`,
    also one found under its expected name with the definition (built by an earlier run, also one aborted before it
    persisted its states), and the legacy index that still serves a definition whose tiebreak build failed in the
    upgrade (the type's own, or the one another type kept earlier in the run), so the type keeps `Applied` under the
    index that serves it.
  - **A failure stays visible until the target shape exists.** `Failed` names the index a definition wants. An update that
    keeps a legacy index (`TrackServedBy(..., targetShape: false)`) removes only an `Applied` state under the expected name,
    so the `Failed` of an upgrade whose tiebreak build failed survives every CK import until a later build or upgrade
    succeeds, an index with the definition is found under the expected name, or an index in the target shape of another
    type serves the definition (AB#9342). Every definition expects its expected name and its alternate name
    (`ExpectIndex`); after a completed run a `Failed` state under a name no definition of the type expected in that run is
    removed (the definition is gone). After an aborted run no `Failed` state is removed for that reason (the definitions
    not processed yet expected nothing); what the run changed before the abort - a failure it recorded, a failure it
    removed because the definition is served in its target shape - is persisted. A run aborted because the lock was lost
    persists nothing (AB#9302). A definition served in its target shape removes a `Failed` state under its alternate name.
  - **A failure never replaces `Applied`.** The tracker keys by index name and collection; a `Failed` never overwrites an
    `Applied` the run recorded under that name. A failed build is recorded with `TrackFailure` and named only after every
    definition of the collection was processed (`ResolveFailures`, AB#9302): when by then the type is `Applied` under the
    definition's expected name - another definition of the type is served by the index there (two definitions whose
    existing indexes hold each other's keys, in either order), or a legacy index with that name keeps serving it after a
    failed upgrade - the failure is recorded under `AlternateIndexName`, the name its index gets while the expected name is
    taken and that a later successful build uses. Before AB#9302 the name was chosen at the failure, so a later
    definition's `Applied` replaced it until the next run. The index a failed build would have replaced (under the
    expected name, the alternate name or, for a unique index, with the keys) stays a candidate for the later definitions
    of the run - it is no longer taken out of the list - and the cleanup does not drop it
    (`SharedIndexKeySpecs.KeepAfterFailedBuild`, released again when a later build replaces it); before AB#9302 a later
    definition with its keys could not adopt it, failed with "Index already exists" and recorded a second wrong failure. `CkIndexState` is unchanged (no new field).
  - **No write without a change.** The merged states are compared with the stored ones by name, collection, state and
    error message; an unchanged run writes no CkType document (`AppliedAt` is ignored, an unchanged state keeps its stored
    `AppliedAt`). Before, every CK import rewrote the CkType documents of every type served by a kept, shared or adopted
    index. Only the `IndexStates` field is written (`$set`), never the whole document - by `BulkUpdateIndexStatesAsync`
    and, since AB#9302, by `ClearIndexStatesForCollectionAsync` (a collection whose types define no index) - so a CkType
    change committed between the read and the write (a model import writes its types before it takes
    `index_update_lock`) is not reverted. Persisting reads
    the types of the collection with one query per collection run.
  - **Log.** The sharing line of a legacy index built for another type names that type (`FindStoredHolderAsync`): the
    type of the collection whose slot name the index carries (`IsSlotNameOf`: `<collection>_<n>`, optionally `_id` and an
    alternate-name hash, nothing else - AB#9302), in every order of the types; for an index with another
    name the only type with a stored `Applied` state under it, otherwise `(unknown)`. Only that last case reads the
    stored types, once per collection run and separately from the read for persisting (another purpose, read at
    another time).
  Tests: `SharedIndexKeySpecTests.IndexStates.cs` (upgrade dropping a shared legacy index, retry after an aborted upgrade,
  a failed tiebreak build through a normal update until the upgrade succeeds, a failed slot next to an adopted index, no
  write on an unchanged update and upgrade, holder identification; each in both orders). The failed builds are produced
  with MongoDB's limit of 64 indexes per collection.
- **Tiebreak upgrade holds the lock per collection (AB#9302).** `UpgradeTiebreakIndexesAsync`
  (`UpgradeTiebreakIndexesPerCollectionAsync`) takes `index_update_lock` once per collection root, not for the whole
  tenant. Each lock section re-reads the CK metadata (`ReadIndexUpdatePlanAsync`: a CK import between two sections can
  change, add or remove collection roots and index definitions - a stale definition would drop an index the import just
  built), takes the next collection root not processed in this run (by collection name: an import may raise the model
  version), upgrades it and releases the lock; a last section creates the model-independent indexes (RtAssociations,
  GridFS owner). A collection an import created meanwhile is upgraded in the same run. The index state tracking scope
  (`BeginTracking` .. persist .. `EndTracking`) lies inside `UpdateIndexesForCollectionRoot`, so inside one section.
  - **Never on a collection of a model being imported.** A CK import releases the lock after its index update and only
    then sets its model Available (`DatabaseCkModelRepository`). A section in that window would not see the model's index
    definitions and drop its indexes. So a section reads the metadata with the Importing models
    (`includeModelsInStateImporting: true`) only to recognise them: a root whose collection holds a type of an Importing
    model (root, base chain or descendant, `InvolvesImportingModel`) is skipped, retried once after every other
    collection, and if still involved left for the next run (a warning names the collections). The upgrade never waits
    for imports to end, so steady imports cannot starve it. A model in another non-Available state (ResolveFailed) is
    treated as by every index update: its types are not part of the metadata.
  - **What a concurrent import waits for.** A CK import (`UpdateIndexAsync`, also `ITenantContext.UpdateIndexesAsync`)
    keeps the whole-tenant lock and its 90 s wait (`RepositoryDistributedLockService.MaxAcquireDuration`); it waits for
    the collection the upgrade works on, not for the whole tenant. The yield gives it the chance to take the lock next,
    not a guarantee: an import can wait through several sections, and still times out when the sections it waits for
    take longer than its 90 s. It creates its lock with `markWaiters`: while it waits it keeps a waiter
    marker `waiter:index_update_lock:<owner token>` in `SysLock` (expiry 5 s, refreshed on every 1 s poll, deleted when
    it stops waiting). After each section the upgrade checks the markers (`HasWaitersAsync`); with a fresh one it gives
    the waiter up to 3 s (`YieldToWaiterMaxWait`) to take the lock, then acquires for the next collection with a wait of
    up to one hour (`UpgradeSectionMaxWait`) - it never times out behind an import. A dead waiter's marker expires, so
    the upgrade waits at most 3 s per section for it, and only while the marker is fresh. The marker is opt-in per lock:
    every other lock (model import, migrations, ...) behaves as before.
  - **Cost of one collection.** Usually an import waits for the current collection's tiebreak build plus the 3 s yield;
    the yield is no guaranteed turn, so it can be several sections.
    Measured (`TiebreakUpgradeSectionPerformanceTests`, `Category=Performance`, local Testcontainer, 2026-11-23): one
    collection with 1,000,000 documents of about 1 KB and one legacy index took 2.1 s for its section (metadata re-read,
    tiebreak build, legacy drop, index states), the other collections of the tenant at most 121 ms each. A collection
    rebuilds its legacy indexes one after the other, so its section grows with documents x legacy indexes; at the local
    rate one section alone reaches 90 s at about 40 million document-indexes. That is an estimate for one section, not a
    threshold for import timeouts: an import that waits through several sections can exceed its 90 s with smaller
    collections. Production disks under load are slower - treat the local rate as a lower bound and check the
    "finished in" lines of the first production upgrade. The import's 90 s wait is not raised (no collection near that
    size is known); if one is, upgrade that tenant outside import times.
  - **Cancellation and lock loss.** The caller's token stops before the next section (an index build already started
    finishes, the collection's run completes). A lost lock (`LockLostToken`, signalled by the heartbeat within 15 s of
    the takeover) aborts the current collection before its next index operation (also before the text-index drop and
    the RtAssociation index maintenance); once the loss is detected - also after the final index-list read - its index
    states are not persisted (a token check cannot fence a write already started; states are written with `$set` on
    `IndexStates` only) (another holder may work on the collection; the next run
    reconciles), and the upgrade does not go on with the next collection. In both cases the lock is not held afterwards
    (released, or another holder's). The index-state write checks the token again after reading the stored states
    and before each type it writes (`IndexStateService.BulkUpdateIndexStatesAsync`).
  - **Acquire deadline and loss during the acquire.** `AcquireLockAsync(maxWait)` never polls after `maxWait`: a retry
    that would start after it (an overrun delay, a slow waiter-marker write) ends in the `TimeoutException`. When the
    heartbeat finds the lock taken while the waiter marker is still being cleaned up, the acquire throws an
    `OperationCanceledException` with the cancelled `LockLostToken` instead of returning success, and leaves the lock
    document to its new holder (`RepositoryDistributedLockServiceDeadlineTests`).
  - **Logs.** Per section `index_update_lock` acquire/release at debug (the lock service's routine lines at debug too,
    `routineLogLevel`), collection start/finish (with seconds) at information, both with tenant and collection; at the
    end the number of collections, the total and the longest collection.
  - **Not changed:** the normal index update (CK import, `UpdateIndexesAsync`) still holds the lock for the whole tenant
    (AC 6: it creates only missing indexes, fast, from one consistent metadata pass). Two side effects for it: its lock
    writes the waiter marker while it waits, and a lost lock now aborts it inside a collection (before the next index
    operation) without persisting that collection's index states - before, it stopped only between collections and
    persisted them. MongoDB's own collection locks of an
    index build are unchanged: while a collection's index is built, readers of that collection can be blocked briefly
    (test-2 2026-11-18: one rollup tick of every tenant failed while `octosystem.RtEntity_SystemTenant` was rebuilt) -
    in prod windows run the system tenant separately. The public API is unchanged.
  Tests: `TiebreakIndexUpgradeLockTests` (lock per collection and free between collections, an import during the upgrade
  gets the lock after the current collection and the upgrade finishes the rest from re-read metadata, a collection with
  a type of an Importing model left for the next run without a drop, a dead waiter, cancellation, lock loss after the
  tracking began), `RepositoryDistributedLockServiceTests` (waiter marker opt-in, refreshed, deleted).
- **Not changed:** unpaged queries keep the caller's sort; the association navigations (`MultipleOrigin*`), whose
  entity-driven path (AB#4369) relies on an index-backed `$sort` with a `$lookup` between `$sort` and `$limit`; the cached
  navigation path (page membership comes from the cached id list). Interface queries already break ties by rtId in their
  merge (`RowComparer`, tie re-fetch, AB#7019).
- **Measured** with `PagedQueryTiebreakPerformanceTests` (`Category=Performance`; 100,000 entities, 100 distinct names,
  ~4 KB each, page 50, median of 5, local Testcontainer):

  | CK index on `Name` | Page | skip 0: without / with tiebreak | skip 50,000: without / with tiebreak |
  |---|---|---|---|
  | legacy `{ckTypeId, name, rtState}` | sort `Name` | 1 ms (IXSCAN, 50 docs) / 103 ms (SORT over 100,000 docs) | 526 ms / 522 ms (SORT spilled to disk) |
  | legacy | no sort | 2 ms / 1 ms (`{ckTypeId, _id}`) | 112 ms / 392 ms (50 docs, 50,050 keys) |
  | tiebreak `{ckTypeId, name, _id, rtState}` | sort `Name` | 2 ms / 2 ms (IXSCAN, no SORT) | 464 ms / 626 ms (no SORT) |
  | tiebreak | no sort | 1 ms / 1 ms | 138 ms / 329 ms |

  The gate therefore keeps row 1 without the tiebreak until the upgrade; "without" on the tiebreak index is an engine
  before AB#8960 after a rollback. Deep unsorted pages cost the skip over more index keys (accepted for correctness).
- Tests: `PagedQueryTiebreakTests` (all pages over tied and unsorted entities, skip and cursor-style paging, sort shapes),
  `PagedQueryParityTests.PagedQuery_Unsorted_PagesFollowTheRtIdOrder` (every parity shape), `PagedQueryPipelineTests`,
  `GetRtEntitiesByInterfaceAsyncTests.PagingThroughTiedSortKeys_ReturnsEveryEntityExactlyOnceInRtIdOrder`,
  `TiebreakIndexUpgradeTests` (shapes per index type, legacy kept by import and update, upgrade replaces gaplessly and is
  idempotent, descending pages from the same index, rollback plan, gate cache across instances, unindexed sort
  attribute, Unique and UniqueNotDeleted sort attributes, compound unique, unique index with a further predicate, failed
  build of a slot under its alternate name), `TiebreakIndexShapeTests` (unit), `PagedQueryTiebreakTests`
  (also the `$geoNear` page).
