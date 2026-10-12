# Linked binaries (GridFS) and transactions

GridFS is not part of the MongoDB transaction of an `IOctoSession`. To keep bytes consistent with the
entities (AB#6248), `MongoLinkedBinaryDataSource` defers destructive GridFS work while a transaction is active.
The rule is: **the committed entity must always reference existing bytes; leftover bytes are acceptable, missing
bytes are not.**

| Operation (transaction active) | Behaviour |
|---|---|
| `DeleteAllFileSystemBinariesAsync` (entity delete / replace) | Bytes are deleted **after the commit**. Rollback or disposing the session without commit keeps them. |
| `ReplaceFileSystemBinaryAsync` with an existing `BinaryId` | The new bytes are uploaded under a **new id, which is returned**. The engine (`BulkRtMutation`) writes that id into the entity before the entity write, i.e. inside the transaction. The old bytes stay readable until the commit and are deleted **after the commit**. Rollback (or a session disposed without commit) deletes the new bytes; the old bytes and the old `BinaryId` stay valid. There is no step after the commit that modifies the entity. |
| `UploadFileSystemBinaryAsync` | Bytes are uploaded immediately; removed again if the transaction is rolled back. |
| Temporary binaries | Unchanged (immediate). |
| No active transaction | Unchanged (immediate); a replace keeps the id. |

Callers of `ReplaceFileSystemBinaryAsync` must store the returned id (the engine contract changed in
`octo-construction-kit-engine`, see AB#6248): after a replace the `BinaryId` of the entity is a different value.

Mechanism: `IOctoSessionInternal.RegisterTransactionCallbacks(afterCommit, afterRollback)` on `OctoSession`.
Callbacks run once; a failing callback is logged and never thrown (the database state is already final).

Edge cases (all covered by tests):

| Situation | Behaviour |
|---|---|
| Abort (or dispose without commit) whose abort call throws | The rollback callbacks still run (`finally`): staged bytes are removed. |
| `CommitTransactionAsync` throws | The outcome is unknown (the commit may have reached the server). **Neither callback set runs**, also not on a later abort/dispose: deleting the staged bytes of a transaction that did commit would leave the entity without its bytes. Staged bytes may stay behind as orphans. |
| Crash after the commit, before the after-commit delete | Old bytes stay as orphans; the entity references the complete new bytes. |
| Two sessions replace the same entity | The second hits a write conflict and aborts; its staged bytes are removed. |
| Stale `BinaryId` (the file was already replaced by another session) | The new file is stored with the owner (`rtEntityId`) of the entity being updated, so an entity delete finds it. |
| `BinaryId` that belongs to another entity | Refused as not found (AB#6540); that entity's bytes are untouched. A file without owner metadata is accepted. |
| Update guard (`AttributeNewerThanGuard`) together with a linked binary | Refused by the engine (`BulkRtMutation`), because a non-matching guard skips the entity write silently. |

## Orphaned bytes

A crash (or a failing delete) between the commit and the after-commit delete leaves the **old** bytes in GridFS
without an entity that references them; a crash before the commit leaves the **new** (staged) bytes. Both are
harmless orphans: no entity points to them, and entity reads never miss bytes. They are identified by their
GridFS metadata (`rtEntityId`, content type, binary type `FileSystem`) combined with the fact that the entity with
that `rtEntityId` references a different `BinaryId`. The automated sweep is **AB#6541** (it must exist before the file migration sweep AB#6183 is switched on). Until then, orphans can be removed by a
maintenance job that deletes `fs.files` documents of binary type `FileSystem` whose `_id` is not referenced by the
linked-binary attribute of the entity named in `metadata.rtEntityId` (and the matching `fs.chunks`). The cleanup
must run outside active transactions and only for files older than the longest transaction timeout, so that bytes
staged by an in-flight transaction are never removed.

No persistence or CK schema change: the `BinaryId` is already stored in the entity's linked-binary attribute and
GridFS metadata is carried over unchanged. Existing entities and their `BinaryId`s stay readable.

## Cost of a large delete (measurement, AB#6248)

`tests/.../LargeBinary/DeleteCapMeasurementTests.cs` (explicit, runs only with `OCTO_MEASURE_DELETE_CAP=500,1000,...`,
results appended to `OCTO_MEASURE_OUT`) deletes N entities that each own a 4 KB linked binary in **one** transaction -
the shape of the asset-repo recursive folder delete. Local run (Mac, Testcontainers MongoDB replica set, 2026-10-11):

| Entries | Transaction open (apply) | Commit + deferred GridFS delete | Total |
|---:|---:|---:|---:|
| 500 | 0.5 s | 0.7 s | 1.2 s |
| 1,000 | 0.8 s | 1.2 s | 1.9 s |
| 2,000 | 2.4 s | 2.7 s | 5.0 s |
| 5,000 | 11.1 s | 7.1 s | 18.2 s |
| 10,000 | 36.1 s | 13.8 s | 49.9 s |
| 20,000 (before the change: transaction aborted by the 60 s MongoDB transaction lifetime) | - | - | failed |

The time is not linear: `DeleteAllFileSystemBinariesAsync` looks up the bytes of every entity by
`metadata.rtEntityId`. The measurements above were taken **without** an index on that field, so each entity cost a
collection scan. The time the transaction stays open is the limiting factor (MongoDB `transactionLifetimeLimitSeconds`,
default 60 s), not memory (about 150 KB allocated per entry, no measurable working-set growth).

### Owner index (AB#6538)

Measured with `DeleteCapMeasurementTests` on the same machine and database (4 KB files; `OCTO_MEASURE_OWNER_INDEX=off|on`
drops or creates the index before the run):

| Entries | Transaction open (apply), no index | Transaction open (apply), with index | Commit + deferred GridFS delete (with index) | Total with index |
|---:|---:|---:|---:|---:|
| 5,000 | 11.7 s | 2.3 s | 5.7 s | 7.9 s |
| 10,000 | 37.4 s | 4.4 s | 12.2 s | 16.6 s |

With the index the time inside the transaction grows linearly (2.3 s to 4.4 s for twice the entries) and the 10,000-entry
transaction stays open for 4.4 s instead of 37.4 s. The after-commit delete (12.2 s) is outside the transaction. The
asset-repo limit `FilesOptions.MaxDeleteEntries` (5,000) is **not** changed by this work: raising it is a decision of the
product owner (the total of 16.6 s at 10,000 entries is a 3.6x margin to the 60 s transaction lifetime, the story asks for
more than 4x). These numbers were not taken on the restored sbeg-sized tenant.

The tenant index setup (`UpdateIndexAsync`, a full update that is not scoped to one model; it runs when a tenant is
created and on every index update) creates the index `metadata_rtEntityId_1` on `fs.files.metadata.rtEntityId`. It is
**sparse**, because temporary binaries have no owner. Creating an index with an identical definition is a no-op, so the
setup can be repeated and run concurrently. The owner lookups that use it are `DeleteAllFileSystemBinariesAsync` and the
orphan sweep (AB#6541). Existing tenants get the index with the next full index update after the engine-mongodb release;
building the index on a very large `fs.files` takes time (MongoDB builds it online; plan the first index update of such a tenant outside peak load).
