using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class RtEntityDto : GraphQlDto
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [JsonConverter(typeof(NewtonOctoObjectIdConverter))]
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    ///     Returns the creation date time
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public DateTime? RtCreationDateTime { get; set; }

    /// <summary>
    ///     Returns the last change date time
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public DateTime? RtChangedDateTime { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? CkId { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? RtWellKnownName { get; set; }

    [JsonExtensionData]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public IDictionary<string, object>? Properties { get; set; }
}
