using System.Text.Json.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

public class RtModelRootDto
{
    public RtModelRootDto()
    {
        RtEntities = new List<RtEntityDto>();
    }

    [JsonPropertyName("entities")] public List<RtEntityDto> RtEntities { get; }
}
