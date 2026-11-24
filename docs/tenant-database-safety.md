---
description: Tenant delete as two-phase drop, tenant namespace gate, database ownership marker
applies_to: src/Runtime.Engine.MongoDb/Repositories/TenantOwnership/**, src/Runtime.Contracts.MongoDb/TenantOwnership/**, src/Runtime.Engine.MongoDb/Repositories/TenantLifecycle/**
---

### Race-safe Tenant Delete — Two-phase Drop

A tenant delete must **delete the tenant metadata record and commit it before dropping the physical
database**. The physical `dropDatabase` is a DDL op that runs *outside* the caller's MongoDB
transaction (Mongo can't drop a database inside a multi-document transaction), so it takes effect
immediately, whereas the `RtTenant` record deletion only becomes visible to other sessions on commit.
The original order (drop DB at `DropChildTenantAsync` → delete record → caller commits) left a window
— measured at ~180 ms live — in which the tenant record was still committed-visible while the
database was already gone. Any concurrent tenant-resolve in that window
(`TryGetChildTenantContextAsync` from a nightly consumer, a health check, or the pre-notification of
an immediately following Create) finds the record, resolves the context, and the **auto-import on
resolve** (`UpdateSystemCkModelAsync` / `EnsureServiceManagedCkModelsImportedAsync` /
`EnsureStreamDataCkModelImportedAsync`) writes `CkModel` into the tenant DB — MongoDB auto-creates the
database (`CkModel` + `SysLock` skeleton). The just-dropped database is resurrected, and the next
tenant `Create` aborts on `IsDatabaseExistingAsync` with *"Tenant database '…' does already exist"*,
rolls back, and leaves an orphan DB with no tenant record → every re-run of `om_initialize_tenant`
deadlocks identically.

`DropChildTenantAsync` is therefore split into two composable phases on `ITenantContext`:

| Method | Runs in caller's txn? | Does |
|---|---|---|
| `DeleteChildTenantMetadataAsync(session, tenantId)` | yes | Pre-delete notification + delete `RtTenant` from current & system repos. Returns a `TenantDeletionHandle(DatabaseName, CorrelationId)`. |
| `DropTenantDatabaseAsync(handle, tenantId)` | no (DDL) | Physical `dropDatabase` + post-delete notification at once (reusing the handle's correlation id; the caller committed the metadata delete before). `DropChildTenantAsync` publishes it after the caller's commit instead, see "Tenant-deleted notification after the commit". |

The tenant delete REST endpoint (`TenantsController.Delete`) now calls
`DeleteChildTenantMetadataAsync` → `CommitTransactionAsync` → `DropTenantDatabaseAsync`, so the record
is durably gone before the drop and no resolve can resurrect the DB. `DropChildTenantAsync` is kept as
a single-call convenience (`DeleteChildTenantMetadataAsync` + `DropTenantDatabaseAsync`, record-delete
now ordered before the drop) for callers with no concurrent resolver: create-rollback
(`CreateChildTenantAsync` blueprint path), `ClearChildTenantAsync`, `TenantBackupService` temp cleanup,
and the integration tests. Complements the AB#4294 guard invalidation above (which handles the
*same-process delete+recreate* import-guard, a different facet of the same resurrection mechanism).

`DropTenantDatabaseAsync` additionally **drops the tenant's MongoDB user** and drops the database
under **both spellings** — normalized and as stored in the record (AB#4762). Both matter:
`dropDatabase` does *not* remove the account — it lives in the authentication database — so every
delete used to leave a live credential behind that a database re-created under the same name would
silently inherit; and `dropDatabase` is case-sensitive while `IsRepositoryExistingAsync` compares
case-insensitively, so dropping only one spelling missed either the normalized physical database of a
mixed-case record or the mixed-case physical database a legacy attach adopted. MongoDB forbids two
databases differing only in case, so at most one spelling exists. The user drop is best-effort: it is
logged, never allowed to fail the delete.

**Dropping a tenant for good also drops the CrateDB tables of its archives** (AB#4255) — but only
when the caller says so. `DeleteChildTenantMetadataAsync` / `DropChildTenantAsync` take
`dropStreamData` (default **false**): with `true` the archives of the child are collected into the
`TenantDeletionHandle.StreamDataArchives` *before* the record is deleted (the last moment the child
resolves — the entities are gone with the database), and `DropTenantDatabaseAsync` drops exactly those
archives' tables (data table, `__genmap` side-table and `__rc` recompute staging table, each `DROP TABLE IF EXISTS`, all statuses) through
`IStreamDataRepositoryFactory.DeleteArchiveTablesAsync(tenantId, rtIds)` after the Mongo drop. The
guard in `DisableStreamDataAsync` (see *StreamData: Archives and Rollups*) only ensures nothing is
*live* by then. Who passes `true`: the tenant delete REST endpoint and `ClearChildTenantAsync` (Clear
empties the tenant; its archive entities go with the database, keeping their tables would only orphan
them). Who keeps the default: `TenantBackupService.RestoreTenantAsync` (restore over an existing
tenant is a *database swap* — the same archives exist afterwards and find their tables again, so a
Mongo-only restore keeps the stream data), the blueprint create-rollback, the deleting-settle sweep in
octo-common-services (builds its handle from the lifecycle record, no archives) and the test cleanups.
`DetachChildTenantAsync` keeps everything for a later attach.

The tenant-level state of the restore reconcile feature goes too (AB#6968): `IStreamDataRepositoryFactory.DeleteTenantStateAsync`
deletes the tenant's rows from `__restore_reconcile` (marker) and `__rollup_repair` (queue) and drops such a table only when it is
empty afterwards, because the schema is shared (rows are keyed by the full tenant id); a tenant re-created under the same id must not
inherit a set marker. The per-archive `__sweep` record tables are `ArchiveTableDrop`'s. It runs when the tenant had the stream data
model loaded (`TenantDeletionHandle.StreamDataModelPresent`), also with no archive left; a tenant without the model never touches CrateDB.

Two rules of that drop are deliberate. **Per-archive, never "everything in the tenant's schema"**:
`TenantSchema.SchemaName` strips `-` and `_` from the tenant id, so `acme-corp`, `acme_corp` and
`acmecorp` share one CrateDB schema — a schema-wide drop (the previously unused
`DeleteStreamDataDatabaseAsync`) would take a neighbour's tables with it. **Best-effort like the user
drop**: skipped (with a warning naming the tables) when no factory is registered or `StreamData:Enabled`
is false at instance level; a failure is an ERROR log listing the `archive_<rtId>` tables to drop by hand
(every statement is idempotent). It runs only after the Mongo drop succeeded — when `dropDatabase`
itself throws, the user drop and the table drop are both skipped and the exception propagates — and it
goes through the CrateDB resilience pipeline: the factory stops at the first failure, so with CrateDB
unreachable a Delete/Clear of a tenant *with the stream data model* (also one without archives, because of the tenant state
cleanup above) blocks for up to ~2 min (timeout × retries) before logging the error; it never fails the drop, and tenants
without the stream data model never touch CrateDB.

**The resurrection window is not fully closed, and since AB#4762 it bites harder.** Background work
that outlives the delete — above all a `tenant_setup_retry` entry, whose retry loop keeps calling
`SetupAsync` for a tenant that no longer exists — re-creates the database as an empty `CkModel` +
`SysLock` shell seconds after the drop. The create path no longer reclaims such a shell (it used to,
by dropping it — that was the AB#4762 data-loss bug), so the leftover permanently blocks its own
database name behind a deliberately reason-free conflict. `TenantsController.Delete` therefore clears
the tenant's setup-retry entries via `ITenantSetupRetryStore.ClearAllForTenantAsync`. A peer service
still racing its own CK-model import into the tenant can produce the same shell; when a database name
is refused and nothing seems to own it, look for that shell and remove it.

### Tenant Namespace Gate — one authority for tenant ids and database names (AB#4762 / AB#4763)

`EnsureTenantNamespaceAvailableAsync` is the single gate for both platform-wide namespaces, shared by
`CreateChildTenantAsync` and `AttachChildTenantAsync`. It runs **before any side effect** — including
the pre-create notification — because everything after it sits inside the `try` whose `catch` rolls the
tenant back, and that rollback used to drop whatever database the name pointed at.

Checks, in order: the configured system tenant id (reserved — the system tenant has no `RtTenant`
self-record, so a registry lookup alone cannot cover it), the configured system database name,
MongoDB's own `admin` / `local` / `config`, the instance's tenant-database prefix (AB#8797, see
*Tenant-Database Prefix* below), the platform-wide registry by tenant id, the registry by
database name, and finally physical existence — required for attach, forbidden for create.

Two rules to preserve when touching it:

- **Both conflicts are uniform and reason-free.** Exactly two conflict messages exist —
  `Tenant ID '<x>' is already in use.` and `Database name '<x>' is not available.` — and every reason
  collapses into one of them, including "does not exist" on the attach path. A distinguishable answer
  turns the endpoint into a cluster-wide existence oracle for callers who cannot see the colliding
  resource. The real reason goes to the log, never to the response, and rejections are **logged, not
  audited**: an audit event per rejected attempt would be an unbounded write amplifier into the system
  database, since nothing rate-limits the callers. (Format validation is different: a tenant id or
  database name that is syntactically invalid throws a descriptive `ArgumentException` before the
  conflict checks — that reason is about the caller's own input, leaks nothing about other tenants,
  and both REST endpoints map it to 400, not 409.)
- **The rollback is gated on `databaseCreated`**, a local set only after `CreateTenantInternalAsync`
  returns. The exists-check inside that method is kept as a TOCTOU net and deliberately throws while
  the flag is still `false`, so a racer's database is never dropped.

Registry lookups (`GetRtSystemTenantAsync`, `GetRtSystemTenantByDatabaseNameAsync`) query the
**platform-wide** registry via `GetSystemTenantRepositoryAsAdmin()`; the subtree-local
`GetRtTenantAsync` is a different thing and the two must never be swapped — they were byte-identical
copies, which is what made the "global" uniqueness check blind (AB#4763). Neither imposes a sort
order: which row wins decides which database a tenant resolves to and which one a delete drops, so
ordering already-ambiguous rows could move a live tenant. Ambiguity is **logged as a warning**
instead, and the registry deletes in `DetachChildTenantAsync` / `DeleteChildTenantMetadataAsync` are
qualified with the database name so a leftover duplicate cannot make them unregister a different
parent's tenant. The qualification matches the name **as stored in the registry being deleted from**
(verbatim for the subtree-local record — the pre-AB#4763 attach wrote the operator's raw casing
there — and normalized for the system registry, which has always been written normalized); a
normalized filter against the local registry silently missed every legacy mixed-case record. Note the
gate prevents new duplicates but nothing at the database level enforces uniqueness (the Tenant CK
type's `TenantId` index is not unique), so two creates racing the gate in separate transactions can
still both commit — `FirstAndWarnOnDuplicates` surfaces the result.

Detach removes the platform-wide row too, so detach and attach are exact inverses. Without that the
now-global id check would reject every re-attach, and a "detached" tenant stayed fully resolvable from
the system context.

### Tenant Database Ownership Marker — cross-instance attach guard (AB#4945 / Epic AB#4944)

The namespace gate's registry lookups only cover the OWN instance's system database. On a shared
MongoDB server a **second OctoMesh instance** (different `SystemDatabaseName` — the instance
separator per Epic AB#4944) could therefore attach a tenant database this instance still owns:
two controllers, trigger schedulers and lifecycle watchdogs on one tenant DB — split-brain.

`TenantOwnershipStore` (`Repositories/TenantOwnership/`, non-CK collection `tenant_ownership` in
the **tenant database itself**, one document with fixed id `owner`) closes that gap. The marker
travels with the database, so every instance that can physically reach it can read who owns it.
Instance identity = the normalized `SystemDatabaseName` (ordinal compare).

Lifecycle:

| Point | Effect |
|---|---|
| `CreateChildTenantAsync` | Stamps right after the physical create — owned from birth; a stamp failure fails the create and the rollback drops the database we provably created. |
| `AttachChildTenantAsync` | The gate consults the marker (attach mode, after the existence check): foreign owner → **uniform, reason-free rejection** per the AB#4763 rule (the owner is logged, never returned — no cross-instance oracle). **STRICT by decision: no force override** — the owning instance must detach first. On pass, the attach claims first thing in the try with `TenantOwnershipStore.ClaimAsync` (AB#8796): another instance's marker is never overwritten - when another instance attaching the same unmarked database won the claim between the gate's read and this write, the attach is refused with the same uniform conflict. **The claim is part of the caller's transaction** (every caller starts one): an own marker is re-stamped and a missing one inserted inside it, so the claim commits together with the registry rows, an abort takes it back (no rollback ever has to remove a claim another attach might rely on), and a concurrent attach of the same database - by this or another instance - collides with the uncommitted claim (write conflict) and is refused with the uniform conflict. Only without a transaction is the claim a plain write that the catch releases (`TryRemoveClaimAsync`: only while it is still exactly this call's claim - owner, tenant and claim time from `NewClaimTime` - and only when no own marker was there before). |
| `DetachChildTenantAsync` | Removes the marker LAST inside the try — the one sanctioned ownership handover. Deliberately not best-effort: a removal failure aborts the caller's transaction, the registry rows are restored, and the database stays consistently attached here. Removes it **only when it names this instance** (`RemoveIfOwnedByAsync`, AB#8796): detaching a double-registered tenant in the non-owning instance leaves the owner's claim intact. |
| Tenant-resolve (`TryGetChildTenantContextAsync`) | Lazy claim: stamps insert-if-absent (once per process per tenant, guard cleared by `ClearTenantResolveImportGuards`), so the existing fleet becomes owned without a migration script. Insert-if-absent means a pre-guard double attachment across two instances is won by the first writer and never flaps. AB#8796: the stamp returns the marker (`StampIfAbsentAndGetAsync`); a marker of ANOTHER instance logs one warning per process and tenant and is recorded in `TenantOwnershipConflictTracker` (`ITenantOwnershipConflicts`), which turns `TenantContextHealthCheck` **Degraded** with tenant, database and owner. Resolve is never refused, the marker never overwritten; `InvalidateTenantResolveImportGuards` and detach clear the conflict. A recorded conflict is rechecked against its database at most once per `TenantOwnershipConflictTracker.RecheckInterval` (one minute) by the health check (`ITenantOwnershipConflicts.RecheckAsync`) and by every resolve of the conflicting tenant: while the other instance's marker stays, the conflict and Degraded stay; once the other instance detached the tenant (its marker is gone), the recheck claims the database insert-if-absent for this instance and forgets the conflict, so the health returns to Healthy. |
| `ClearChildTenantAsync` / delete | Delete drops the database and the marker with it, **after the delete guard (AB#8807, below)**; Clear goes through the same delete and re-stamps via its internal `CreateChildTenantAsync`. |

The collection is deliberately NOT in `InfrastructureCollections` — that registry is the
system-database shell allowlist (AB#4854), and the marker is never written into a system database
(the gate reserves the system database name before any marker access). Reads never materialize a
database: every caller operates on a database whose existence the gate has already established. The one
write that may materialize one is the restore's claim on a new target name (AB#8672), which a failed
restore drops again.

**Cross-instance registrations (AB#8796).** Databases from before the marker carry none, so the
marker alone lets another instance adopt them. `TenantDatabaseRegistrationLookup` (same folder) reads
the `RtEntity_SystemTenant` registry of every database on the server with the admin client and answers
which **system databases** register a database. A system database = a database with the
system-database marker (AB#8672, `SystemDatabaseMarkerStore`: collection `system_database_marker` in
the system database itself, listed in `InfrastructureCollections` so a marker-only shell stays
bootstrappable; stamped insert-if-absent by `CreateSystemTenantAsync` and by `EnsureSystemCkModelAsync`
over an existing system tenant — identity calls it on every start — best effort, a failed stamp only
logs), or, as the fallback for instances without the marker, a database without ownership marker whose
registry has at least one row (for another database) that carries a `parentTenantId`. The system
registry writes the parent id into every row, the parent-local registry of a parent tenant never does,
so a parent with children is never taken for a system database — attached or detached (a detached
parent has no marker and no registration anywhere; the earlier rule "not registered by any other
registry" refused every re-attach and restore of a tenant with children), and a corrupt own row that
registers another instance's system database does not hide it. A system database whose rows all predate
`parentTenantId` is recognised by its marker only (every instance stamps it on start). The marker
recognises the system database of an instance without any tenant. Accepted gap (Odin 2026-11-11): the system
database of an instance that runs an older version and has no tenant carries neither signal, so it is not
recognised; rollout rule: upgrade all instances on one MongoDB server together. This snapshot is the ONE
classification; attach, restore and delete all decide with `TenantDatabaseForeignOwnership.FindAsync`
on top of it (attach: any foreign owner -> the same uniform conflict, no override). The lookup takes a fresh snapshot per call — admin paths only, never on
resolve. `ISystemContext.ReviewTenantOwnershipAsync(session, stamp)` (`TenantOwnershipReview`) lists
the own registry's databases that are `Unmarked`, `MarkedByOtherInstance`, `RegisteredByOtherInstance` or
`SystemDatabase` (a corrupt row naming a system database, checked first) and with `stamp` writes the marker only
for `Unmarked` ones (no marker, registered only here, not a system database; insert-if-absent): it never writes
into another instance's system database or a database another instance registers. Missing databases are
counted, never stamped. Asset repo:
`POST system/v1/tenants/review-ownership`; octo-cli `ReviewTenantOwnership [--stamp]`.

**Restore guard (AB#8672).** `TenantBackupService.RestoreTenantAsync` checks the target before any
write (before `dropExistingTenant` and mongorestore; the "registered to another tenant of this instance" check runs
there too now, so that refusal drops nothing either): another instance's system database is refused and
never overridable; a database whose ownership marker names another instance, or that another system
database registers, is refused naming the owner. `overrideForeignOwnership` lets the restore overwrite
such a tenant database with an `OWNERSHIP OVERRIDE` warning — except a database another instance still
registers when `attachTenant` is true: the attach gate above would refuse it after the data was
replaced, and a successful attach would register one database in two instances. The override covers the
checked target only: with `dropExistingTenant`, the existing tenant's database is dropped under the override only
when it IS the target; a different existing database goes through the delete guard without it.
The check only reads, so before the first destructive step the restore **claims** the target (`RestoreClaim`:
insert-if-absent marker of this instance with a claim token; under a granted override the foreign marker is taken
over, only while it is still the one the check saw) and holds the claim through the whole restore: another
instance can neither attach nor restore the database in between (a claim it won in between refuses this restore
with the uniform conflict before anything is dropped). A target that is the dropped existing tenant's database is
claimed again right after that drop. mongorestore never restores the archive's markers (`MongoRestoreOptions.NsExclude`
= `*.tenant_ownership`, `*.system_database_marker`), so the claim survives `--drop` and a backup of another instance
(or of a system database) never looks like that instance's database. The attach at the end takes the claim over in
its transaction; a restore without attach releases it (the database stays unowned like any detached one); a failed
restore releases a claim it inserted and drops a database its claim materialized and nothing else got into, so a
failed restore into a new name leaves no empty database behind. "Restored no data" is detected as "holds nothing but
markers" (`HoldsOnlyMarkersAsync`).

**Delete guard (AB#8807).** `DeleteChildTenantMetadataAsync` (and so `DropChildTenantAsync`, the REST
two-phase delete and `ClearChildTenantAsync`) calls `EnsureDatabaseNotOwnedByAnotherInstanceAsync`
right after the stale-row check, before the pre-delete event and any write of the engine. The asset repository's
REST delete writes its `Deleting` lifecycle tombstone before it calls the engine; after a refusal (409) the
tombstone stays and the delete settle sweep rolls it back once the settle period has passed, because the tenant
is still fully registered (until then a re-create of that tenant id answers 409). `DropTenantDatabaseAsync` checks
the same decision again right before the physical drop, for every caller - also the settle sweep in
common-services, which re-drops a resurrected database from a `TenantDeletionHandle` built out of the lifecycle
record after only a local lookup: a database claimed by another instance in the meantime is never dropped (uniform
conflict, database untouched; cache invalidation and post-delete event still run). This refusal comes after the
caller committed the metadata deletion, so the exception carries `TenantException.IsDatabaseDropPending` (also
`IsConflict`, same uniform text): the tenant record is gone, the database stays and the tombstone stays for the
sweep, which retries the drop each tick. The asset repository's REST delete answers it with a 409 that says so
(record deleted, database left untouched, delete pending); `DropChildTenantAsync`, whose metadata deletion is not
committed yet, turns it back into the plain conflict. After the read-only check the drop **reserves** the database
until the drop (insert-if-absent marker of this instance; the marker goes with the database): an unmarked database
can no longer be attached by another instance between the check and the drop, and a claim another instance won in
between refuses the drop. Nothing is written into a database the check found foreign. Only an override the metadata
phase granted (`TenantDeletionHandle.ForeignOwnershipOverridden`) passes there, and only while the database is
still merely an orphaned foreign marker (re-checked at every drop; the marker is then taken over for the drop). The
REST delete records the granted override in the tombstone (`TenantLifecycleRecord.ForeignOwnershipOverridden`,
`ITenantLifecycleStore.EnsureDeletingAsync(..., foreignOwnershipOverridden)`) BEFORE it commits the metadata
deletion, and the settle sweep builds its handle with it, so a delete interrupted between the commit and the drop is
finished with the same decision; `MarkDeletingAsync` resets it for a new delete.
`TenantDatabaseForeignOwnership.FindAsync` (same folder; also the decision of the attach gate and of the
restore guard, whose `CheckForeignOwnershipAsync` only words the restore's messages) decides: another instance's system database, a marker
naming another instance, or another system database registering the database (also the owner side of a
double registration) -> uniform `DatabaseNameNotAvailable` conflict, the owner in the log only.
`overrideForeignOwnership: true` drops a database that only carries an orphaned foreign marker (no other
instance registers it) and logs `OWNERSHIP OVERRIDE ...` with the owner; another instance's system
database and a database another live instance still registers are never dropped
(`TenantDatabaseForeignOwner.CanBeOverridden`). A corrupt own row that registers another instance's
system database does not hide it: the parent-id rule of the registry snapshot recognises it. Tests: `DeleteOwnershipGuardTests` (TwoInstance collection).

Tests: `TenantOwnershipGuardTests` (create stamps; detach removes + re-attach re-stamps;
foreign-owned attach conflicts uniformly with the foreign claim untouched; unstamped legacy DB is
adopted and stamped; tenant-resolve lazily stamps an unmarked DB). Two real instances on one server
(`TwoInstanceFixture`, collection `TwoInstance`): `TenantDatabaseRegistrationLookupTests`,
`TenantOwnershipCrossInstanceTests` (unmarked attach refused/allowed, other system database refused,
resolve warning once + Degraded + detach in the non-owner keeps the marker, the attach claim inside the
transaction (concurrent attach refused, abort releases it), Degraded rechecked: persists, bounded by the interval,
Healthy once the owner detached, idempotent list/stamp with the ambiguous databases untouched),
`RestoreOwnershipGuardTests` (incl. the restore claim against a concurrent attach of the other instance before and
after mongorestore, and a failed restore that releases its claim and its materialized database), `SystemDatabaseMarkerTests` (fresh
instance without tenants recognised and its system database refused as restore target, idempotent
startup stamp with the registry rule as fallback, marker removed from a restored copy, a failing stamp still
starts the instance and reports Degraded until the next stamp).

**System-database marker stamp failure (AB#8672).** `SystemContext.TryStampSystemDatabaseMarkerAsync` (bootstrap
and every `EnsureSystemCkModelAsync` over an existing system tenant) never fails the start: a failed stamp logs a
warning and sets `ITenantOwnershipConflicts.SystemDatabaseMarkerProblem`, so `TenantContextHealthCheck` reports
`Degraded` with the reason; the next successful stamp clears it.
