using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class SortDto
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string? AttributeName { get; set; }

    [JsonConverter(typeof(StringEnumConverter), typeof(ConstantCaseNamingStrategy))]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public SortOrdersDto SortOrder { get; set; }
}
