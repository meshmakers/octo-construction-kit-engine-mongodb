using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Persistence.Contracts;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.Exchange;

[DebuggerDisplay("{" + nameof(CkId) + "}")]
public class CkEntity
{
    public CkEntity()
    {
        Attributes = new List<CkEntityAttribute>();
        Associations = new List<CkEntityAssociation>();
        Indexes = new List<CkEntityIndexDto>();
    }

    [JsonPropertyName("ckId")]
    [JsonRequired]
    public CkTypeId CkId { get; set; }

    [JsonPropertyName("ckDerivedId")]
    [JsonConverter(typeof(CkIdTypeIdConverter))]
    public CkId<CkTypeId>? CkDerivedId { get; set; }

    [JsonPropertyName("isFinal")] public bool IsFinal { get; set; }

    [JsonPropertyName("isAbstract")] public bool IsAbstract { get; set; }


    [JsonPropertyName("attributes")] public List<CkEntityAttribute> Attributes { get; set; }

    [JsonPropertyName("indexes")] public List<CkEntityIndexDto>? Indexes { get; set; }

    [JsonPropertyName("associations")] public List<CkEntityAssociation>? Associations { get; set; }

    /// <summary>
    /// Gets or sets if the change stream should include pre and post images
    /// </summary>
    [JsonPropertyName("enableChangeStreamPreAndPostImages")]
    public bool EnableChangeStreamPreAndPostImages { get; set; }
}