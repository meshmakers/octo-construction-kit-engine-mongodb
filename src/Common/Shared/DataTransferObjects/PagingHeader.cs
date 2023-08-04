namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class PagingHeader
{
    public PagingHeader(
        long totalCount, int skip, int take)
    {
        TotalCount = totalCount;
        Skip = skip;
        Take = take;
    }

    public long TotalCount { get; }
    public int Skip { get; }
    public int Take { get; }
}
