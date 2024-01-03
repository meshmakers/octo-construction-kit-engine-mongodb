using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

public class AggregatedBulkImportResult
{
    private readonly IEnumerable<IBulkImportResult> _bulkWriteResult;

    public AggregatedBulkImportResult(IEnumerable<IBulkImportResult> bulkWriteResult)
    {
        _bulkWriteResult = bulkWriteResult;
    }

    public long InsertedCount => _bulkWriteResult.Sum(x => x.InsertedCount);
    public long DeletedCount => _bulkWriteResult.Sum(x => x.DeletedCount);
    public long ModifiedCount => _bulkWriteResult.Sum(x => x.ModifiedCount);

    public bool HasError()
    {
        return _bulkWriteResult.Any(x => x.HasError());
    }
}