using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class SortDto
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string AttributeName { get; set; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public SortOrdersDto SortOrder { get; set; }
}
