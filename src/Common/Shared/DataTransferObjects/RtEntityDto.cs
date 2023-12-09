using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class RtEntityDto : GraphQlDto
{
    [JsonConverter(typeof(OctoObjectIdConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CkId<CkTypeId> CkTypeId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? RtWellKnownName { get; set; }

    [JsonExtensionData]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public IDictionary<string, object>? Properties { get; set; }
}