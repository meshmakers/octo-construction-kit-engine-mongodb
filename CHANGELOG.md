# Changelog

## Unreleased

- AB#8896 New: a paged runtime-entity query loads at most one page of documents. `SingleOriginQuery.ExecuteQuery`
  no longer puts count and page into one `$facet`, which pushed every matching document through the server and sorted
  the whole set in memory. A paged query now runs a page pipeline (`$match`, the association stages that navigation
  filters need, one `$sort`, `$skip`, `$limit`; the enrichment `$lookup`s afterwards on the page only) and a separate
  server-side count (the `$match` + `$count` pipeline of AB#8895, no document returned). Page and count are two reads
  on the caller's session, one after the other; outside a transaction they are not one snapshot. Items, order and
  total count stay the same (page and count parity tests per query shape and per data-permission caller). New
  additive overloads `ITenantRepository.GetRtEntitiesByTypeAsync` / `GetRtEntitiesGraphByTypeAsync` with
  `includeTotalCount`: `false` runs no count at all and returns a lower bound as `TotalCount` (skip + returned
  entities, 0 for an empty page). The existing overloads always count and are unchanged for every caller. A paged
  query with `take` 0 returns no entity without reading a page (it used to fail with `$limit` 0).
- AB#8895 New: a runtime-entity query that asks only for the total count counts on the server without loading any
  document. `ITenantRepository.CountRtEntitiesByTypeAsync` (type query) and `CountRtEntitiesGraphByTypeAsync` (type
  query with navigation pairs, as persistent queries use) run `$match` - plus the geospatial stage and the association
  stages that navigation filters need - followed by `$count`; no sort, no `$facet`, no enrichment `$lookup`, no entity
  is materialised. The count equals the `TotalCount` of `GetRtEntitiesByTypeAsync` / `GetRtEntitiesGraphByTypeAsync`
  with the same options, data permissions of the caller included (count parity tests per query shape). Additive: two
  new interface members, implemented here; existing queries are unchanged. Before, an unpaged query that only needed
  the count loaded every matching document (ASSET1002 OUT_OF_MEMORY on large entities, AB#8793).
