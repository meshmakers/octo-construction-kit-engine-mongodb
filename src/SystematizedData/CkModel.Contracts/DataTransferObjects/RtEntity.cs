using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Persistence.Contracts;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtEntity
{
    public RtEntity()
    {
        Attributes = new List<RtAttribute>();
        Associations = new List<RtAssociation>();
    }

    [JsonPropertyName("rtId")]
    [JsonRequired]
    public OctoObjectId RtId { get; set; }

    /// <summary>
    ///     Returns the creation date time
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]

    public DateTime? RtCreationDateTime { get; set; }

    /// <summary>
    ///     Returns the last change date time
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? RtChangedDateTime { get; set; }

    [JsonPropertyName("ckId")]
    [JsonRequired]
    public CkId<CkTypeId> CkId { get; set; } 

    [JsonPropertyName("rtWellKnownName")] 
    public string? RtWellKnownName { get; set; }

    [JsonPropertyName("attributes")] 
    public List<RtAttribute> Attributes { get; }

    [JsonPropertyName("associations")] 
    public List<RtAssociation>? Associations { get; }
}
