using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class FieldFilterDto
{
    public string? AttributeName { get; set; }

    [JsonConverter(typeof(StringEnumConverter), typeof(ConstantCaseNamingStrategy))]
    public FieldFilterOperatorDto Operator { get; set; }

    public object? ComparisonValue { get; set; }
}
