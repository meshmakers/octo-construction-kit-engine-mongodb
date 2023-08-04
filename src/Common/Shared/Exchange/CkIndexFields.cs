using System.Collections.Generic;
using System.Text.Json.Serialization;
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkIndexFields
{
    public int? Weight { get; set; }

    [JsonRequired] public List<string> AttributeNames { get; set; } = null!;
}
