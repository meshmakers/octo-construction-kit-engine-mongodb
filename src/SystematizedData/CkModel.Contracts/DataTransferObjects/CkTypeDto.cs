using System.Diagnostics;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

[DebuggerDisplay("{" + nameof(TypeId) + "}")]
public class CkTypeDto
{
    public CkTypeDto()
    {
        Attributes = new List<CkTypeAttributeDto>();
        Associations = new List<CkTypeAssociationDto>();
        Indexes = new List<CkTypeIndexDto>();
    }

    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    [JsonPropertyName("typeId")]
    [JsonRequired]
    public CkTypeId TypeId { get; set; }

    [JsonPropertyName("derivedFromCkTypeId")]
    [JsonConverter(typeof(CkIdTypeIdConverter))]
    public CkId<CkTypeId>? DerivedFromCkTypeId { get; set; }

    /// <summary>
    ///     If true, the type cannot be inherited again
    /// </summary>
    [JsonPropertyName("isFinal")]
    public bool IsFinal { get; set; }

    /// <summary>
    ///     If true, the type cannot be instantiated by a runtime entity
    /// </summary>
    [JsonPropertyName("isAbstract")]
    public bool IsAbstract { get; set; }

    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    [JsonPropertyName("attributes")]
    public List<CkTypeAttributeDto>? Attributes { get; set; }

    /// <summary>
    /// Gets or sets a list of indexes
    /// </summary>
    [JsonPropertyName("indexes")] 
    public List<CkTypeIndexDto>? Indexes { get; set; }

    [JsonPropertyName("associations")]
    public List<CkTypeAssociationDto>? Associations { get; set; }

    /// <summary>
    /// Gets or sets if the change stream should include pre and post images
    /// </summary>
    [JsonPropertyName("enableChangeStreamPreAndPostImages")]
    public bool EnableChangeStreamPreAndPostImages { get; set; }
}