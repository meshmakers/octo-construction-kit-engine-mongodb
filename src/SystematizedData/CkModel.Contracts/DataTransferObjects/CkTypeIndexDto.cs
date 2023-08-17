using System.Text.Json.Serialization;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

public class CkTypeIndexDto
{
    public CkTypeIndexDto()
    {
        Fields = new List<CkIndexFieldsDto>();
    }

    [JsonPropertyName("indexType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IndexTypeDto IndexType { get; set; }

    [JsonPropertyName("language")] public string? Language { get; set; }

    [JsonPropertyName("fields")] public List<CkIndexFieldsDto> Fields { get; set; }
}