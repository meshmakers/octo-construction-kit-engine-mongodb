using System.Text.Json.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class ExportModelRequestDto
{
    [JsonConverter(typeof(OctoObjectIdConverter))]
    public OctoObjectId QueryId { get; set; }
}
