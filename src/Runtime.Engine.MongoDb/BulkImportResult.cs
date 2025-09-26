using Meshmakers.Octo.Runtime.Contracts.Repositories;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

public class BulkImportResult(BulkWriteResult? bulkWriteResult) : IBulkImportResult
{
    public long InsertedCount => bulkWriteResult?.InsertedCount ?? 0;
    public long DeletedCount => bulkWriteResult?.DeletedCount ?? 0;
    public long ModifiedCount => bulkWriteResult?.ModifiedCount ?? 0;

    public bool HasError()
    {
        return (!bulkWriteResult?.IsAcknowledged) ?? false;
    }
}
