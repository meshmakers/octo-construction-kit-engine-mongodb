using System.Collections.Generic;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

namespace Meshmakers.Octo.Common.Shared.GraphQL;

// ReSharper disable once ClassNeverInstantiated.Global
public class Connection<TDto>
{
    public ICollection<TDto>? Edges { get; set; }

    public ICollection<TDto>? Items { get; set; }
    
    public ICollection<GroupingDto>? Grouping { get; set; }

    public PageInfo? PageInfo { get; set; }
    public int TotalCount { get; set; }
}
