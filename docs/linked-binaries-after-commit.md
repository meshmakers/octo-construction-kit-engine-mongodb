# Linked binaries (GridFS) and transactions

GridFS is not part of the MongoDB transaction of an `IOctoSession`. To keep bytes consistent with the
entities (AB#6248), `MongoLinkedBinaryDataSource` defers destructive GridFS work while a transaction is active:

| Operation (transaction active) | Behaviour |
|---|---|
| `DeleteAllFileSystemBinariesAsync` (entity delete / replace) | Bytes are deleted **after the commit**. Rollback or disposing the session without commit keeps them. |
| `ReplaceFileSystemBinaryAsync` with an existing `BinaryId` | New bytes are uploaded under a staging id; after the commit the staging file is re-keyed to the original id (metadata only) and the old bytes are removed. Rollback removes the staging file; the old bytes stay readable. |
| `UploadFileSystemBinaryAsync` | Bytes are uploaded immediately; removed again if the transaction is rolled back. |
| Temporary binaries | Unchanged (immediate). |
| No active transaction | Unchanged (immediate). |

Mechanism: `IOctoSessionInternal.RegisterTransactionCallbacks(afterCommit, afterRollback)` on `OctoSession`.
Callbacks run once; a failing callback is logged and never thrown (the database state is already final), so
a crash or failure between commit and cleanup can leave orphaned bytes, never missing ones.
