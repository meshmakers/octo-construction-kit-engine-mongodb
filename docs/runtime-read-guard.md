---
description: "Runtime read guard: unbounded runtime-entity reads of pipeline nodes made visible (warning and metric, no cap)"
applies_to: src/Runtime.Engine.MongoDb/RuntimeQueries/RuntimeReadGuard*.cs, src/Runtime.Engine.MongoDb/Repositories/MongoDb/TenantRepository.cs, src/Runtime.Engine.MongoDb/Configuration/DependencyInjection/RuntimeEngineBuilderExtensions.cs
---

## Why

Pipeline nodes in the mesh adapter read runtime entities directly through the engine (`ITenantRepository` against
MongoDB). The asset repository's runtime query guard (AB#8898) sits in its GraphQL API and never sees these reads. The
runtime read guard (AB#9824) makes them visible: **it logs and counts, it never caps, refuses or changes a read** -
results, paging and errors are the same in every mode. A cap is a later step (AB#8911).

## What counts as unbounded

A read is unbounded when it may return more than `MaxTake` entities (default 1000, the page limit of the asset
repository's guard): no `take`, a `take` above `MaxTake` (`int.MaxValue` included) or a negative `take` - unless a key
list of at most `MaxTake` ids bounds it (reads by id). Covered reads of `TenantRepository`
(`RuntimeReadKinds`): `ByType` (`GetRtEntitiesByTypeAsync`), `ById`, `GraphByType`
(`GetRtEntitiesGraphByTypeAsync`, which persistent and aggregation queries use), `GraphById`, `AssociationTargets`
(`GetRtAssociationTargetsAsync`, every overload), `IndirectAssociationTargets` and `DeepGraph`. Not covered: the
interface query (its `take` is required and capped at 1000 by the engine), counts, CK reads and migration reads.

## Who called: `RuntimeReadScope`

The caller comes from the ambient `RuntimeReadScope` (`Runtime.Contracts`, octo-construction-kit-engine): the pipeline
runtime (octo-communication-sdk) opens an execution scope (tenant, pipeline, data flow, execution id) and a node scope
(node type, node path, node key) around every node. Inside an execution the guard reports **one warning and one count
per execution and node**, however often the node runs (a `ForEach` over 500 items is one report). A read outside an
execution (the host's own reads) is reported with caller `unknown`, at most once per tenant, CK type and hour. Reads
inside `RuntimeReadScope.Suppress()` are deliberate batch work and not reported.

**Tenant state and leases.** The per-execution marks live on the execution scope, not on the guard. The guard's only
tenant-keyed state is the hourly throttle of the `unknown` caller; `ISystemContext.InvalidateTenantResolveImportGuards`
drops it for the tenant (`RuntimeReadGuard.ForgetTenant`), which a pooled mesh adapter calls when a lease ends and the
tenant lifecycle calls before an update or delete. The counter series carry the tenant as a label and are cumulative by
design: they are kept, so `increase()` stays correct, and a read of one tenant is never counted under another.

## Opt-in per host, no double count

`AddRuntimeReadGuard()` on the runtime engine builder registers the guard; `TenantContext` hands it to every tenant
repository. The mesh adapter calls it. The asset repository does not: its API has its own guard, so a read is never
counted by both. Without the call the repositories have no guard and nothing changes.

## Configuration

Section `RuntimeReadGuard` of the host:

| Key | Values | Default |
|-----|--------|---------|
| `Mode` | `Off` (nothing is logged or counted) or `Warn` (case ignored) | `Warn` |
| `MaxTake` | the largest `take` that counts as bounded; below 1 = default | `1000` |

There is no mode that caps or refuses. A `Mode` that names neither value (for example `Enforce`), or a section that
cannot be bound, is logged once per value as a warning (event 41002, `RuntimeReadGuard.InvalidConfiguration`, naming
the invalid and the valid values) and the guard runs as `Warn` - with the configured `MaxTake` for an invalid `Mode`,
with the default limit for a section that cannot be bound; a misconfiguration never turns the guard off silently. A
`take` of `int.MaxValue` always counts as unbounded, whatever `MaxTake` is - unless a key list of at most `MaxTake` ids
bounds it (reads by id). Engine-internal lookups run suppressed and are never attributed to a node: the registry reads
`ISystemContext.GetAllTenantsAsync`, `GetChildTenantsAsync` and `GetDirectChildTenantsAsync`, the tenant lookups
behind `TryGetChildTenantAsync` and the platform-wide tenant lookup, the tenant configuration reads
(`GetConfigurationAsync`, the read in `SetConfigurationAsync`) and the data-policy table load. The mesh adapter chart sets the section from `runtimeReadGuard.mode` / `runtimeReadGuard.maxTake`.

## Warning and metric

- Log: warning, event id 41001 (`RuntimeReadGuard.UnboundedRead`), message starting with
  `Unbounded runtime-entity read`, with the structured fields `ReadKind`, `CkTypeId`, `TenantId`, `Take` (`null`,
  the number or `int.MaxValue`), `ReturnedCount` (`unknown` when the result cannot be counted without enumerating it),
  `MaxTake`, `NodeType`, `NodePath`, `PipelineId`, `PipelineName` (only when the host knows it; the mesh adapter
  passes only the ids), `DataFlowId`, `ExecutionId`.
- Metric: counter `octo.engine.runtime_read.unbounded` (Prometheus `octo_engine_runtime_read_unbounded_total`) on the
  meter `Meshmakers.Octo.MongoDb`, labels `tenant`, `ck_type`, `node_type` (`unknown` outside a node) and `mode`. It is
  observable: a new series is exported as 0 at the first collection after its first hit and with its value from the
  next one, so the first hit is an increase. At most 2000 series; further combinations count under `other`.

Dash0 (logs of the mesh adapters): filter the log body for `Unbounded runtime-entity read` and group by the
attributes `TenantId`, `PipelineId` and `NodeType`; metric: `increase(octo_engine_runtime_read_unbounded_total[1h])` by
`tenant`, `node_type`, `ck_type`.
