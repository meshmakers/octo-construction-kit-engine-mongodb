---
description: Data-permission checks, deep graph permissions, AuditOnly read violations and the interface query
applies_to: src/Runtime.Engine.MongoDb/Repositories/Query/**, src/Runtime.Engine.MongoDb/RuntimeQueries/**
---

### Public data-permission check agrees with the repository

The engine exposes `IRtDataPermissionEvaluator` (see the engine `octo-construction-kit-engine/docs/data-permissions.md`, "Data permissions: one rule set, one
public check"). `DataPermissionEvaluatorDifferentialTests` pins, against real MongoDB, that its verdict equals
what a session bound to the same `RtSecurityContext` experiences: a record the filtered repository returns is
exactly the one the evaluator calls visible (`NotVisible` = not found), and a change set the write guard accepts
is exactly the one the evaluator allows (creator-owned `Test/Continent`, owner-attribute-owned `Test/Ticket`,
full / owned-only / read-only / write-only grants, AuditOnly, system context, type-level vs insert). A new
data-permission rule in the read filter or the guard must keep that test green.

The write guard applies the read visibility first, so the "write but not visible" cell is refused too
(the evaluator says `NotVisible`, the repository raises "not found"): the differential test compares every record for
Write and Delete, `HiddenRecord_IsRefusedExactlyLikeAMissingOne` pins that a hidden record raises the very exception
(type and message) of a missing one for update, archive and erase, and `WriteWithoutRead_CanInsert_ButNotUpdateOrDeleteExistingRecords`
pins the compatibility change. `TenantRepository.CreateEntityNotFoundException` (override of the engine hook) must mirror
the "not found" exception of `MongoDbDataSourceCollection` (update/replace/archive: `IdNotFound`, erase:
`FilterNotMatching`). Needs the engine package that contains the guard change.

`DataPermissionHiddenEqualsMissingTests` pins on real MongoDB that, for a caller with restricted read
(Read, Write and Delete owned-only; Write and Delete for all with Read owned-only; Write and Delete without Read), a
hidden record answers exactly like a missing rtId with a valid or an invalid payload, also when addressed as a sibling
type of the shared `Location` collection (`Test/Country`, run without a transaction; the stored record stays
unchanged, the engine's stored-type step refuses it after validation) and when deleted by the abstract
`Test/Location`. The caller's own visible record with the same invalid payload still answers the validation error.
Callers without a visibility question (system session, no policies, full read) keep the engine's answers of before
for a missing record only: an invalid payload is a validation error, a valid one is not found. Engine PR 383 changed
two answers: a delete through an abstract type of an rtId that does not exist answers not found instead of returning,
for every caller including a system session, and a valid update by a non-system caller under policies of a record
addressed through a sibling type (a type outside the stored type's subtree that shares its collection) answers not
found and writes nothing, also for a full reader. Needs the engine package of engine PR 383.

### Deep graph honours the caller's data permissions

Both `ITenantRepository.GetRtDeepGraphAsync` overloads (ParentChild traversal and the directed
follow-spec traversal) apply the caller's data-permission filter. The traversals themselves run over
the association collection and know nodes only as `(rtId, ckTypeId)`, so the filter is applied as a
post-step (`TenantRepository.PruneDeepGraphAsync`) on the **unpaged** result and paging is applied
afterwards (`TotalCount` = pruned count):

- A node passes when its type is unprotected or allowed for the caller (decided in memory from
  `RtDataSecurityQueryFilter`); owned-only types are checked against the stored entities with the
  very same predicate the other queries use (`DataSecurityFilterRenderer.Build`); everything else is
  denied.
- A denied node loses its row **and** every association that points to it (target id and type), so a
  restricted export reveals neither the entity nor the edge. Nodes only reachable through a denied node
  stay if the caller may read them (their own data is not hidden by the graph).
- System sessions and unrestricted tenants (`CreateSecurityFilterAsync` returns null) take the
  unchanged path: results, paging and aggregations are byte-identical to before. For restricted
  callers `AggregationResult`/`FieldAggregationResult` are not returned (they cannot be recomputed on
  the pruned set).
- Pinned by `DeepGraphDataPermissionTests` (denied type, owned-only, system bypass, unrestricted
  byte-identical, paging after pruning; both overloads) with the real filter and a restricted policy
  table. Consumers: the export-by-deep-graph job of octo-bot-services.

### AuditOnly read violations: action and aggregation

`TenantRepository.CreateSecurityFilterAsync` publishes `DataPermissions.ReadViolation` once per repository
call whose type graph hits `AuditDeniedCkTypeIds` (16 call sites; a `ForEach` of single queries makes
thousands per run). The audit event works as follows:

- The event is built by the engine's `DataPermissionAudit.PublishReadViolationAsync` with the real
  action: queries pass `read` (default parameter), the update/replace/delete overrides pass
  `update`/`replace`/`delete` for their pre-filter, so a write is no longer reported as "read". The
  read message is unchanged; other actions read `performed '<action>' on protected type … without a
  read grant`. Every call is counted in `octo.datapermissions.audit.violations`.
- `TenantContext.GetRepositoryAuditEventSink()` hands the engine's `AggregatingAuditEventSink`
  (fallback: the plain `IAuditEventSink`) to every `TenantRepository`, so the read filter and the
  engine's write funnel publish first occurrence + one summary per window per (tenant, subject,
  type, action). Other events through that sink carry no aggregation key and pass through unchanged.
- Pinned by `DataPermissionEnforcementTests.AuditOnlyViolations_NameTheRealAction_AndRepeatedReadsAreAggregated`
  (fixture `DataPermissionTestFixture.AuditSink`). Details of the aggregation: engine `octo-construction-kit-engine/docs/audit-trail.md`,
  "Aggregation of repeated audit events".

### Interface query: `ITenantRepository.GetRtEntitiesByInterfaceAsync`

One method returns the entities of **all implementers of a CK interface** (derived types included), filtered, sorted, paged
and optionally counted over all their collections in one global order (GraphQL interface types and the pipeline node
call it). Decision of the interface-query spike (Gerald, 2026-10-19, option A): type list from the CK cache
(`CkInterfaceGraph.ImplementingTypes`), fan-out over the collections of the implementers, application-side k-way merge of the
top `skip + take` rows per collection; NOT `$unionWith` (about 1 s at 1M), no denormalized `CkInterfaceIds`, **no migration,
no index created or changed, no change of the persisted `RtEntity`**. Full design: `docs/interface-query.md`. Code:
`Repositories/Query/RtInterfaceQueryExecutor.cs` (validation, plans, fetch, merge), `TenantRepository.GetRtEntitiesByInterfaceAsync`,
contract `ITenantRepository` + `RtInterfaceQueryResult` + `InterfaceQueryException` (`Runtime.Contracts.MongoDb`).

* **Per collection** the same `SingleOriginRtQuery` as the type query (field-filter resolver, rtState pre-filter, security
  pre-filter) with the `ckTypeId` pre-filter replaced by the implementers of that collection (`RestrictToCkTypeIds`), pipeline
  `$match` → `$sort` → `$limit(skip + take + 1)`; no `$facet`/`$skip`. The sort keys are exposed by `Query.SortKeys` and read
  from the raw documents for the merge; the global tie-break is the rtId. A tie that straddles the window end (detected with the
  extra row) is re-queried once, ordered by rtId, because the rtId cannot be part of the server sort without defeating the
  `(ckTypeId, attributes.<member>, rtState)` indexes.
* **Security.** One data-permission filter for the call (audit event as in the type query). Types that are protected and neither
  allowed nor owned-only for the caller are removed from the `$in` list; a collection left empty is **not queried** (test:
  `Security_ACollectionThatIsFullyDenied_IsNeverQueried`, command count). `Security_ShowsExactlyWhatTheTypeQueriesShow` compares
  the interface query with the union of the type queries for system, a role holder, owned-only readers and an outsider.
* **Count** only on request (`includeTotalCount`), one `countDocuments` per collection (parallel without a transaction); no
  count query otherwise (`Count_IsIssuedOnlyOnRequest_OneCommandPerCollection`).
* **Session.** In a transaction session the collections are queried one after the other (MongoDB: one operation at a time in a
  transaction); without a transaction queries and counts run in parallel (at most 8).
* **Errors.** `InterfaceQueryException`: message number **7019**, `Reason` token + `Parameters` are the contract
  (`UnknownInterface`, `TextSearchNotSupported`, `AggregationNotSupported`, `NavigationNotSupported`,
  `TooManyCollectionRoots` (64 roots), `PageTooLarge` (`skip + take` > 1000), `UnknownMember`); an interface without implementers
  returns an empty result.
* **v1 limits.** No text/attribute search, aggregation, navigation, geospatial; filter/sort only on interface members and system
  fields; untyped `RtEntity` rows; deep pages cost `collections x (skip + take)` documents.
* **Index guidance.** The method creates no index. A type that implements an interface declares an index on the member clients
  sort by (`indexes:` in its CK type: `(ckTypeId, attributes.<member>, rtState)`); **index the sort field of heavily used
  interface members**. Without it the sort of every collection reads all its matching rows (O(matching rows), no latency
  promise). There is deliberately no default index on `Name` (decision 2026-10-19; re-check on real tenants).
* **Budget and measured numbers** (`GetRtEntitiesByInterfacePerformanceTests`, trait `Category=Performance`, default 100 000
  entities over 30 collection roots, `OCTO_TEST_INTERFACE_QUERY_ENTITIES=1000000` for the opt-in 1M run; not in the PR/main CI,
  nightly in `azure-pipelines-performance.yml`; .NET end to end through
  `ITenantRepository`, MongoDB 8.0.15 Testcontainer, local development machine, page size 50, index on Name on every root, 20 runs after 3
  warm-ups, median / p95 in ms). Budget: one collection at most 1.25x of the type query with the same rows;
  indexed first page over 30 collections p95 at most 100 ms; unindexed: no promise (printed, not asserted).

  | Measure | 100k, transaction | 100k, no transaction | 1M, transaction | 1M, no transaction |
  |---|---|---|---|---|
  | indexed first page (Name), no count | 15.0 / 18.1 | 4.5 / 5.8 | 16.2 / 19.4 | 5.4 / 6.5 |
  | indexed, skip 450 | 86.1 / 95.7 | 45.6 / 55.2 | 94.9 / 100.9 | 49.4 / 63.1 |
  | indexed first page with count | 33.6 / 37.2 | 9.0 / 10.4 | 137.5 / 143.9 | 21.9 / 23.9 |
  | **unindexed** sort (Rank), first page | 65 / 65 | 41 / 45 | 585 / 589 | 408 / 411 |
  | one collection (Name), interface / type query | 1.5 / 3.0 | 0.7 / 2.6 | 5.3 / 30.1 | 3.9 / 29.5 |
  | one collection (Rank), interface / type query | 3.5 / 3.6 | 2.3 / 3.5 | 24.7 / 31.7 | 20.2 / 31.7 |

  The interface query with count requested is faster than the type query (which always counts in a `$facet`) in the one-collection
  case; the multi-collection count costs one pass over the matching rows (about 140 ms at 1M sequential, 22 ms parallel).
* **Tests.** `tests/.../InterfaceQuery/`: `GetRtEntitiesByInterfaceAsyncTests` (Testcontainers MongoDB, test model
  `InterfaceQueryTestModel`: Asset+Pump share a collection, Sensor+Gauge share one, Valve and Vault have one each; hand-computed
  windows, ties across and inside collections, security, count observation, every error code with parameters, the 64/65 root cap),
  `GetRtEntitiesByInterfacePerformanceTests`. The model builders (`InterfaceQueryTestModel`, `InterfaceQueryWideModel`) build
  `CkCompiledModelRoot` in C#, like `CkV2KitchenSinkModel`.
