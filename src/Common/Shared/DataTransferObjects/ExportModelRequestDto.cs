using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class ExportModelRequestDto
{
    [JsonConverter(typeof(OctoObjectIdConverter))]
    public OctoObjectId QueryId { get; set; }
}
