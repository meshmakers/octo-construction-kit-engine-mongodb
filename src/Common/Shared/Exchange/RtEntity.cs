using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtEntity
{
    public RtEntity()
    {
        Attributes = new List<RtAttribute>();
        Associations = new List<RtAssociation>();
    }

    [JsonProperty("rtId", Required = Required.Always)]
    [JsonRequired]
    public OctoObjectId RtId { get; set; }

    /// <summary>
    ///     Returns the creation date time
    /// </summary>
    [JsonProperty("rtCreationDateTime", NullValueHandling = NullValueHandling.Ignore)]
    public DateTime? RtCreationDateTime { get; set; }

    /// <summary>
    ///     Returns the last change date time
    /// </summary>
    [JsonProperty("rtChangedDateTime", NullValueHandling = NullValueHandling.Ignore)]
    public DateTime? RtChangedDateTime { get; set; }

    [JsonProperty("ckId", Required = Required.Always)] 
    [JsonRequired]
    public string? CkId { get; set; }

    [JsonProperty("rtWellKnownName")] 
    public string? RtWellKnownName { get; set; }

    [JsonProperty("attributes")] 
    public List<RtAttribute> Attributes { get; }

    [JsonProperty("associations")] 
    public List<RtAssociation>? Associations { get; }
}
