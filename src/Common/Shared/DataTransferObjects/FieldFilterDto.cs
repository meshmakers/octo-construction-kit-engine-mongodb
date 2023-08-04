using System.Text.Json.Serialization;

// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class FieldFilterDto
{
    public string AttributeName { get; set; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FieldFilterOperatorDto Operator { get; set; }

    public object? ComparisonValue { get; set; }
}
