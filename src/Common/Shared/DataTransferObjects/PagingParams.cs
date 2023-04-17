// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class PagingParams
{
    public int Skip { get; set; }
    public int Take { get; set; }

    public string? Filter { get; set; }
}
