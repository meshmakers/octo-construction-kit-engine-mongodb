using System.Collections.Generic;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtModelRoot
{
    public RtModelRoot()
    {
        RtEntities = new List<RtEntity>();
    }

    [JsonProperty("entities")] public List<RtEntity> RtEntities { get; }
}
