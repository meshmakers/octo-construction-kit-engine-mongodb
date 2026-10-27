# Interface query: `ITenantRepository.GetRtEntitiesByInterfaceAsync` (AB#7019)

Developer documentation of the engine method that returns the entities of **all implementers of a CK interface** in one
global order. Decision: spike AB#6930 (Gerald, 2026-10-19, option A). Consumers: the GraphQL interface types (AB#6932) and
the pipeline node (AB#6933); a consumer calls this one method and never fans out or merges itself.

```csharp
Task<RtInterfaceQueryResult> GetRtEntitiesByInterfaceAsync(IOctoSession session,
    RtCkId<CkInterfaceId> ckInterfaceId, RtEntityQueryOptions queryOptions, int skip, int take,
    bool includeTotalCount = false);
// RtInterfaceQueryResult(IReadOnlyList<RtEntity> Items, long? TotalCount)   TotalCount == null: not asked for
```

## Why a fan-out, and why not `$unionWith`

Runtime entities live in one MongoDB collection per **collection root**. The implementers of an interface are in
several collections (unrelated types always are), so there is no single query. Measured in the spike (mongosh): a
`$unionWith` over the collections cannot use the sort index and costs about a second at 1M entities even with the index.
Option B (a denormalized `CkInterfaceIds` on the entity) would not remove the fan-out and is not built: no migration, no
new index, no change of the persisted `RtEntity` contract.

## Algorithm

1. **Validate** (before any collection is touched, in this order): `skip`/`take` negative → `ArgumentOutOfRangeException`;
   `skip + take > 1000`; text search or attribute search; field or result aggregation; geospatial filter; a filter or sort
   path with navigation syntax (`->`, `::`); a filter or sort path whose first segment is no member of the interface
   (`CkInterfaceGraph.AllAttributes`) and no system field (rtId, ckTypeId, rtCreationDateTime, rtChangedDateTime, rtVersion,
   rtWellKnownName, rtCreatedBy). Each refusal is an `InterfaceQueryException` (below).
2. **Implementers** come from the CK cache: `ICkCacheService.GetRtCkInterface` → `CkInterfaceGraph.ImplementingTypes` (derived
   types included). Abstract types are dropped, the rest is grouped by `CkTypeGraph.DefiningCollectionRootCkTypeId`, i.e. by
   collection. More than 64 collection roots → refusal (same cap as the abstract navigation target, AB#5000).
3. **Security.** The caller's data-permission filter is built once (`IDataSecurityFilterFactory`, audit event for AuditOnly
   policies as in the type query). It is a predicate on `ckTypeId`, so it composes with the per-collection `$in` list of
   implementing types. A type that is protected and neither allowed nor owned-only for the caller can never match; it is
   removed from the `$in` list, and a collection that is left with no type is **not queried at all**. Owned-only and
   owner-attribute rules stay predicates in the query (decided per row), exactly as for the type query.
4. **One query per collection**, built by the same `SingleOriginRtQuery` as the type query (same field-filter resolver, same
   rtState pre-filter, same security pre-filter), with the `ckTypeId` pre-filter replaced by the implementers of that
   collection (`RestrictToCkTypeIds`). The attribute paths are resolved against one implementer of the collection; the stored
   path of an attribute does not depend on the type, and the compiler guarantees that every implementer has every member.
   Pipeline: `$match` → `$sort` (the requested keys; `_id` when none) → `$limit(skip + take + 1)`; no `$facet`, no `$skip`.
5. **K-way merge** in the application on the sort keys read from the raw documents (`RtInterfaceQueryExecutor.ExtractKey` /
   `CompareValues`: MongoDB type bracketing, numbers by value, strings by UTF-8 bytes - the entity collections have no
   collation; of an array the smallest element for ascending, the largest for descending; missing = null). The tie-break is the
   rtId ascending, so the global order is total and the same for every collection layout. Only the rows of the requested
   window are deserialized (`BsonSerializer.Deserialize<RtEntity>`), Secret read-normalization is applied as for the type query.
6. **Ties at the window end.** The server order of documents that tie on every sort key is arbitrary, so a collection that is
   cut inside a tie could hand a different subset of the tied rows to the merge than the global order (rtId) wants. The extra
   row (`+ 1`) detects it: when row `skip + take` ties with row `skip + take - 1` the collection is asked once more for **all
   rows of that tie** ordered by rtId (limit `skip + take`) and the tied rows are replaced. No tie → no second query.
   (The rtId cannot be part of the server sort: the declared indexes are `(ckTypeId, attributes.<member>, rtState)`; a sort on
   `(<member>, _id)` is not served by them and would turn every indexed sort into a blocking sort.)
7. **Count** (`includeTotalCount`): one `countDocuments` per collection with the same filter, summed. Without
   `includeTotalCount` no count query is issued (`MongoRequestScope.CommandCount` in the tests).

### Session and parallelism

* In a session with an **active transaction** the collection queries (and counts) run one after the other: MongoDB runs one
  operation at a time in a transaction. This is the case for the usual request pipeline (GraphQL session listener).
* Without a transaction the queries and counts run in parallel (at most 8 at once) on implicit sessions.

## Errors (stable contract)

`InterfaceQueryException` (`Meshmakers.Octo.Runtime.Contracts.MongoDb`, a `PersistenceException`): `MessageNumber` is always
**7019**; the case is the `Reason` token (also parameter `reason`); `Parameters` carry the machine-readable values; the text of
`Message` is English for logs and may change. Precedent: message 136 (`BlueprintGenericCreateGuard`, one number plus a reason
token). Tokens in `InterfaceQueryReasons`:

| Reason | Parameters |
|---|---|
| `UnknownInterface` | `ckInterfaceId` |
| `TextSearchNotSupported` | `ckInterfaceId` |
| `AggregationNotSupported` | `ckInterfaceId` |
| `NavigationNotSupported` | `ckInterfaceId`, `attributePath` (empty for a geospatial filter) |
| `TooManyCollectionRoots` | `ckInterfaceId`, `rootCount`, `maxRoots` (64) |
| `PageTooLarge` | `skip`, `take`, `maxSkipPlusTake` (1000) |
| `UnknownMember` | `ckInterfaceId`, `attributePath`, `usage` (`filter` or `sort`) |

An interface that exists but has no implementer (or none the caller may see) returns an empty page and, if asked, count 0.

## Budget and measured numbers

Budget of the decision: one collection at most 1.25x of a type query returning the same rows; several collections with an indexed
sort field: first-page p95 at most 100 ms (30 collections, 1M entities); unindexed sort field: no promise (O(matching rows)).
The test `GetRtEntitiesByInterfacePerformanceTests` (trait `Category=Performance`; 100 000 entities by default,
`OCTO_TEST_INTERFACE_QUERY_ENTITIES=1000000` for the 1M run) asserts the first two and prints the third. Measured numbers
(.NET end to end through `ITenantRepository`, MongoDB 8.0.15 in a Testcontainer on a Mac mini, page size 50, 30 collection
roots, index `(ckTypeId, attributes.name, rtState)` on every root): see the table in `CLAUDE.md`, "Interface query".

**Guidance: index the sort field of heavily used interface members.** The method creates and changes no index. A type that
implements an interface declares the index of the member that clients sort by in its own CK type (`indexes:`); without it the
sort of every collection reads all of its matching rows. (Decision of Gerald 2026-10-19: no default index on `Name`.)

## Limits of v1

* No text search, no aggregation, no navigation, no geospatial filter (refused, see above).
* `skip + take <= 1000`: every collection returns its first `skip + take` rows to the merge, so deep pages cost
  `collections x (skip + take)` documents; use filters, not deep paging.
* Sort on a multi-valued path follows MongoDB's min/max rule for the order; ties on such a path at the window end are resolved
  by the rtId only among the rows the equality re-query returns.
* The result is untyped (`RtEntity`); `RtEntity.CkTypeId` names the concrete type of every row.
