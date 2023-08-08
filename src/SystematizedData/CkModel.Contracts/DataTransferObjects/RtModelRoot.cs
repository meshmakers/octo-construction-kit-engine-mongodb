using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtModelRoot
{
    public RtModelRoot()
    {
        RtEntities = new List<RtEntity>();
    }

    [JsonPropertyName("entities")] public List<RtEntity> RtEntities { get; }
}
