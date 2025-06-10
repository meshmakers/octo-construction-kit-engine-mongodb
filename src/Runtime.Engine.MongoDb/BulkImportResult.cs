using Meshmakers.Octo.Runtime.Contracts.Repositories;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

public class BulkImportResult(BulkWriteResult bulkWriteResult) : IBulkImportResult
{
    public long InsertedCount => bulkWriteResult.InsertedCount;
    public long DeletedCount => bulkWriteResult.DeletedCount;
    public long ModifiedCount => bulkWriteResult.ModifiedCount;

    public bool HasError()
    {
        return !bulkWriteResult.IsAcknowledged;
    }
}
