using System.Diagnostics;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

[DebuggerDisplay("{" + nameof(AttributeId) + "} -> {" + nameof(AttributeName) + "}")]
public class CkEntityAttributeDto
{
    [JsonPropertyName("id")]
    [JsonRequired]
    [JsonConverter(typeof(CkIdAttributeIdConverter))]
    public CkId<CkAttributeId> AttributeId { get; set; }

    [JsonPropertyName("name")] [JsonRequired]
    public string AttributeName { get; set; } = null!;

    [JsonPropertyName("isAutoCompleteEnabled")]
    public bool IsAutoCompleteEnabled { get; set; }

    [JsonPropertyName("autoCompleteFilter")] 
    public string? AutoCompleteFilter { get; set; }

    [JsonPropertyName("autoCompleteLimit")]
    public int? AutoCompleteLimit { get; set; }

    [JsonPropertyName("autoIncrementReference")]
    public string? AutoIncrementReference { get; set; }
}