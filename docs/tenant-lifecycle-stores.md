---
description: Tenant database prefix, lifecycle notifications, lifecycle and retry stores, method handler snapshots, infrastructure-only shell
applies_to: src/Runtime.Contracts.MongoDb/TenantLifecycle/**, src/Runtime.Engine.MongoDb/Repositories/TenantLifecycle/**
---

### Tenant-Database Prefix — per-instance names on a shared MongoDB

Two instances on one MongoDB server (different `SystemDatabaseName`) used to collide on equal tenant ids: the
namespace gate refuses a database name that exists anywhere on the server, and clients name the
database after the tenant id by convention. `OctoSystemConfiguration.TenantDatabasePrefix` (section `System`,
env `OCTO_SYSTEM__TENANTDATABASEPREFIX`, **default empty**) gives each instance its own name space, the MongoDB
counterpart of `StreamData:SchemaInstancePrefix` (CrateDB). It is its own setting on purpose: an instance may
set either one without the other. Both default to empty. An operator may configure `TenantDatabasePrefix` on an
existing instance: its existing tenants keep their database names (see the exemption rule below). This exemption
is MongoDB-only: `StreamData:SchemaInstancePrefix` has none, so an existing instance must never set it, or its
tenants' CrateDB schemas would move (see the Stream Data section).

| Rule | Where |
|---|---|
| Format: normalized (trim, lower case), at most 30 bytes of ASCII letters, digits, `-`, `_`; anything else fails when `System` is bound (`ConfigurationErrorException` naming the option) | `TenantDatabaseNames.ValidatePrefix`, the setter |
| Default name of a new tenant = `<prefix><tenantId>`, normalized; without a prefix the tenant id (unchanged convention) | `TenantDatabaseNames.DeriveDefault` (public, for the hosts: asset repository REST create without `databaseName`) |
| Create and attach with a name that does not start with the prefix → the uniform `Database name '<x>' is not available.` (409); the prefix goes to the log only | `EnsureDatabaseNameCarriesTenantDatabasePrefix`, one call in the namespace gate after the reserved-name checks |
| Tenants registered before the prefix keep their names: resolving never looks at the prefix; `ClearChildTenantAsync` re-creates under the old name inside `TenantDatabasePrefixExemption.Begin(name)` (an `AsyncLocal` scope for exactly that name and call) | `TenantDatabasePrefixExemption` |
| The existence check is unchanged: a prefixed name that exists on the server is still refused | namespace gate |

Restore (`TenantBackupService.RestoreTenantAsync`) attaches after `mongorestore`, so the attach's refusal would
come after the data is written. `CheckTenantDatabasePrefixAsync` therefore runs right after the cross-instance
ownership guard, before any drop or write: a target name without the prefix is refused with a descriptive
`CommandResult.Failure`, unless it is the current database of the same tenant id (a restore over a tenant from
before the prefix), which the restore then attaches inside the exemption scope. The scope is opened by the caller,
not inside the async helper: an `AsyncLocal` set in an awaited method does not flow back. Without a prefix nothing
of this runs differently.

Tests: `TenantDatabasePrefixTests` (own collection; switches the prefix on the bound options object to emulate a
restart: default name, explicit name refused on create and attach, prefixed attach, no prefix as before, legacy
tenant resolves and survives a clear, taken prefixed name refused, restore into an unprefixed new name refused before
anything is written, restore over a legacy tenant keeps its name), `TwoInstanceTenantDatabasePrefixTests` (two real
instances on one server, `TwoInstanceCollection`: different prefixes create the same tenant id with the default name;
without prefixes the second is refused) and `StreamData.UnitTests/TenantDatabaseNamesTests` (naming, validation,
exemption scope).

### Tenant-created notification after the commit

Every service sets a tenant up when it receives `PosCreateTenant` (`PosCreatePosUpdateTenantConsumer`, octo-common-services)
and must find the tenant registered. `CreateChildTenantAsync` and `AttachChildTenantAsync` write the registry rows in the
caller's session, so they hand the notification to `NotifyPosTenantCreateAfterCommitAsync` (`TenantContext`) instead of
publishing it:

| Situation | When it is published | A failed publish |
|---|---|---|
| Inside a `TenantNotificationScope` bound to the same session (engine operations that own their transaction: the restore's attach in `TenantBackupService`) | by the operation, `scope.PublishAsync()` after its commit | reaches the operation; the restore still succeeds (data restored and attached) and sets `CommandResult.TenantCreatedNotificationFailed`, with the warning ("... run ClearCache") in `Output` and a WARN log; the clone (`CloneTenantToTempAsync`) passes the flag on. Nothing is dropped: a drop by id after the commit could hit a tenant that replaced it |
| Caller's transaction (`IOctoSessionInternal.IsTransactionActive`) | after-commit callback of the session (`RegisterTransactionCallbacks`); nothing on a rollback, and nothing when the same transaction detached or dropped the tenant after it or created or attached it again (`TenantTransactionNotificationState`, per session, no read; the registry read is a second check) | three attempts (`PosTenantCreateRetryDelays`: 250 ms, 1 s; each bounded by `PosTenantCreateAttemptTimeout`, 5 s), then a WARN naming the tenant and the recovery |
| No transaction | at once, as before | thrown to the caller (create: rolled back as before) |

Rules:

- **Nothing re-sends a lost notification.** The setup retry store is keyed per service, so the engine cannot queue a setup
  for every service. A tenant whose notification failed for good is set up by a ClearCache of the tenant (it publishes a
  full `PosUpdateTenant`), the tenant's next CK import or a service restart. That is why the restore flags it on its result
  and the callback path warns with the recovery.
- The callback runs inside the caller's `CommitTransactionAsync`, so its retries delay that call (at most ~16 s when every
  attempt times out); keep the delays short. An attempt that times out is abandoned; if it completes later, the services
  set the tenant up twice (setup is idempotent).
- A scope collects only the creates and attaches of the session it was opened with; any other session in the same async
  flow publishes as without a scope. `PublishAsync` bounds each publish to `PosTenantCreateAttemptTimeout` too.
- A caller's transaction gets a WARN, not an error, on a lost notification (the asset repository's create and attach
  answer success then). Surfacing it to that caller would need a change of the asset repository, not of the engine.
- `ClearChildTenantAsync` (drop + create in the caller's session) follows the same rule for its create, and inside a
  transaction its `PosUpdateTenant` is published when the transaction ends (commit or rollback, as before; not after a
  commit whose outcome is unknown), after the tenant-created notification (callbacks run in registration order), bounded
  to 5 s; a failure is a warning.
- A commit whose outcome is unknown (the commit call throws although the server may have committed) runs no callback,
  so no notification goes out; the caller gets the exception. The same holds for the tenant-deleted notification
  (next section).
- The system tenant's own create (`SystemContext.CreateSystemTenantAsync`) has no transaction and publishes at once.
- Tests: `TenantCreatedNotificationOrderTests` (collection `TenantNotification`, fixture `TenantNotificationFixture`: the
  recording notifications check `IsTenantRegisteredAsync` at publish time): create and attach in a transaction publish after
  the commit with the tenant registered, a rolled-back attach publishes nothing, no transaction publishes at once, the
  callback retries and then warns, the restore publishes after its commit and flags a failed publish on its successful
  result, a clone passes the flag on, an attach and detach in one transaction publishes no
  tenant-created notification (also with the registry read disabled), attach, detach and attach publish once, a scope of another session does not hold back a create, a clear in a transaction
  publishes delete, create, then update, after the commit.

### Tenant-deleted notification after the commit

Services tear a tenant down when they receive `PosDeleteTenant` (for example the bot's operation purge), so a drop or
detach must not announce a removal that its transaction can still roll back. `DetachChildTenantAsync` and
`DropChildTenantAsync` remove the registry rows in the caller's session and hand the notification to
`NotifyPosTenantDeleteAfterCommitAsync` (`TenantContext`). It is the same mechanism as the tenant-created notification
above (`NotifyPosTenantAfterCommitAsync`, `TenantNotificationScope`, `PublishWithRetryAsync`), with one rule per situation:

| Situation | When it is published | A failed publish |
|---|---|---|
| Inside a `TenantNotificationScope` bound to the same session (the backup's detach, `BackupTenantAsync(detachTenant: true)`, and the restore's drop of an existing tenant in `TenantBackupService`) | by the operation, `scope.PublishAsync()` after its commit | reaches the operation; it still succeeds (the removal is committed), and the warning ("... run ClearCache for the tenant") is appended to `CommandResult.Output` and logged as a WARN. `CommandResult.TenantCreatedNotificationFailed` is not touched. When a later step of the operation throws (the restore's re-claim, mongorestore or attach; the backup's dump), the failure result (`Exception during restore: ...` / `Exception during backup: ...`) still carries the warning in `Output` |
| Caller's transaction | after-commit callback of the session; nothing on a rollback or on a dispose without commit | three attempts (`PosTenantCreateRetryDelays`, each bounded by `PosTenantCreateAttemptTimeout`), then a WARN naming the tenant and the recovery |
| No transaction | at once, from the `finally` as before (a failed drop or detach still announces it) | thrown to the caller |
| Two-phase delete (`DeleteChildTenantMetadataAsync`, commit, `DropTenantDatabaseAsync`) | `DropTenantDatabaseAsync` publishes at once: the caller committed before | thrown to the caller, as before |

Rules:

- **No skip for a delete.** A tenant-created notification is dropped when the same transaction removed the tenant again;
  a tenant-deleted notification is always published after the commit, also when the same transaction created the tenant
  again: its database was dropped (DDL, no transaction), so the services must let go of it. Callbacks run in
  registration order, so a clear in a transaction publishes delete, create, update.
- **The database drop is not transactional.** `DropChildTenantAsync` inside a transaction that is rolled back has
  dropped the database, but the registry rows come back and no notification goes out (unchanged: only the registry is
  transactional; such a tenant was broken before too).
- **Recovery for a lost notification**: services may keep the tenant's cached state (contexts, caches, open clients)
  until a ClearCache of the tenant or their next restart. The asset repository's delete endpoint uses the two-phase path
  and is not affected.
- `SystemContext.DeleteSystemTenantAsync` runs without a transaction and publishes at once.
- Tests: `TenantDeletedNotificationOrderTests` (collection `TenantNotification`; the recording notifications check
  `IsTenantRegisteredAsync` at publish time): detach and drop in a transaction publish after the commit with the tenant
  unregistered, a rolled-back or uncommitted detach publishes nothing, the two-phase delete publishes when the database
  is dropped, the callback retries and then warns, the backup's detach and the restore's drop publish after their commit
  and name a failed publish in the output, also in the failure result when a later step throws (a wrapped
  `IRepositoryOpsService` throws in mongodump / mongorestore), a scope of another session does not hold back a detach. A clear in a
  transaction publishes delete, create, update (`TenantCreatedNotificationOrderTests`).

### Tenant Lifecycle Store — `EnsureCreatingAsync` is an atomic branch on the stored state

`TenantLifecycleStore` (`Repositories/TenantLifecycle/`, non-CK collection `tenant_lifecycle` in the
SYSTEM database) is written from two directions at once: `SetupTenantAsync` calls
`EnsureCreatingAsync` on **every** tenant setup run, while the reconciler in `octo-common-services`
concurrently claims tenants with `TryClaimForReconcileAsync` (single-flight via a 2-minute lease plus
an `AttemptCount` retry budget). In a live cluster `PosCreateTenant` / `PosUpdateTenant` events arrive
continuously, so those two writers overlap constantly.

`EnsureCreatingAsync` is therefore a **single `UpdateOneAsync` with an aggregation-pipeline update**
(`$set` + `$cond`, upsert), not the former `Find` → build record → `ReplaceOneAsync` round trip. Three
branches, decided on the state that is already stored:

| Stored state | Effect |
|---|---|
| `Active` | Metadata refresh only (`LastTransitionUtc`, `DatabaseName`) — a healthy tenant re-running setup is never downgraded. |
| `Creating` | Re-opens the phase (`SetupStarted`, `LastError` cleared) but **leaves `AttemptCount`, `LeaseOwner`, `LeaseUntil` untouched** — that bookkeeping belongs to whoever is currently driving the tenant. |
| missing / `Deleting` / `Failed` | New creation cycle: attempt budget, lease and last error are reset; `CreatedUtc` is preserved via `$ifNull`. |

Why it matters: the old read-modify-write cleared `LeaseOwner`/`LeaseUntil` on every setup
run and could revert a concurrent claim's `AttemptCount` increment (lost update). A tenant stuck in
`Creating` therefore stayed at `AttemptCount = 0` with no lease forever — it never exhausted the retry
budget, never reached `Failed`, and never recorded a diagnosable `LastError`, which made the reconciler
look dead when it was in fact running.

The pipeline addresses fields by their **stored element name** (the typed builders cannot express
`$cond`), so the names live in `TenantLifecycleStore.Fields` and `TenantLifecycleStoreTests`
pins them against the class map — a property rename or a change to the camelCase convention would
otherwise make the update write to fields nobody reads, silently and without error.

### Repository-Client Cache Invalidation on Tenant Delete

`UserRepositoryAccess` / `AdminRepositoryAccess` cache one `MongoRepositoryClient` — and therefore one
`MongoClient` with its own connection pool — **per database name**, for the lifetime of the process.

Dropping a tenant also drops its database user. MongoDB then invalidates the authentication of every
connection already open in those pools, and **the driver never re-authenticates an existing
connection**: each one keeps failing with error 13 (`"... requires authentication"`) even after the
tenant is re-created and the user exists again. Connections are only retired when the server or the pool
happens to close them, so a re-created tenant could be unusable for hours — this is the root cause of
an incident where a delete + recreate under the same database name left Identity unable to read the new
tenant database, so its `SetupTenantAsync` aborted and the tenant ended up with no roles.

Both accessors therefore expose `Invalidate(databaseName)`, which **evicts** the cached client so the
next resolve builds a fresh, freshly-authenticated one. Reached through
`ISystemContext.InvalidateTenantRepositoryClientsAsync(tenantId, databaseName?)`, which resolves
the database name from the tenant record when it is not supplied.

**Eviction only — never dispose.** The first iteration disposed the evicted client
(`Dispose` → `Cluster.Dispose`). That was a regression: handed-out clients are captured by live
`TenantContext` / `MongoDbRepositoryDataSource` instances beyond the cache, and disposing tears the
cluster down underneath them — every in-flight operation then fails with
`ObjectDisposedException('CoreServerSessionPool')`. Observable as: a sequential CK batch import
(LibraryStatus **FixAll**) whose per-import `PosUpdateTenant` event disposed the tenant's client
between two batch steps, so each FixAll run imported exactly one model and then failed at the next
`AcquireModelImportLockAsync` (tenant-setup storms on shared test installations, 2026-08-05). The evicted
client is collected once its holders let go; its stale connections are closed by server/pool idle
handling.

Called from two places:

| Where | Why |
|---|---|
| `TenantContext.DropTenantDatabaseAsync` (engine, explicit database name) | The process performing the drop, independent of any event delivery. |
| `PreUpdatePreDeleteTenantConsumer` on `PreDeleteTenant` + `PosCreatePosUpdateTenantConsumer` on `PosCreateTenant` (`octo-common-services`) | Every other service: pre-delete while the tenant record still exists so the name resolves; post-create closes the window where a resolve between the pre-delete event and the physical drop re-populated the cache. NOT called on `PosUpdateTenant` — that event fires on every CK model import and eviction there would churn a fresh client per import for no benefit (an update drops no database user). |

### Tenant Setup Retry Store — durable per-service retry

`TenantSetupRetryStore` (`Repositories/TenantLifecycle/`, non-CK collection `tenant_setup_retry` in the
SYSTEM database) holds one document per **(service, tenant)** whose default-configuration setup threw.

Deliberately separate from `tenant_lifecycle`: that record describes the tenant and has a single writer
(the asset repository), whereas *every* service runs its own `SetupTenantAsync` and needs its own retry
bookkeeping. The `serviceId` (the creator's assembly name by default) keys them apart, so two services
can have an independent pending entry for the same tenant.

| Method | Purpose |
|---|---|
| `RecordFailureAsync` | Upsert on failure: `$inc` attempt count, store the error, stamp `lastAttemptUtc`, release the lease so the entry is claimable again after the retry interval. |
| `ClearAsync` | Delete after a successful setup. |
| `TryClaimAsync` | Atomic find-and-update claim of the longest-waiting entry that is inside its attempt budget and whose last attempt is older than the retry interval. |
| `ReleaseLeaseAsync` / `ListAsync` | Lease release and diagnostics. |

Consumed by `DefaultConfigurationCreatorServiceBase` in `octo-common-services`: `SetupAsync` records on
failure and clears on success, and `RetryFailedTenantsAsync` drains the queue from the
`FailedTenantRetryBackgroundService` timer that every service already runs. Entries that exhaust the
attempt budget stay in the collection for operators but are no longer handed out.

Background: before this, a tenant setup that failed once was logged and forgotten — services on the base
creator had no retry at all. A tenant whose database was briefly unreachable right after a delete +
recreate under the same name (Mongo `errorCode 13`) therefore stayed half-provisioned until the pod was
restarted; for Identity, which owns the roles/groups seed, that meant no administrator could be
provisioned for that tenant at all.

### CK Method Handler Snapshot Store (F3.2)

`CkMethodHandlerSnapshotStore` (`Repositories/Methods/`, port `ICkMethodHandlerSnapshotStore` in
`Runtime.Contracts.MongoDb/Methods`, non-CK collection `ck_method_handler_snapshot` in the SYSTEM database) persists the
CK method handler registry of `octo-common-services` (`CkMethodHandlerRegistry`) so a restarted dispatcher host can answer a
call without waiting for the next heartbeat round. One document per (`methodKey`, `instanceId`).

- **Plain collection, not a System CK entity.** It is service bookkeeping with a TTL, not tenant data; a System CK
  element would be a System model change (compatibility statement, downgrade guard) for no gain.
- **Service data only.** The document holds ids, service name, version, contract version, times, the served tenant ids and the
  withdrawal flag, never a parameter value, token or user data (integration test over the stored document with an allow-list).
- **A row is "last seen", not "live".** A reader treats it as live only while `expiresUtc` (receipt time + heartbeat TTL) has not
  passed and `withdrawn` is `false`. `retainUntilUtc` has a MongoDB TTL index (`expireAfterSeconds: 0`), so stale rows leave
  by themselves.
- `LoadAsync` is a read (never materializes the system database). `UpsertAsync` returns `false` and writes nothing while the
  system tenant does not exist and only replaces a row with a strictly newer `lastSeenUtc` (replicas share the
  collection; the duplicate-key outcome of the unique index is the "keep the newer row" case and is ignored).
- Registered by `AddMongoDbRuntimeRepository` (`RuntimeEngineBuilderExtensions`); a host that does not resolve the
  port keeps the registry in memory only.

### Infrastructure-Only Shell of the System Database

The engine's own plumbing writes non-CK bookkeeping into the SYSTEM database — `tenant_lifecycle`,
`tenant_setup_retry`, `display_rule_sweep`, `ck_method_handler_snapshot`, `SysLock` (registry: `InfrastructureCollections`). On a
virgin server such a write may materialize the system database BEFORE the system-tenant bootstrap
runs; in r3.4.93 the bootstrap then refused over the "existing" database and the datasource user was
never created — every fresh install wedged permanently on a MongoDB authentication error.

Rules that keep this closed:

- **Reads and update-only claims never materialize the database.** The stores ensure their indexes
  only on document-creating operations (`GetCollectionAsync(ensureIndexes: ...)`).
- **A database containing nothing but infrastructure collections is a "shell"** —
  `IsDatabaseMaterializedOnlyByInfrastructureAsync`. `IsSystemTenantExistingAsync` reports a shell
  as not existing (checked BEFORE the CK-model read, which would otherwise fail with an
  authentication error on the missing datasource user); `ISystemContext.IsSystemDatabaseBootstrappableAsync`
  answers absent-or-shell; the system bootstrap's refusal guard exempts shells.
- **The seed-decision guard lives in `UpdateSystemCkModelAsync`, not in callers.** Seeding the model
  (as admin) into a shell would make the shell look like a real system database and skip the
  bootstrap — the only creator of the datasource user — forever. The check sits immediately before
  the seed so a shell that materializes after a caller's earlier probe cannot slip through a
  check-then-act window; `EnsureSystemCkModelAsync` deliberately has no guard of its own.
- **The bootstrap rollback drops only what it created from nothing.** `CreateSystemTenantAsync`
  gates `CleanupFailedTenantCreationAsync(dropDatabaseAndUser: ...)` on
  `databaseCreated && !databaseExisted` (mirrors `CreateChildTenantAsync`): a failed
  attempt that started over a pre-existing shell keeps the shell (it holds other services' durable
  bookkeeping — setup-retry queue, lifecycle records, locks), and a racing replica whose create
  throws inside the try can never drop the winner's database.
- **The allowlist is closed.** Any new pre-bootstrap writer into the system database must either use
  a collection registered in `InfrastructureCollections` or run after the bootstrap — anything else
  re-arms the fresh-install wedge. Renames are compile-protected through the shared constants;
  additions are not, so review them explicitly.

Pinned by `SystemTenantVirginBootstrapTests` (isolated `VirginSystemFixture` container).


## Re-ensuring a tenant's MongoDB user

The tenant database user (`OctoSystemConfiguration.DatabaseUser`, e.g. `octo-system-ds-user-<db>`) is created only by
`CreateTenantInternalAsync` and the attach path. If it disappears later (restored or re-created MongoDB, manual
cleanup), every access to that tenant's database fails with `MongoAuthenticationException` (SCRAM) and nothing
re-created the user: a local stack retried the setup of one tenant 617 times in 7 days.

`ISystemContext.EnsureTenantDatabaseUserAsync(tenantId)` closes that gap. It reads the database name from the registry
(no tenant-database access, no CK import — the tenant cannot be opened with its own user, that is the reason it runs),
calls `IAdminRepositoryClient.EnsureUserAsync` (idempotent `CreateUser` that reports whether it created the user) and,
when it had to create it, logs an error naming user, tenant and database and invalidates the cached repository clients
of the tenant. Result: `TenantNotRegistered`, `AlreadyPresent` or `Created`. It needs the admin connection and throws
without it, so callers (the setup retry loop in octo-common-services) call it best-effort.
Test: `EnsureTenantDatabaseUserTests` (drops the user, asserts the authentication failure, ensures, reads again).
