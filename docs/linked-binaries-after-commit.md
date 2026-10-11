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

## Orphaned bytes

A crash (or a failing delete) between the commit and the after-commit delete leaves the **old** bytes in GridFS
without an entity that references them; a crash before the commit leaves the **new** (staged) bytes. Both are
harmless orphans: no entity points to them, and entity reads never miss bytes. They are identified by their
GridFS metadata (`rtEntityId`, content type, binary type `FileSystem`) combined with the fact that the entity with
that `rtEntityId` references a different `BinaryId`. Until an automated sweep exists, orphans can be removed by a
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
`metadata.rtEntityId`, and `fs.files` has no index on that field, so each entity costs a collection scan. The time the
transaction stays open is the limiting factor (MongoDB `transactionLifetimeLimitSeconds`, default 60 s), not memory
(about 150 KB allocated per entry, no measurable working-set growth). An index on `fs.files.metadata.rtEntityId` would make
it linear; that is a separate change.
