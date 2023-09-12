namespace Meshmakers.Octo.SystematizedData.Persistence;

public interface IBulkImportResult
{
    long InsertedCount { get; }
    long DeletedCount { get; }
    long ModifiedCount { get; }
    bool HasError();
}