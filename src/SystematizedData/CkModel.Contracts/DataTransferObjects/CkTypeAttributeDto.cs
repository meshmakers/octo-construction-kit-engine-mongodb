using System.Diagnostics;
using System.Text.Json.Serialization;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

[DebuggerDisplay("{" + nameof(AttributeId) + "} -> {" + nameof(AttributeName) + "}")]
public class CkTypeAttributeDto
{
    [JsonPropertyName("id")]
    [JsonRequired]
    [JsonConverter(typeof(CkIdAttributeIdConverter))]
    public CkId<CkAttributeId> AttributeId { get; set; }

    [JsonPropertyName("name")] 
    [JsonRequired]
    public string AttributeName { get; set; } = null!;

    public bool IsAutoCompleteEnabled { get; set; }

    public string? AutoCompleteFilter { get; set; }

    public int? AutoCompleteLimit { get; set; }

    public string? AutoIncrementReference { get; set; }
}