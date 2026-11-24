---
description: Tenant registry vs. hierarchy, resolving a tenant, connection names, repository client release, write and read concern
applies_to: src/Runtime.Engine.MongoDb/Repositories/MongoDb/**, src/Runtime.Engine.MongoDb/Services/**, src/Runtime.Contracts.MongoDb/Services/**
---

## Tenant Registry vs. Tenant Hierarchy (AB#5025)

The system tenant's database doubles as the **platform-wide routing registry**: every tenant of
the installation has an `RtTenant` row there (with `ParentTenantId` naming its logical parent),
and per-request tenant resolution (`SystemContext.TryFindTenantContextAsync`) is a single-level
lookup against that registry. A child of a **non-system** tenant additionally has an `RtTenant`
row in the parent tenant's own database (written without `ParentTenantId`).

Two enumeration APIs with distinct semantics:

| API | Semantics | Use for |
|---|---|---|
| `ITenantContext.GetChildTenantsAsync` | **Direct children only** — filters `ParentTenantId == own id OR unset` | Logical hierarchy: child-tenant listings (`GetTenants`), client-mirror backfill |
| `ISystemContext.GetAllTenantsAsync` | **Full registry**, unfiltered | Installation-wide concerns: token cleanup, CORS, health checks, observability, rollup orchestration, tenant discovery |

Re-parenting an existing tenant = `Detach` (old parent context; requires tenant capabilities
disabled) + `Attach` (new parent context). Attach writes both registry rows; routing keeps
working because the system registry always holds every tenant.

**Registry reconcile (AB#6941):** a tenant can be listed by its parent's own registry (and so by the recursive
`GET tenants/descendants` / `GetTenants -r`) yet lack its system registry row (`GetTenants -a`) — then client mirror
provisioning, token cleanup, CORS and health checks never see it. `ISystemContext.ReconcileTenantRegistryAsync(session,
apply)` walks the tree through the parent-local registries (detached contexts, no CK import or ownership stamp) and
inserts the missing system rows with the parent set and id/database normalized. Dry run by default (`apply = false`);
it never deletes or changes a row: a row that disagrees (other database, other parent, database used by another
tenant) or a parent registry that cannot be read is returned as a conflict. Idempotent. It is an explicit admin
operation, not run at start-up; `TenantRegistryReconcileResult.Summary` is the one-line report (walked, inserted /
to insert, already present, conflicts).

## Resolving a tenant without the registry — the two runtime seams (AB#4924)

A process normally answers "where does tenant X live and how do I open it" from the installation:
`SystemContext.TryFindTenantContextAsync` runs `IsSystemTenantExistingAsync` (**`listDatabases` on the
admin connection** plus a system CK-model read), then reads the tenant's registry row, and
`UserMongoRepositoryClient` builds the connection from `OctoSystemConfiguration.DatabaseUser` (a
**format string** over the database name) and the installation-wide `DatabaseUserPassword`.

🔴 **Both halves are installation-wide authority, and an adapter-pool member must have neither.** Such
a member executes work for tenants other than the one that owns it and is handed exactly one of them
at a time by a lease; a process holding the admin password — or a datasource password whose user name
is derivable for every database — makes that lease decorative.

Two optional seams, both `GetService` and both inert in any host that registers no implementation:

| Interface | Keyed by | Answers | Consulted in |
|---|---|---|---|
| `ITenantLocationSource` | tenant id | which database holds that tenant | `SystemContext.TryFindTenantContextAsync`, **before** the probe and the registry read |
| `ITenantDatabaseCredentialSource` | database name | which credential opens that database | `UserMongoRepositoryClient`, per connection build |

Keyed differently on purpose: a process may open several databases, but asks for one tenant at a time.
Either seam alone is not enough — with only the credential the member still needs admin to *find* the
tenant, and with only the location it cannot open what it found.

🔴 **A context built from the location source is DETACHED: it manages nothing.**
`TenantContext.CreateDetachedTenantContext` performs none of the four side effects the registry route
performs — the system CK-model update, the stream-data model, the service-managed model imports and
the ownership stamp. Every one of those is a **write** into the tenant's database on behalf of the
installation that owns it, and a borrowed process running one pipeline is not that installation; an
ownership marker written by it would name the wrong owner. The tenant's own services have already done
all four.

The database name and the credential are **trusted, not verified** — verifying them against the
registry is precisely what is being avoided — so both must come from a party authorised to know them.
For a pool member that is the communication controller, which resolved both before granting the lease.

Tests: `tests/Runtime.Engine.MongoDb.IntegrationTests/DetachedTenantContextTests.cs`. 🔴 The
arrangement is the proof: the tenant's **database still exists** while its **registry row has been
deleted**, which is the one state in which the two routes disagree — the registry route answers "does
not exist", the detached route resolves and the repository opens a session. Arming the source for a
tenant the registry also knows would pass either way and would keep passing after a regression.
`TestTenantLocationSource` is registered in the shared fixture and answers nothing until a test arms
it, so every other test still exercises the registry route.

## Connection ApplicationName is clamped (AB#4762)

Both repository clients label their connection
`OctoMesh-{database}-{instanceId:guid}-{user}` so it is identifiable in MongoDB's `currentOp` and
logs. The driver rejects an ApplicationName longer than **128 bytes**, and the database name appears
in that string **twice** — once directly and once inside the per-tenant user
(`string.Format(DatabaseUser, database)`). A database name beyond roughly 30 characters therefore
threw `ArgumentException` from `MongoUrlBuilder.set_ApplicationName` deep inside tenant provisioning:
the tenant came up half-built and then failed on every background tick forever, and a service that
iterates all tenants at startup (identity's `DynamicAuthSchemeServiceInitializer`) refused to boot at
all.

`MongoRepositoryClient.BuildApplicationName` truncates it on a UTF-8 character boundary instead.
Clamping a diagnostic label is strictly better than refusing to serve the tenant, so **do not** turn
this back into a validation rule on the database name — MongoDB's own 63-byte limit is the only one
callers should have to satisfy.

## Repository clients are released, not only dropped (AB#8281)

Every `AdminMongoRepositoryClient` / `UserMongoRepositoryClient` is a MongoDB driver cluster of its own (own application
name, own `ClusterConfigurator`, both part of the driver's `ClusterRegistry` key): one heartbeat thread, one RTT-monitor
thread and its pooled sockets per client and database. Rules:

- **`MongoClient.Dispose()` releases nothing of that** in driver 3.x: with the default cluster source it only returns the
  cluster to the process-wide registry, which keeps sockets and monitor threads alive. `MongoRepositoryClient.Dispose` calls
  `ClusterRegistry.Instance.UnregisterAndDisposeCluster(client.Cluster)` and then disposes the client. Do not "simplify" it
  back to `_client.Dispose()` (the first AB#8281 attempt did; the sockets stayed open) or to `Cluster.Dispose()` alone (the
  registry entry and, through the configurator, the host's service graph stay reachable).
- **The accesses own their clients.** `RepositoryClientCache<TClient>` (behind `AdminRepositoryAccess` /
  `UserRepositoryAccess`) tracks every client it built, evicted ones included, and disposes them all when the DI container
  disposes the access (both are `IDisposable` singletons). `Invalidate` still only evicts (the FixAll /
  `CoreServerSessionPool` reason is in the code); an evicted client is released at shutdown instead of never.
- Why it mattered: no client was ever disposed, so every disposed host leaked its clients. The identity integration suite
  (one host per test class plus derived hosts, hundreds of tenants) ended with ~5,300 MongoDB connections, ~1,300 RTT-monitor
  and ~1,800 heartbeat threads and an 8-9 GB test process; on a loaded CI agent it ran out of memory and token requests
  answered 500 (identity build 65204 on r3.5.61).
- Test: `RepositoryClientDisposalTests` asserts on the **server** (`$currentOp` by application name) that a disposed access
  leaves no connection behind - an `ObjectDisposedException` on the client alone also passed with the sockets still open.

## Write and read concern of the repository clients (AB#8026)

`MongoRepositoryClient.CreateClientSettings` sets the consistency of **every** repository client (admin and user,
system and tenant databases) in code, after `MongoClientSettings.FromUrl`:

| Setting | Value |
|---|---|
| Write concern | `w: majority`, `wtimeout` 30 s (`MongoRepositoryClient.RepositoryWriteConcern`) |
| Journal (`j`) | not set: follows the replica set's `writeConcernMajorityJournalDefault` (MongoDB default `true`), so a majority ack means journaled on a majority |
| Read concern | `majority` |
| `RetryWrites` | `true` |

No configuration can weaken it: the clients build their URL from `OctoSystemConfiguration` (there is no connection
string), and a `w` / `wtimeoutMS` / `readConcernLevel` option in a URL would be overwritten anyway. Transactions
(the handler session of a CK method, methodConsistency R3) inherit the client write concern. CK method handlers rely
on it: a success envelope is sent only for a write a majority acknowledged (methodConsistency OD-7, decided
2026-11-01: keep the defaults, document them, pin them with a test). A `MongoWriteConcernException` (majority not
reached within 30 s) does not undo the write on the primary; a method handler must report it as an unknown outcome (R3), not as "nothing happened".

Pinned by `MongoRepositoryClientConsistencySettingsTests` (no database): the live driver settings of a real
`AdminMongoRepositoryClient` and `UserMongoRepositoryClient` (`ClientSettings`, internal test seam over
`MongoClient.Settings`) and `CreateClientSettings` with a URL that asks for `w=1`, a 1 s wtimeout and read concern
`local`. Changing a value turns the test red on purpose; such a change needs a new decision on OD-7 first.
