using System.Collections.Generic;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkIndexFields
{
    public int? Weight { get; set; }

    [JsonRequired] public List<string> AttributeNames { get; set; } = null!;
}
