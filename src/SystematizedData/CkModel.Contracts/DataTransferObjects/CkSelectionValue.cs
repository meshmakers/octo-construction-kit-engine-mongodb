using System.Text.Json.Serialization;
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkSelectionValue
{
    [JsonPropertyName("key")][JsonRequired]  public int Key { get; set; }

    [JsonPropertyName("name")] [JsonRequired] public string Name { get; set; } = null!;
}
