using System.Collections.Generic;
using System.Linq;

// ReSharper disable MemberCanBePrivate.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class PagedResult<T>
{
    public PagedResult(IEnumerable<T> source, int? skip, int? take, long totalCount)
    {
        TotalCount = totalCount;
        Skip = skip;
        Take = take;
        List = source.ToList();
    }

    public PagedResult(IEnumerable<T> source)
    {
        List = source.ToList();
        TotalCount = List.Count();
    }

    public long TotalCount { get; }
    public int? Skip { get; }
    public int? Take { get; }
    public ICollection<T> List { get; }

    public PagingHeader? GetHeader()
    {
        if (Skip.HasValue && Take.HasValue)
        {
            return new PagingHeader(
                TotalCount, Skip.Value,
                Take.Value);
        }

        return null;
    }
}
