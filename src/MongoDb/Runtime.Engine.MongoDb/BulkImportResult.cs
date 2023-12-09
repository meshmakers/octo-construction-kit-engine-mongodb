using Meshmakers.Octo.Runtime.Contracts.Repositories;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

public class BulkImportResult : IBulkImportResult
{
    private readonly BulkWriteResult _bulkWriteResult;

    public BulkImportResult(BulkWriteResult bulkWriteResult)
    {
        _bulkWriteResult = bulkWriteResult;
    }

    public long InsertedCount => _bulkWriteResult.InsertedCount;
    public long DeletedCount => _bulkWriteResult.DeletedCount;
    public long ModifiedCount => _bulkWriteResult.ModifiedCount;

    public bool HasError()
    {
        return !_bulkWriteResult.IsAcknowledged;
    }
}