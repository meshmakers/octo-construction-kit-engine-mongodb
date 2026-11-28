# Changelog

## Unreleased

- AB#10017 Fix: a tenant restore that replaces an existing tenant (`dropExistingTenant: true`) no longer leaves the tenant
  dropped when the System-model check or the raise of a genuine older model fails at run time (migration error, catalog
  gone between the checks). The whole dump is restored into a staging database (`<prefix>restorestage_<id>`), checked
  and raised there, dumped into a temp archive, and only then is the existing tenant dropped and the checked copy restored
  into the target; no catalog read, check or raise runs after the drop. On every outcome the staging database and the
  database user the raise created for it are dropped, the tenant's CK cache in this process is unloaded and the temp
  archive is deleted; a refusal names "The existing tenant '...' was not changed." A CK migration of the raise that
  reports a failure (until now only logged) refuses the restore (`upgrade_failed`). A database user the check kept for
  the attach is dropped again when the attach fails (review of #290). Restores into a new tenant keep the two-check flow.
  The staged flow needs a second archive's disk space in the temp folder and copies the data three times. No public
  contract changes. Tests: `RestoreSystemModelCheckTests` (staged flow with failure injection). Docs:
  `docs/ck-model-import.md`, "Restore check of service-managed models".

- AB#10060 Fix: creating a child tenant with a blueprint in one call (`CreateChildTenantAsync` with a `BlueprintId`,
  POST tenants with `blueprintId`) failed for every blueprint with "Tenant '<id>' not found; cannot install CK model": the
  tenant's registry entry is still in the caller's uncommitted transaction (AB#8796) while the blueprint's CK install
  resolves the tenant in its own session. `CreatingTenantScope` now makes exactly that tenant id resolve to its new
  database in the creating async flow while the blueprint is applied; the transaction boundary is unchanged.
- AB#9326 Fix (Bug AB#10061): the restore check no longer refuses a genuine dump because the resolved dependency closure
  (`dependencies`) of a service-managed model differs from the catalog's. That closure is written by the compiler from the
  dependency versions the build saw, so the same model version exists with several closures (published System.UI-2.12.0
  lists `System-2.8.0`, services built from main embed it with `System-2.10.0`), and every genuine dump of a fresh tenant
  was refused as `content_differs` for System.UI and System.Files - on shared environments too, the published catalog
  carries the same skew. For a range-retaining model (every published System.* model) the closure is now validated
  instead of compared (`RestoreSystemModelClosure`, the rule of the engine's `CkClosureMove`, AB#5435): same model names,
  every differing entry of the same major and inside the declared range and floor, and published in the catalog (new
  refusal reason `dependency_unpublished`). Element rows, `writeAccess` and the declared `dependencyRanges` stay compared
  exactly; classic models keep their exact pins. The raise target is judged the same way. No public contract change.
  Tests: `RestoreSystemModelCheckTests` (MongoDB, mongodump; published System.UI, System.Notification and System.Files files as
  test data), `RestoreSystemModelClosureTests`,
  `RestoreSystemModelDeciderTests`. Docs: `docs/ck-model-import.md`, "Resolved dependency closure".
- AB#9326 New: a tenant restore can no longer bring in a manipulated or outdated service-managed CK model (System,
  System.*). `TenantBackupService.RestoreTenantAsync` compares every such model of the dump (model row and element rows,
  read back like the CK cache reads them) in canonical form with the catalog's model of the same id. It runs once on the
  dump's `Ck*` collections in a scratch database before anything destructive, and once on the restored database before
  the attach. Refused, with an error starting with `RESTORE_SYSTEM_MODEL_REFUSED` that names model, version and reason:
  content that differs from the published model of the same id (also for an older version), an unpublished version, a
  higher major than published, an unknown model name, a model row whose id and `ModelId` differ, element or link rows that
  the compared form does not hold (filed under another model, without a model row, extra link rows), two versions of one model,
  a version below the security floor (System.Identity 3.6.0), and an unreadable catalog or system tenant (fail closed).
  A genuine older model is raised to the platform's version (the system tenant's, which must be published with identical
  content) through the guarded system import with migrations, while the database is still unattached
  (`RestoringTenantScope`). A refused restore attaches nothing and drops the restored database when it created it; an
  existing target tenant stays as it was. WARN log per refused model and counters `octo.ck.restore_system_model.refused`
  (tag `reason`) and `octo.ck.restore_system_model.raised` (tag `model`). The per-process memo of the service-managed
  auto-import is now keyed by the tenant's registry record, so a re-attached tenant is checked again on its next resolve.
  The round-trip gate of the tests and the restore check share one canonical form (`CkCompiledModelCanonicalizer`), which
  now converts attribute default values to their value type like the import (a `TimeSpan` default is stored as ticks).
  Behaviour change: a dump with an unpublished System version (e.g. from a dev lane) is no longer restorable, and a restore
  needs the CK catalog. No public contract changes. Tests: `RestoreSystemModelCheckTests`,
  `PublishedSystemModelRoundTripTests`, `ServiceManagedImportMemoReattachTests`, `RestoreSystemModelDeciderTests`,
  `RestoreSystemModelContentTests`, `RestoringTenantScopeTests`. Docs: `docs/ck-model-import.md`, "Restore check of
  service-managed models".

- AB#9932 Fix: a native dump of an archive without Hidden columns or leftovers of computed columns copies the whole row
  again, so the restore gives a NULL in a column with a DEFAULT its default (`rtcreationdatetime`, `was_updated`;
  AB#5490, AB#6647). Since AB#9837 every dump named its columns (`format = 'json_object'`), which writes a NULL as an
  explicit `null` that `COPY FROM` keeps. That turned the CrateDB version matrix of `octo-asset-repo-services` red
  (`Format2_ANullInAColumnWithADefault_IsRestoredWithTheDefault`). `StagingCopyRunner.DumpedColumns` now names columns
  only when the target excludes Hidden columns or the source table really holds leftovers; the AB#9837 rules are
  unchanged. Tests: `StagingCopyRunnerTests`, `ArchiveComputedColumnLeftoverBackupIntegrationTests`. Docs:
  `docs/streamdata-write-path.md`.
- AB#9837 Fix: a native dump of an archive whose table still holds the leftovers of computed columns now restores. A
  removed computed column leaves its physical column in the table, with the `{base}__v{N}` versions of its formula
  changes, and a formula change leaves the earlier version behind. The dump copied them, and the restore refused the
  archive ("has no column ... that the dump contains. Nothing was loaded."), so the archive ended Disabled without rows.
  The dump now leaves these leftovers out (`Copy/ArchiveColumnOwnership`, `ArchiveCopyTarget.Ownership`, a warning names
  them). Their values are derived from a formula the archive no longer has, or from an earlier formula, and nothing reads
  them. A column is left out only on positive evidence: it has a computed column's type, and it is a stored version of a
  computed column, or it carries a versioned name, or it maps to no attribute path of the CK type. Every other column the
  definition does not own stays in the dump and is still refused by the restore. That covers undeclared ingested columns,
  `text`/`object` columns, and plain unknown columns while the CK type cannot be read. `EnsureArchiveCreatedAsync` now
  provisions a computed column under its active name (and its pending name during a formula change), not under its base
  name. Before, a restored archive whose formula had changed lacked the column its values live in. Behaviour change: a dump
  made before this fix still carries the leftovers, and its restore is still refused. The refusal now says that the columns
  are leftovers and that a new dump leaves them out. The restore never creates them, because `COPY FROM` cannot skip a JSON
  key. Tests: `ArchiveComputedColumnLeftoverBackupIntegrationTests`, `ArchiveColumnOwnershipTests`, `StagingCopyRunnerTests`.
  Docs: `docs/streamdata-write-path.md`, "Backups leave the leftovers of computed columns out".

- AB#9750 Fix: the count-only form of a nested association query (`GetRtAssociationTargetsAsync` with `take: 0`, the
  form of `children(first: 0) { totalCount }` and, with the asset repository of the same change, of every nested
  selection of `totalCount` alone) counts on the server and loads no target document. Before, it ran the item pipeline
  for one item: every target document went through a per-edge `$lookup` and an unbounded `$setWindowFields`, so a large
  association (many or big targets) could run out of memory like the top-level count of AB#8895. The new sub-pipeline
  matches the edges, probes each target by `_id` in its collection with the archived and field filters inside the probe
  and keeps only `_id`, then ends with `$count`: each origin receives one count document, never an array of targets or
  target ids. The count equals the total of the unpaged item query (one per edge whose target exists and passes the
  filters). Skip and sort do not change it and are ignored. Behaviour change of `take: 0` with a sort order: before, it
  took the sorted paths and reported their total - without a field filter the number of non-archived edges, including
  edges whose target is archived or missing (also the edge count of a dense origin, AB#4369); now it reports the exact
  count, which can be lower. A sorted page (`take` above 0) without a field filter still reports the edge count.
  Dense origins (AB#4369): without a security, id, field or geospatial filter on the targets, an origin with more than
  `ReverseNavigationOptions.EdgeThreshold` edges is answered by the capped, index-only edge count and runs no target
  probe, with or without a sort order. Behaviour change: the `totalCount` of a dense origin without a field filter is
  the edge count and may include archived or missing targets (before, without a sort order it was the exact count);
  with a field filter it stays exact. With a geospatial filter `take: 0` keeps the item path for one item and drops the
  targets, so its count always equals the item query's total (that path does not apply the geospatial filter yet, Bug
  AB#9869). Every other paging (`take` null or above 0) is unchanged. Code: `MultipleOriginDirectAssociationsRtQuery`
  (`CreateCountOnlyAssociationStages`), `TenantRepository.CreateRtAssociationTargetsQueryAsync` (internal: the query
  construction of `GetRtAssociationTargetsAsync`, so tests can render the pipeline). Tests:
  `NestedAssociationCountOnlyTests` (pipeline assertion; parity with the item query for six option shapes, zero, `Any`;
  archived targets; a geospatial filter on the item path; dense origins with and without a field filter, one well above
  the threshold; a batch of dense and sparse origins), `NestedAssociationCountOnlyDataPermissionTests` (a caller with a
  restricted data permission, sparse and dense origin), `NestedAssociationCountOnlyLargeAttributeTests` (twenty targets
  of 1 MB each). The tests that lower the static `ReverseNavigationOptions.EdgeThreshold` (also
  `MultipleOriginDirectAssociationsRtQueryTests`) run in the new collection `ReverseNavigationThreshold` with
  `DisableParallelization`, so no test of a parallel collection runs while the threshold is lowered. CK compatibility: no CK model, API or
  contract change.
- AB#9702 Test: the regression test of the escaped element pattern
  (`ServiceManagedImportRefusalTests.OrdinaryModelWhoseNameIsAPatternForSystemIdentity_LeavesTheSystemIdentityRowsAlone`)
  asserts that both versions of `Sy.tem.Identity` are installed - the second import runs `DeletePreviousVersion` - and
  that the snapshot holds System.Identity element rows; with the old unescaped pattern it fails. The `S.*` case is
  gone (model names allow only letters, digits, `_` and `.`), and the test class disposes its MongoDB client.
- AB#8502 Fix: CK model validation refusals carry engine catalog numbers. `ValidateCkModelAsync` reports a system
  reference mismatch as message 145 (`CkModelSystemReferenceMismatch`: `modelId`, `referencedModelId`,
  `installedModelId`) and a refused dependency set as message 146 (`CkModelDependencyNotResolvable`: `modelId`,
  `detail`) instead of 8492 with a `reason` parameter. The import's `ModelValidationException` texts are unchanged.

- AB#9770 New: the engine refuses changes to synced master data outside the sync. An entity that carries `RtSyncOrigin`
  is changed, replaced, deleted or archived, and `RtSyncOrigin` is set or changed, only by a sync-writer session of the
  source tenant it names (`ITenantRepository.GetSyncWriterSessionAsync`); every other session of every tenant
  repository - GraphQL and REST callers, pipelines, system sessions - is refused with `RuntimeRepositoryException`,
  message 144, `code` = `SYNC_OWNED_ENTITY` (`SyncOriginWriteRules`). A sync-writer session in turn never touches an
  entity without an origin or another source's copy. The rule is folded into the write filters of the guarded funnel, so
  an update or delete that touches no copy costs no extra command; only a miss reads once to tell a refusal from "not
  found". An archive, every replace, an update that writes a linked binary and a bulk-mode insert (an upsert on the id)
  read once per type before anything is written, so a refusal deletes or uploads no GridFS bytes, also without a
  transaction. Member paths of the origin (`RtSyncOrigin.<member>`, any case) are refused like the origin; the
  `attributes: null` repair statement carries the same write filter as its update. A filter write does not select copies
  the session may not touch (silently, without a read). Entities without `RtSyncOrigin` are written exactly as
  before. Not covered: bulk import, `*ForMigrationAsync`, and the repositories of the engine maintenance services
  (`MongoRuntimeRepositoryProvider`, now handing out `TenantContext.GetMaintenanceTenantRepository`). Docs:
  `docs/runtime-write-semantics.md`, "Synced master data". Test: `SyncOriginWriteGuardTests`.
- AB#9770 Fix: an update whose attribute value is a generated record class (any CK model's `Rt<Name>Record`, also
  inside a record list) is stored as a plain record, like the insert of the same entity. The update path writes each
  attribute as a boxed `$set` value (`MongoDataSourceMapper.ApplyUpdate`), and the registered object serializer allows
  only the runtime contract types, so such an update failed with "Type … is not configured as a type that is allowed to
  be serialized". `RtRecordValueNormalizer` copies the value to `RtRecord` / `List<RtRecord>` first; values without a
  generated record are passed through unchanged; the member values of a record array item update go through the same
  step. Tests: `TypedRecordUpdateTests`, `RecordArrayItemUpdateTests`.
- AB#9770 New: opt-in delete events for runtime-entity watches. `WatchStreamFilter.IncludeDeleteEvents` (default
  `false`) keeps every existing subscription exactly as it was: before, a delete never passed the watch (its change
  event has no full document, and the watch matched on `fullDocument.ckTypeId`), and without the opt-in it still does
  not. With the opt-in and `Delete` in `UpdateTypes` a delete event is matched in an own `$or` branch: on the pre-image
  (`fullDocumentBeforeChange`: type set, RtId, field filters and before-field filters) when the collection root has
  `EnableChangeStreamPreAndPostImages` - exact for derived types sharing the collection -, otherwise on the document key
  alone (`documentKey._id` for a watched RtId), which is exact only for a watch on the collection root without field
  filters; any other combination is refused at subscription (`OperationFailedException.DeleteEventsNeedPreImages`).
  `IUpdateInfo<T>.DocumentRtId` (additive default member, `null` in other implementations) carries the RtId of the
  document key for every event; `UpdateInfo` sets it. No consumer must rebuild: implementers keep compiling, and the
  watch only changes for callers that set the opt-in. Tests: `WatchRtEntitiesDeleteEventTests` (real MongoDB).
- AB#8875 Fix: a large raw or time-range archive write no longer runs its 1,000-row statements one after another.
  `CrateDatabaseClient.InsertGroupedAsync` splits the rows of a statement shape into lanes by row key and runs the lanes at
  the same time (`InsertStatementLanes`), so two statements in flight never carry the same key and the rows of one key are
  written in the order they were sent; the conflict guard, the skipped rows (same order) and `MissingRowRetry` per
  statement are unchanged. **New setting** `StreamData:MaxConcurrentInsertStatements` (`StreamDataInstanceConfiguration`,
  default 4, not positive = 1, at most the pool size; environment variable `OCTO_STREAMDATA__MAXCONCURRENTINSERTSTATEMENTS`
  in the services, which read `OCTO_`-prefixed variables, or `StreamData__MaxConcurrentInsertStatements` in a host without
  a prefix). **`StreamData:MaxPoolSize` is now read from configuration** (`StreamDataInstanceConfiguration.MaxPoolSize`,
  environment variable `OCTO_STREAMDATA__MAXPOOLSIZE`): a positive value overrides the pool size the host sets on
  `StreamDataConfiguration`, otherwise the host's value (default 32) applies, for the tenant's data source
  (`CrateDbConnectionAccess`) and for the clamp above. Both keys are bound with the `StreamData` section by
  `AddCrateDbStreamDataRepository`, like `Enabled` and `SchemaInstancePrefix`; `AddStreamDataDatabase` (used by test
  fixtures) does not bind that section, so there the limit is 4 and the pool the host's. Without the keys nothing changes
  but the limit of 4. A write of one statement runs as before. On a failed write no new
  statement starts, the ones in flight finish and the first failure is rethrown unchanged; the set of rows already written
  can then differ from the sequential order. All of them are idempotent upserts (guarded where the archive has conflict
  precedence), so a retry of the same insert converges when no other write of the same keys comes in between; without
  conflict precedence the last write wins, and a retry can replace a newer value that another write stored meanwhile. Tests: `InsertStatementLanesTests` and `MaxConcurrentInsertStatementsBindingTests` (unit), `StreamDataWriteLoss/ArchiveWriteConcurrencyIntegrationTests`
  (real CrateDB, 130,000 guarded rows, both modes compared).
- AB#6209 Test: the contention-heavy AutoIncrement integration tests (20 parallel writers with retry loops, with and
  without transactions, also across the year boundary) are `Category=Stress` (+ `LoadSensitive`) and run in the
  performance pipeline; the regular suite keeps one small concurrency test per criterion (3 parallel creations; 2+2
  across the year boundary; no retry, no timing). The allocation decisions - retry only outside a transaction and at
  most 3 attempts, the first number of a block, the overflow guard, the numbers left from the `StartValue` floor - are
  unit tests without Docker (`AutoIncrementAllocationRulesTests`), and the format and range-rule tests moved to the unit
  test project.
- AB#6209 Fix: a legacy format that does not show the counter (no `{0}`, e.g. `INV`) would give every document the
  same text. It is refused when a range is configured - insert, a replace that changes the format, a partial update of
  the format - with an error that names the range. A range already stored with such a format keeps numbering, and a
  replace that keeps its format (a seed re-apply) is written.
- AB#8675 Fix (review of PR #283): a `PeriodKind = FiscalYear` range that was numbered without periods (`CurrentValue`
  n > 0) and has no fiscal-year period yet needs a `StartValue` above n; its first fiscal year would otherwise start
  at `StartValue` (or 1) and issue numbers again (e.g. `26-{0:D4}` becoming `{yy}-{counter:D4}`). It is a range rule,
  so it is checked on every write of the range (the switch itself, a later change of `StartValue`) and again before
  allocation, which also covers a range written past the validator and a legacy number taken between the check and
  the switch outside a transaction (the allocation then refuses until `StartValue` is above the counter, never a
  duplicate). A legacy format whose token text is escaped
  (`{{yyyy}}-{0:D4}`) stays a legacy format. An entity's numbers are all taken in one fiscal year (its creation time
  is read once). A range switched from FiscalYear to legacy after the first statement and before the read of the
  range is tried again instead of reporting end reached; a switch after that read is refused with "write again". The numbers left
  in `AutoIncrementEndReached` count from the `StartValue` floor, and the message names the refused block only. A new
  period's block whose last number would pass `long.MaxValue` is refused instead of overflowing. Breaking for direct
  callers: the `AutoIncrementModifier` constructor takes `(ICkCacheService, ILogger<AutoIncrementModifier>,
  TimeProvider? = null)`; it is registered by `AddMongoDbRuntimeRepository`, no direct caller in the services.
- AB#8675 Fix: number ranges refuse configurations that would issue a number twice. A token format without
  `{counter}` (e.g. `RE-{yyyy}`) is refused, it would give every document of a period the same number. A
  `PeriodKind = FiscalYear` range needs a token format with `{yyyy}` or `{yy}`: without a format, with a legacy
  `string.Format` format or with a token format without the year, its counter restarts every fiscal year and the numbers
  would repeat, so the range is refused with `InvalidNumberRange` (when it is configured and before any allocation);
  ranges without periods are unchanged. A FiscalYear range numbers only String attributes; an Int attribute is refused
  before anything is allocated. Every allocation statement now also bumps `rtVersion` and sets `rtChangedDateTime` like
  any engine update, so a stale copy of the counter written back with an expected `rtVersion` (outside a transaction)
  is refused instead of setting the counter back. Tests: `AutoIncrementNumberFormatTests`,
  `AutoIncrementFiscalYearTests` (`FiscalYearRange_ForAnIntAttribute_HandsOutNoNumber`,
  `Allocation_BumpsTheCounterVersion`, configuration refusals).
- AB#9755 New: a number range continues from its `StartValue` (System 2.9.0, design note D5). Every allocation takes
  its block from `max(counter, StartValue - 1) + 1` on, with and without fiscal-year periods: a range with start value
  1235 continues an external range that ended at 1234, and a start value set below a counter already in use is ignored,
  so it never produces a duplicate. The floor is part of the single allocation statement (a pipeline update instead of
  `$inc`), so allocation stays one atomic statement per counter, year and batch; without a start value the numbers are
  exactly as before. The range rules refuse a `StartValue` below 1 or above `End`, and `AutoIncrementRangeValidator`
  now also checks partial updates of a range (engine hook `IPreDocumentUpdateValidation`): the stored range with the
  update laid over it must be valid, so an update that sets `PeriodKind = FiscalYear` without a zone, an unknown zone,
  a fiscal-year token without periods or a start value outside 1..`End` is refused before anything is written (it was
  refused only at the next allocation). Updates that touch no configuration attribute read nothing. Tests:
  `AutoIncrementStartValueTests` (MongoDB: start value 1235, start value below the counter, the floor in a fiscal year
  in use, two company tenants under one group tenant in two tenant databases, seed re-apply keeps `CurrentValue`,
  `Periods` and a tenant-changed start value, partial-update refusals).
- AB#9754 New: number ranges per document type and fiscal year with format tokens (System 2.9.0). A
  `System/AutoIncrement` with `PeriodKind = FiscalYear` keeps one counter per fiscal year in `Periods` and restarts at
  `StartValue` (or 1) when a fiscal year starts. The fiscal year is taken from the creation time of each entity (an
  injected `TimeProvider`, read per entity, so a batch across the year boundary numbers each entity in its own year)
  read in the range's IANA `ReferenceTimeZone`, with `FiscalYearStartMonth` (1-12, default 1); it is named by the
  calendar year in which it starts. Allocation stays atomic: one update of the year's entry per counter, year and
  batch, bounded by `End`; the first number of a new year adds the entry with its block in one conditional update
  that matches only while the year is missing, so parallel writers never create it twice. `Format` knows the tokens
  `{yyyy}`, `{yy}` (fiscal year) and `{counter}` / `{counter:D<n>}` with literal text around them
  (`RE-{yyyy}-{counter:D5}`); every other format is passed to `string.Format` exactly as before, and a range without a
  period numbers exactly as before (same single statement, no read). A range whose configuration is not valid is
  refused with `AutoIncrementFailedException` (`InvalidNumberRange`) when it is inserted or replaced (new
  `AutoIncrementRangeValidator`) and again at allocation (a fiscal-year range before a number is taken; a range
  without periods right after its statement, so a refused block of such a range stays unused - a gap, never a
  duplicate): `FiscalYear` without a known IANA zone
  (no UTC default; Windows ids refused), a start month outside 1-12, a token format that does not parse, or
  `{yyyy}`/`{yy}` without `FiscalYear`. Tests: `AutoIncrementFiscalYearTests` (MongoDB, fake clock: restart on
  1 January and 1 July, two types, legacy `{0:D4}`, batch across midnight, 20 parallel creations across the boundary
  with and without transactions, configuration refusals) and `AutoIncrementNumberFormatTests`.
- AB#9753 New: `AutoIncrementModifier` allocates numbers atomically. Before, it read the counters once per batch,
  incremented `CurrentValue` in memory and replaced the whole counter entity, so two parallel writers outside a
  transaction could get the same number. Now one `findOneAndUpdate` per counter and batch takes a block of
  consecutive numbers; its filter holds the `End` bound (`currentValue + n <= end`), so the counter never passes `End`
  (`AutoIncrementEndReached`, also when the batch needs more numbers than are left: the batch is refused as a whole,
  nothing is taken, and the message names the range, the numbers needed and the numbers left) and `AutoIncrementReferenceNotFound` is raised as before when there is no counter. The counter is neither
  read first nor replaced. Numbers follow the batch order (with the engine's ordered `EntityRuleEngine`). The statement
  runs in the caller's session: inside a transaction it is rolled back with it, and a parallel writer of the same
  counter aborts the transaction with `TransientTransactionError`, which is passed on unretried for the transaction
  owner to run again; outside a transaction the statement runs at most 3 times (the first attempt and up to 2
  retries) on that label or a write conflict
  (committed at once, so a later failure leaves a gap). Tests: `AutoIncrementModifierTests` (20 parallel creations
  with and without transactions, injected and real transient errors, rollback, block over `End`, one findAndModify
  and no replace on the counter).
- AB#6209 Fix: `AutoIncrementModifier` numbers every new entity of a mixed batch. Before, the first type or entity
  without an auto-increment attribute ended the pass (`return`), so in a batch such as `[Note, Ticket]` the Ticket got
  no number, depending on the order. Because the pre-document modifications run on insert and on replace without
  telling them apart, the modifier now reads the stored entities of the batch's auto-increment types by RtId (one read
  per type, only when the batch has such a type): a stored entity (replace, bulk import of an existing entity) is never
  renumbered and the counter does not move; when its payload has no value, the stored number is carried over. A new
  entity is numbered only for an attribute without a value, so ImportRt/restore values are kept and do not advance the
  counter. The stored-entity check is one helper, `StoredRtEntities.GetByRtIdAsync` (read by `_id` in the session
  of the write), for the later number allocation stories to reuse. A batch without auto-increment types reads
  nothing extra. Two new entities of one batch get consecutive numbers (in batch order since AB#9753). The version-less AutoIncrement lookup (D-G1-1) stays. Tests:
  `AutoIncrementModifierTests` (MongoDB).
- AB#9524 Fix: the index update resolves index attribute paths against the inheritance chain of the index-defining type
  (its own attributes and those of its base types, the chain the update already loads; the type's own assignment wins
  over an inherited one of the same name), not only against the attributes the type assigns itself. An inherited plain
  attribute such as `Name` (Basic.Energy `EdaProcess`) no longer logs "Could not resolve attribute path ... using
  fallback" on every index update; its key spec is byte-identical to the fallback (`attributes.` + camelCase), so no
  index is rebuilt. An inherited record path (`TimeRange.From`) now resolves to the stored path
  (`attributes.timeRange.attributes.from`) and an inherited array path (`Tags[*]`) to `attributes.tags`; the fallback
  of before (`attributes.timeRange.From`, `attributes.tags[*]`) pointed at a field that is never stored, so a tenant
  with a custom model that indexes an inherited record or array path gets a one-time rebuild of that index (no shipped
  model does: every dotted index path in the monorepo's models is on the type's own attribute). The Secret guard
  (AB#5533) and the Hidden backstop consider inherited assignments the same way. A path that still does not resolve
  logs the warning and falls back as before. Known limitation: the merged text index is defined on the collection root
  and takes fields of descendants; those still resolve against the root's chain and may fall back. Code:
  `IndexAttributePaths`. Tests: `IndexAttributePathsTests` (unit), `InheritedIndexAttributePathTests` (MongoDB).
- AB#9325 Fix: the caller overload `ITenantContext.ImportCkModelAsync(CkCompiledModelRoot)` - behind every caller-initiated
  CK import (bot `ImportCk` / `ImportCkBatch` jobs, whoever queued them) - refuses every service-managed model (`System`,
  `System.*`, case-insensitive, trimmed) in every version, also when the tenant does not have it, before anything is
  written: `ServiceManagedCkModelImportRefusedException` with code `CK_SERVICE_MANAGED_MODEL` (the asset repository's REST
  code), WARN log, counter `octo.ck.explicit_import.refused`. Operators lose the explicit System downgrade of decision Q5:
  the owning service's embedded import (restart, `ClearCache`, `ReRunTenantSetup`) only brings in a missing or older
  version; a damaged model of the same version or a too new one needs a manual database repair. Non-System models
  keep the explicit downgrade with WARN. The element delete of an import (`DeletePreviousVersion`,
  `PreserveExtensibleEnumValues`) escapes the model name in its pattern (`ModelElementsPattern`): before, a caller
  import of an ordinary model named `Sy.tem.Identity` (`.` is the only regex metacharacter a model name may hold)
  deleted the element rows of System.Identity. New public predicate `ServiceManagedCkModels.IsServiceManaged` (single source;
  the tenant upgrade guard uses it). No `ITenantContext` member changed. Tests in other repositories that installed a
  System model through the caller overload must load it into a test catalog and import it by id (consumer PRs merge
  first).
- AB#9302 Fix: a CK import no longer waits for the whole tenant of a running tiebreak index upgrade: the upgrade releases
  the lock between collections and lets a waiting import take it. This reduces contention; it is no bound - the import
  keeps its 90 s wait and can still time out when one collection's builds, or several sections before it gets its turn,
  take longer. `UpgradeTiebreakIndexesAsync` holds `index_update_lock` per collection instead of for
  the whole tenant: each lock section re-reads the CK metadata, upgrades the next collection root not processed in this
  run (also one an import created meanwhile) and releases the lock. A waiting index update (CK import,
  `UpdateIndexesAsync`) keeps a short-lived waiter marker in `SysLock` (opt-in per lock, only `index_update_lock`; every
  other lock is unchanged); the upgrade gives it the chance to take the lock after the current collection (a yield of up
  to 3 s, not a guaranteed turn) and then waits up to one hour for the lock itself. Measured locally: 2.1 s for a
  collection of 1,000,000 documents with one legacy index. A section never drops or replaces an index of a collection
  holding a type of a model that is still being imported: such a collection is retried after the others and otherwise
  left for the next run (warning). Cancellation stops before the next collection; a lost lock, once detected (heartbeat,
  every 15 s), aborts the current collection and persists no more index states - also in the normal index update, which
  before stopped only between collections; the index-state write checks the loss again after reading the stored
  states and before each type it writes. A lock acquire never polls after its deadline (a delay or a slow waiter-marker
  write that overruns it ends in the `TimeoutException`, not in a late acquire), and it fails with the cancelled
  `LockLostToken` (`OperationCanceledException`) instead of returning success when the heartbeat found the lock taken
  while the waiter marker was being cleaned up. The normal index update keeps its whole-tenant lock; the public API is
  unchanged. Per-collection start/finish logs (information) and lock acquire/release (debug) name tenant and collection.
  Index states (AB#9391 follow-ups): a failed build is named after every definition of the collection was processed, so
  a failure under the alternate name is visible in the same run in either order, also when a legacy index carries the
  tiebreak name; the index a failed build would have replaced stays a candidate for the later definitions of the run;
  `ClearIndexStatesForCollectionAsync` writes only `IndexStates` (`$set`); the slot-name check of the holder log accepts
  only engine slot names. Tests: `TiebreakIndexUpgradeLockTests`, `TiebreakUpgradeSectionPerformanceTests`
  (`Category=Performance`), `RepositoryDistributedLockServiceTests`, `RepositoryDistributedLockServiceDeadlineTests`,
  `SharedIndexKeySpecTests.IndexStates.cs`.
- AB#7212 Test: `DataPermissionHiddenEqualsMissingTests` pins on real MongoDB that, for a caller with restricted read
  (owned-only for every action, Write and Delete for all with Read owned-only, or Write and Delete without Read), a
  hidden record answers exactly like a missing rtId (update, replace and delete, valid and invalid payload), also when
  it is addressed under a sibling type of the shared Location collection (then it is also not written, checked without
  a transaction; before the engine change a valid update through the sibling type wrote the hidden record) and when it
  is deleted by the abstract Location type (engine PR 383: a delete by an abstract type of a missing rtId answers not
  found instead of returning). The caller's own visible record with the same invalid payload still answers the
  validation error. No code change in this repository; the test needs the engine version that contains PR 383.
- AB#9594 Chore: NuGet packages raised to the newest versions within the .NET 10 line: MongoDB.Driver and MongoDB.Bson
  3.14.1 (same as the engine), Npgsql 10.0.4 (11.x moves with the .NET 11 line, AB#9580), Dapper 2.1.100, Polly.Core
  8.9.1, Snappier 1.3.2, Microsoft.OpenApi 2.13.1, NLog.Web.AspNetCore 6.3.0, Swashbuckle.AspNetCore.SwaggerUI 11.0.1,
  test tooling (Test.Sdk, xunit.v3, runner, coverlet, FluentAssertions, Testcontainers.MongoDb). No behaviour change.
- AB#9561 Fix: a failed transaction commit is no longer masked by "Cannot call AbortTransaction after calling
  CommitTransaction". After a commit attempt `OctoSession` never aborts the transaction (`AbortTransactionAsync` and
  `Dispose` skip the driver call), so the original commit exception (a `w:majority` wtimeout, a write concern error, a
  cancelled token) reaches the caller unchanged, also through `try { ... Commit } catch { Abort; throw; }` blocks such as
  `TenantContext.EnableStreamDataAsync`. After a failed commit the session is no longer active. Tests:
  `OctoSessionTransactionCallbackTests`.
- AB#8897 New: a runtime-entity query that runs too long is stopped by MongoDB (`maxTimeMS`) and ends with
  `RuntimeQueryTimeoutException` (tenant, CK type, limit, elapsed time) instead of keeping the database and the service
  busy. The limit applies to each MongoDB operation of a query (page and count of a paged query each get the full
  limit). **Opt-in per host:** `AddRuntimeQueryTimeLimit()` on the runtime engine builder; the host binds
  `RuntimeQueryOptions` (section `RuntimeQuery`, `MaxTimeMs`, default 120,000 ms, 0 = off). Without the call nothing
  changes. Covered: the queries by type, by id, by type with navigation and by CK interface, and their counts
  (`SingleOriginQuery`, `RtInterfaceQueryExecutor`), also the hinted deep page and the capped count of AB#9138. Not covered yet: association and deep-graph queries
  (`MultipleOrigin*`). A query that completes after more than 80 % of its limit is logged as a warning (tenant, CK type,
  duration, limit). Batch paths override the limit at the call site: `using (RuntimeQueryTimeLimit.Override(TimeSpan))`
  or `using (RuntimeQueryTimeLimit.Disable())`, an `AsyncLocal` scope that holds across `await` and for the tasks started
  inside it and falls back to the previous limit on dispose; the CK migrations of `TenantContext` and the blueprint of a
  new child tenant run inside `Disable()`. Tests: `RuntimeQueryTimeLimitTests` (unit),
  `RuntimeQueryTimeLimitIntegrationTests` and `InterfaceQueryTimeLimitTests` (real MongoDB, including a query MongoDB
  stops with `MaxTimeMSExpired`).
- AB#9391 Fix: the index states of the CK types (`CkType.IndexStates`) name the indexes that exist. At the end of
  every collection run of the index update and the tiebreak upgrade the states of all types of the collection are
  reconciled with the final index list (when that list cannot be read, a warning is logged and the run persists only
  the states it tracked; the next run that reads the list reconciles): an `Applied` state under an index that no longer exists is removed (the cleanup
  dropped a legacy index without removing its states, also when an earlier upgrade was aborted before it persisted them),
  and every index that serves a definition is recorded `Applied`, also one found under its expected name and the legacy
  index that still serves a definition whose tiebreak build failed.
  A `Failed` tiebreak build of the upgrade stays visible through later CK imports that keep the legacy index (a normal
  update removed it) until an index in the target shape serves the definition; a `Failed` state no current definition
  expects is removed after a completed run. A failure never overwrites the `Applied` state of an existing index of the same
  name any more (two definitions of one type whose existing indexes hold each other's keys): it is recorded under the
  alternate name of its slot, which stays expected while a legacy index serves the slot. An index update or upgrade that
  changes no state writes no CkType document (before, every CK import rewrote the CkType documents of types served by a
  kept, shared or adopted index); an unchanged state keeps its `AppliedAt`, and a change writes only the `IndexStates`
  field instead of replacing the CkType document. The log line of a legacy index built for another type names that type
  (by the index's slot name, else by a stored state), otherwise `(unknown)`. No index is built or dropped differently; no
  CK model, index shape, name or public API change; `CkIndexState` unchanged. Existing tenants converge on the next index
  update that reads the final index list. Tests:
  `SharedIndexKeySpecTests.IndexStates.cs`.
- AB#9342 Fix: two CK types of one collection with an Ascending index on the same attributes share one index. Both
  produce the key spec `{ckTypeId, attr..., _id, rtState}` and MongoDB holds one non-unique index per key spec, so the
  index update of the second type failed with MongoDB 85 ("Index already exists with a different name"), logged a warning
  and stored a `Failed` index state on every update; on a collection with one legacy index shared by both types the
  normal update built the tiebreak index of one type next to the legacy index of the other, which the tiebreak upgrade
  (AB#8960) could then never replace (`UpgradeTiebreakIndexesAsync` left the legacy index and ended incomplete, seen on
  test-2 `octosystem`). The index update now dedupes per collection on the key spec: the index that holds it (existing,
  or built earlier in the same collection run, also a legacy index kept without the upgrade) serves every type with
  those keys, whatever the order of the types - no createIndexes call, state `Applied` under the shared index name, one
  information line per type. On a collection that holds both the legacy index of one type and the tiebreak index of the
  other, an update without the upgrade now drops the legacy index after the other indexes (the tiebreak index serves
  both types, nothing is built), as it already did for a single type whose tiebreak index exists. Unique and UniqueNotDeleted indexes keep their per-type filter, text
  indexes are merged per collection as before; an index with foreign options (sparse, hidden) is still kept and
  reported. Existing tenants converge on the next index update or upgrade; no CK model, index shape or name changes.
  Side fix: a removed index state (an index replaced, dropped or no longer serving the type) is now removed from the
  stored states of the CK type too, not only from the states tracked in the run, so a `Failed` state an earlier run left
  disappears once the type is served; an index the run recorded as `Applied` for the type keeps that state even when a
  later definition of the type expected another index under its name (two definitions whose existing indexes hold each
  other's keys). Tests: `SharedIndexKeySpecTests`.
- AB#9250 Test: the integration test containers get a CPU limit next to their memory limit (mongod 4 CPUs, CrateDB 2 CPUs
  each; the CrateDB heap is set explicitly for every CrateDB fixture), xUnit runs at most 8 collections at once
  (`maxParallelThreads: 8`), and the CrateDB pool tests wait for their container and name it when it does not answer instead
  of failing with a bare `BrokenCircuitException`. Test project only; the test README describes the local run on Windows
  (Docker Desktop VM of at least 20 GiB / 12 CPUs).
- Fix: a restore or a backup with detach that throws after its drop or detach was committed and whose tenant-deleted
  notification failed now keeps the warning ("... run ClearCache for the tenant") in `CommandResult.Output` of its failure
  result. Before, the failure result of the throw carried only the exception message and only the WARN log named the
  recovery. A throw without a failed notification gives the same failure result as before.
- Fix: the tenant-deleted notification (`PosDeleteTenant`) of a child-tenant drop or detach is published only after the
  removal is committed. `DetachChildTenantAsync` and `DropChildTenantAsync` published it inside the caller's transaction,
  so services could tear down a tenant whose removal was later rolled back, or act before the removal was visible. Inside
  a caller's transaction it now runs as an after-commit callback (nothing on a rollback), with the same three bounded
  attempts as the tenant-created notification and then a warning naming the tenant and the recovery (ClearCache for the
  tenant, or a service restart); without a transaction it is published at once as before, and the two-phase delete
  (`DropTenantDatabaseAsync` after the committed metadata delete) is unchanged. The backup's detach and the restore's
  drop of an existing tenant publish it themselves after their commit; a failed publish keeps them successful and adds
  the warning ("... run ClearCache for the tenant") to `CommandResult.Output`. A clear in a transaction now publishes
  delete, create, update after the commit. The internal `TenantCreatedNotificationScope` is now `TenantNotificationScope`
  and collects both notifications. Details: `docs/tenant-lifecycle-stores.md`, "Tenant-deleted notification after the commit".
- AB#9177 Fix: the tenant-created notification (`PosCreateTenant`) of a child-tenant create or attach is published only
  after the registration is committed. `CreateChildTenantAsync` and `AttachChildTenantAsync` published it inside the
  caller's transaction, so every service set the tenant up before it was registered (a restore logged "Setup tenant
  failed" on every service, recovered later by the setup retry). Inside a caller's transaction the notification now runs
  as an after-commit callback (nothing on a rollback), with three attempts (250 ms, 1 s apart) and then a warning naming
  the tenant and the recovery (ClearCache for the tenant, or a service restart); without a transaction it is published at
  once as before. `ClearChildTenantAsync` inside a transaction publishes its `PosUpdateTenant` when the transaction ends,
  after the tenant-created notification. The restore (`RestoreTenantAsync` with attach) publishes it itself after its commit
  (`TenantCreatedNotificationScope`); a failed publish keeps the restore successful (the data is restored and attached) and
  is reported on the result: new `CommandResult.TenantCreatedNotificationFailed`, the warning ("... run ClearCache") in
  `Output`, a WARN log; `CloneTenantToTempAsync` passes the flag on. Every publish attempt after a commit, the restore's
  included, is bounded to 5 s. A tenant the same transaction detached or dropped again gets no tenant-created
  notification (recorded per session, no registry read needed). Details: `docs/tenant-lifecycle-stores.md`, "Tenant-created notification after the commit".
- AB#9127 Fix: a tenant dump (`TenantBackupService.BackupTenantAsync`, used by the bot's dump job, the pre-sweep dump and
  the clone to a temp tenant) leaves out platform-only collections, today `RtEntity_SystemCommunicationSecretDefault`
  (`MongoDumpOptions.PlatformOnlyTenantCollections`, `ForTenantArchive`, one `--excludeCollection` each). The secret
  defaults the communication controller projects into a tenant are platform-only and must never reach a file a tenant
  identity can read; the controller projects them again after a restore (AB#7245). `MongoDumpOptions.ExcludeCollections`
  is new and empty by default, so `ForArchive` and every other dump are unchanged.
- AB#9138 Fix: a deep sorted page of a paged runtime-entity query no longer spends its time in MongoDB's plan trial.
  Since AB#8896 the page (`$match`, `$sort`, `$skip`, `$limit`) is planned as a whole; for a deep page none of the
  candidate plans returns results early, so every request ran all candidates through the trial (909,000 entities, skip
  50,000: 56-67 s, execution ~90 ms) and the plan cache never kept the winner. The page read is now hinted to the index
  that provides its sort (`SortIndexHint`: `ckTypeId` followed by the sort keys, same or reversed directions; never a
  partial, sparse, hidden, collated, text, hashed or 2dsphere index), from skip `PagingIndexHintMinSkip` on (**new
  setting**, default 1000). When the filter has an index of its own the page is hinted only with at least
  `PagingIndexHintMinMatches` (**new setting**, default 50,000) matches, counted exactly when the total is asked for (the
  count then runs before the page and is not repeated) or with a capped count. No hint inside a transaction, for
  unsorted pages, for pipelines with stages around the match (`$geoNear`, association filters) and when no index
  provides the sort. The indexes come from `CollectionIndexCache` (one `listIndexes` per collection and process every 5
  minutes, invalidated by this process's index updates); a hint naming a dropped index is retried once without it. New
  `IMongoDbDataSourceCollection.Aggregate` overload with `AggregateOptions`. Measurements: `docs/runtime-query-paging.md`, "Deep sorted
  pages: the index hint".
- AB#9110 Fix: paged stream-data queries return every row exactly once, also without a sort order. The simple query
  (`ExecuteQueryAsync`, transient and persistent `rows`) ran LIMIT/OFFSET without ORDER BY when no sort order was given
  (`AddOrderByTiebreaker` did nothing without an existing ORDER BY), and with a sort order only `timestamp` broke ties.
  CrateDB returns the rows of a sharded table in any order per call, so the pages of a multi-node cluster repeated some
  rows and skipped others (FH VSCT, 3.5.71: 103-122 of 192 rows unique). `CrateQueryBuilder.EnsureDeterministicOrder`
  (replaces `AddOrderByTiebreaker`) appends the row key, ascending, to every page query: raw archives `timestamp, rtid,
  cktypeid`, windowed archives `window_start, window_end, rtid, cktypeid`. Without a sort order that is the default order
  (time axis, then rtId); with one, the key columns the caller did not name break its ties, in the caller's direction
  where the caller named them. The grouped aggregation (query builder and LOCF statement) now orders by its group-by
  columns; the GraphQL connection pages its groups by running it again per page. ORDER BY renders a column the
  way GROUP BY does (`QueryVariable.ToOrderByString`), so `row_origin` / `manual_count` are ordered by their
  `COALESCE(...)` expression, not by the bare name. Downsampling already ordered by bin,
  then series. **Behaviour change:** rows that had no defined order now come in the default order. **Cost:** measured on
  2,880,000 rows (3 shards, single-node test container, median of 5): first page 7 -> 17 ms unfiltered, 9 -> 8 ms for one
  day; deep page 28 -> 2650 ms unfiltered (offset 2,879,900), 10 -> 57 ms for one day (offset 95,900). Accepted by Odin
  (correctness first). Tests: `CrateQueryBuilderTests` (`EnsureDeterministicOrder_*`), `TimeWeightedQuerySqlBuilderTests`,
  `StreamDataPaging/StreamDataPagingIntegrationTests` (real CrateDB, 4 shards, 8 anchors on the same timestamps / windows,
  pages of 50 three times: raw and time-range, without sort order and with a sort on an all-ties column, full key order
  checked over all pages; grouped aggregation on both, and the LOCF path with TimeWeightedAverage on the raw archive),
  measurement `StreamDataPaging/StreamDataPagingOrderCostMeasurement` (`OCTO_PAGING_COST_DAYS`).
- AB#8960 Fix: paging through runtime entities returns every entity exactly once, also without a sort or with a
  sort key that is not unique. Before, `$skip`/`$limit` ran over an undefined order of the ties: 300 entities with the
  same `Name`, paged by 7, came back as 300 items of which only 35 were distinct. The page pipeline of
  `SingleOriginQuery` (and the enrichment read of the page) ends its `$sort` with `_id` in the direction of the last
  sort key unless the caller's sort already contains `rtId`; without a caller sort it sorts by `_id` ascending (a
  `$geoNear` or a text search without a caller sort keeps its own order). **Behaviour change:** an unsorted paged query
  is ordered by rtId, ties of a sorted paged query by rtId.
  **Index shape (new):** CK Ascending indexes are created as `{ckTypeId, attributes..., _id, rtState}` under the name
  `<Type>_<n>_id`, so that they provide the order of a page; unique, UniqueNotDeleted, text and system-level indexes are
  unchanged. Existing indexes in the legacy shape `<Type>_<n>` are **kept** by CK imports and `UpdateIndexesAsync`;
  the new `ITenantContext.UpgradeTiebreakIndexesAsync` (default interface method) rebuilds them per tenant - the new
  index is built before the legacy one is dropped, idempotent. `PagingTiebreakGate` leaves out the tiebreak only for a
  page sorted by keys whose order a legacy Ascending index provides completely, or that start with all keys of a CK
  Unique index (the tiebreak would read every matching document per page: measured 2 ms to 113 ms for the first page over 100,000 entities); it decides
  from the collection's indexes, read through the `CollectionIndexCache` of AB#9138 (5 minutes, one invalidation); a deep
  sorted page with the tiebreak is hinted only to an index that provides `_id` after the sort keys. Unsorted pages and pages sorted by other attributes
  get the tiebreak right after the deploy, also on existing tenants. Unpaged queries, the association navigations and
  the cached navigation path are unchanged; interface queries already broke ties by rtId (AB#7019). Rules and
  measurements: `docs/runtime-query-paging.md`, "Page order of runtime queries".
  **Index updates without a gap (AC 8, AC 13):** an index whose definition changed is no longer dropped before the new
  one is built. When a CK update gives an index slot other keys, its expected name is still held by the old index
  (MongoDB can neither rename an index nor build one with other keys under a taken name); the new index is built under
  `<expected name>_<6 hex digits>` (the digits from the SHA-256 of the definition, so a repeated update finds the same
  name), and the old one is dropped at the end of the collection, after the build. If the build fails, the old index
  stays (also when it is the slot's index under its alternate name). A text index is still dropped before its
  replacement is built (a collection holds only one), whatever its name. An index with the right definition under another name is kept as it is (it used to be dropped and rebuilt
  under the expected name); definitions compare their keys in order (several field groups are one key list), and a
  Unique or UniqueNotDeleted index must name exactly the definition's types in its partial filter - after a descendant
  was added it is replaced like an index with other keys. **Pre-existing:** a text index never matched its definition
  either (the default language `en` was compared with a missing one) and was dropped and rebuilt on every import. A legacy index leaves the tiebreak out only when it provides
  the complete caller sort. An index with options the engine never sets
  (collation, sparse, hidden, TTL, a partial filter on a non-unique index, a unique index without exactly the engine's
  type and state filter) is never taken for a CK index; if it blocks
  our build, it stays (warning, ours tracked as failed). **Pre-existing bug fixed:** `GetIndexListAsync` read every
  partial unique index as UniqueNotDeleted, so a CK Unique index never matched its definition and was dropped and rebuilt
  on every CK import; UniqueNotDeleted is now only an index whose partial filter has a condition on `rtState`. The paging gate counts
  an Ascending index as legacy only when it starts with `ckTypeId` (an engine-made index), so a hand-made index whose
  default name matches the CK name pattern gets the tiebreak. **Naming rule:** a CK index is `<Type>_<n>` / `<Type>_<n>_id`, or the same with
  `_<6 hex>` after an update of its slot; the name is not a contract, the paging gate and the tiebreak shape are
  decided from the keys (the gate only recognises CK indexes by the name pattern `..._<n>` with the optional hash).
- AB#8896 New: a paged runtime-entity query loads at most one page of documents. `SingleOriginQuery.ExecuteQuery`
  no longer puts count and page into one `$facet`, which pushed every matching document through the server and sorted
  the whole set in memory. A paged query now runs a page pipeline (`$match`, the association stages that navigation
  filters need, one `$sort`, `$skip`, `$limit`; the enrichment `$lookup`s afterwards on the page only) and a separate
  server-side count (the `$match` + `$count` pipeline of AB#8895, no document returned). Page and count are two reads
  on the caller's session, one after the other; outside a transaction they are not one snapshot. Items, order and
  total count stay the same (page and count parity tests per query shape and per data-permission caller). New
  additive overloads `ITenantRepository.GetRtEntitiesByTypeAsync` / `GetRtEntitiesGraphByTypeAsync` with
  `includeTotalCount`: `false` runs no count at all and returns a lower bound as `TotalCount` (skip + returned
  entities, 0 for an empty page). Exception: a paged graph query that uses the query result cache (Filter mode with
  navigation pairs) returns the exact total of the cached id list, also for an empty page beyond the cached ids.
  The existing overloads always count and are unchanged for every caller. A paged
  query with `take` 0 returns no entity without reading a page (it used to fail with `$limit` 0).
- AB#8993 Test (deferred review finding of #249, applied with AB#9016): `DownsamplingManyIdsPerformanceTests.CreateSeededArchiveAsync`
  drops the archive table when the seed or its `REFRESH TABLE` fails, so a failed seed leaves no partial multi-million-row
  table in the shared CrateDB container. A failing cleanup is reported and does not replace the seed's error.
- AB#9016 New: a late stream-data delivery refreshes the rollups only for the series it touched, and in place.
  `CrateDbStreamDataRepository` records the distinct rtIds of the late, in-reach automated rows on the dirty window. Above
  `StreamData:Recompute:MaxScopedRtIds` (**new setting**, default 200) it records none, which means every series.
  `MongoArchiveRecomputeStateStore` / `MongoRecomputeJobStore` persist the sets (System.StreamData 1.26.0).
  `CrateDbArchiveRecomputeExecutor.ExecuteScopedRefreshAsync` (the repository delegates to it) stages the buckets for those
  series only (`rtid IN (...)` in the aggregation, the expected-row count and the LOCF carry). It then upserts the staged
  rows into the live table at the generation the readers select for each (window, rtId)
  (`GenerationFilterSql.RenderActiveGeneration`, the same CASE as the read predicate). No new generation, no pointer flip,
  no generation sweep. A bucket row of the series whose source rows are gone is removed, as a full recompute removes it:
  rows at the active generation older than the refresh marker, the `rtchangeddatetime` of the oldest staged row. They are
  counted first and deleted only when there are any. A slice CrateDB stores short is written once more, then the refresh
  fails (AB#8680 rule; the check counts only rows newer than the marker). The recompute
  preparation is shared with `ExecuteAsync` (`PrepareRunAsync`); the full recompute is unchanged.
  `RecomputeLateDataOptions` (`LateDataDebounce` default 5 min, `MaxScopedRtIds` default 200) is bound from
  `StreamData:Recompute` in both registration paths. `TenantContext` hands it to the orchestrator. Needs the engine
  release of AB#9016: this package builds only against it (merge order, §2.4a: engine with or before this repository).
  Tests: `LateDeliveryScopeTests` (unit), `StreamDataLateDelivery/ScopedRefreshEquivalenceTests` (real CrateDB: scoped
  refresh and full recompute give the same reader view for every series on an hourly + daily ladder, TimeWeightedAvg
  carry bucket included; no pointer added).
- AB#8993 Test: performance budget for downsampling over many ids. `DownsamplingManyIdsPerformanceTests` (trait
  `Category=Performance`, runs only in the performance pipeline) seeds a time-range archive of 3500 entities with 15-minute
  windows into a CrateDB testcontainer and requires every downsampling call over 400 ids and 7 days to finish in under 5 s
  (limit 800 and 200, a relative window off the window grid, 10 parallel calls of 350 ids), with every bin complete.
  Measured locally: 734 ms cold, 22-58 ms warm, 10 parallel calls 409-455 ms each. The analysis behind it (prod-2 tenant
  `sbeg`, 20-65 s reported) found that the SQL shape is not the cost driver: the IN list, the `DATE_BIN` GROUP BY, the §7
  containment and the phase snap take ~50 ms and do not grow with the history outside the window. The SQL is unchanged. The
  resolver in the engine now prefers the hourly rollup for such windows (meshmakers/octo-construction-kit-engine#370,
  `MinRollupPoints`).
- AB#8947 Fix: resolving a CK model outside a transaction no longer queries the lock collection (`SysLock`) on every
  call. Since AB#8677 `DatabaseCkModelRepository.IsExistingAsync` asked for the running model imports first, and it
  runs on every tenant lookup of every request (`SystemContext.TryFindTenantContextAsync` ->
  `IsSystemTenantExistingAsync` -> `IsCkModelSatisfiedAsync`): one extra MongoDB `find` per request in every service
  (test-2: ~2 M generated spans per day). The held import locks now come from `CkModelImportLockCache`: one answer per
  tenant database and process, reused for at most 2 s, shared by concurrent readers (one query in flight, cancelled
  after 30 s), and dropped at once when this process acquires or releases a model import lock. When the resolve finds
  no matching version, it asks the lock collection directly, waits for a running import of the model as before
  (AB#8677), and reads the model once more (the import may also have finished in between). A cached answer that
  predates an import in another process, or by a service on an older package during a rolling deployment, therefore
  does not decide a miss; the importer itself does not wait. What remains is the race of AB#8677: the lock check and
  the model read are separate statements, so an import that starts between them can still make a reader see no
  Available version, or read elements while they are replaced (the cache load retry or the caller's retry applies).
  Bound: at most one `SysLock` read per tenant database, process and 2 s for resolves that find their model (at most
  43,200 per day and pod per busy tenant database, far less with normal traffic), plus one `SysLock` read and one more
  `CkModel` read per resolve that finds none. The wait loop, the CK cache load retry and the import's failure
  classification still read the lock collection directly. No interface change.
- AB#8992 Fix: the window-phase probe of a time-range downsampling query (AB#8962) no longer exhausts the tenant's
  CrateDB connection pool. On prod-2 (tenant sbeg) a MeshBoard line chart sent ~10 parallel downsampling queries on a
  time-range archive without rollup; every query ran the probe first under the resilience pipeline (retried, 30 s) and
  the pool of 32 ran dry. Now:
  - the probe runs once, outside the resilience pipeline (no retry, no circuit-breaker sample), through the new
    `IStreamDataDatabaseClient.QueryRowsOnceAsync` (default interface implementation for other implementers; none exists
    outside this repository); its wait for a pooled connection and its statement share one budget;
  - the query waits for the probe at most `StreamData:DownsamplingPhaseProbeTimeout` (**new setting**, default `5 s`),
    then logs one warning and goes on with the unsnapped axis starting at `From` (the AB#7659 behaviour);
  - a single phase is remembered per archive table, window length and filter set (ckTypeId, rtIds, field filters; the
    time bounds are not part of the key) for `StreamData:DownsamplingPhaseProbeCacheDuration` (**new setting**, default
    `00:05:00`, `0` switches the cache off). At most one probe runs per key; one its query stopped waiting for keeps
    running for at most 30 s (one pooled connection) and fills the cache for the next query. A query of another range
    takes only a single phase from a running probe (checked like a remembered phase, see below) and otherwise probes
    its own range after it. With the cache off
    every query probes and the probe ends with the wait;
  - the remembered phase ends when this process drops or (re)activates the archive's table and when it writes (ingest
    or import) a time-range window of another phase into it; a probe that answers after such a write is not
    remembered. With one window per bin, an empty result on a remembered phase (or one taken from another range's
    probe) evicts it, probes this range once more and runs the query again when the windows sit at another phase.
  Residual risk: the cache key has no time part, so a phase probed in one range is used for other ranges of the same
  filters. When a series' windows sit at different phases in different periods (for example after its delivery changed
  without a table drop), a query with several windows per bin (k > 1) can drop at most 1/k of that series' windows in
  the range whose phase differs. This can last longer than one cache duration, because a later probe of the other range
  can remember its phase again. At one window per bin (k = 1) such a query returns no row, and the empty-result check
  recovers it (evict, probe this range, run again). Not changed: the downsampling query and the bucket-count probe of raw archives still have no
  cancellation token (`IStreamDataRepository` is in the engine), so an abandoned request still finishes its main query.
- AB#8962 Fix: downsampling a time-range archive over a window that does not start on a stored window start (a
  relative "last N days" ending at now) returns the stored values again. The bin axis started at the raw `From`
  (AB#7659), so every 15-minute bin straddled two stored windows and the fully-contained predicate dropped all of them:
  resolution-aware line charts on raw time-range archives stayed empty until the resolver switched to a rollup
  (~9 days). The engine now probes the phase of the stored windows in range (`window_start` modulo the window length,
  `MIN`/`MAX` over the same source filters). When they share one phase, it snaps the origin down to the last window
  start at or before `From` (windows at :05/:20/... keep their phase). The first bin may start up to one window before
  `From` and the axis may have one bin more, as on rollups. Requests that already start on a window start, rollups and
  raw archives are unchanged. With no window in range, or with series stored at different phases, the axis starts at
  `From` as before; there, windows of a phase other than `From`'s that straddle a bin edge are still dropped. The fix
  adds one cheap aggregate query per time-range downsampling. The grain route also normalises `From`/`To` to UTC
  (`ToUniversalTime`, as the SQL literals do) before it computes the bin count.
- AB#8895 New: a runtime-entity query that asks only for the total count counts on the server without loading any
  document. `ITenantRepository.CountRtEntitiesByTypeAsync` (type query) and `CountRtEntitiesGraphByTypeAsync` (type
  query with navigation pairs, as persistent queries use) run `$match` - plus the geospatial stage and the association
  stages that navigation filters need - followed by `$count`; no sort, no `$facet`, no enrichment `$lookup`, no entity
  is materialised. The count equals the `TotalCount` of `GetRtEntitiesByTypeAsync` / `GetRtEntitiesGraphByTypeAsync`
  with the same options, data permissions of the caller included (count parity tests per query shape). Additive: two
  new interface members, implemented here; existing queries are unchanged. Before, an unpaged query that only needed
  the count loaded every matching document (ASSET1002 OUT_OF_MEMORY on large entities, AB#8793).
