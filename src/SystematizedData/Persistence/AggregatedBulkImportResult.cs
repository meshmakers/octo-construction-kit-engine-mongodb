using System.Collections.Generic;
using System.Linq;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class AggregatedBulkImportResult
{
    private readonly IEnumerable<BulkImportResult> _bulkWriteResult;

    public AggregatedBulkImportResult(IEnumerable<BulkImportResult> bulkWriteResult)
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
