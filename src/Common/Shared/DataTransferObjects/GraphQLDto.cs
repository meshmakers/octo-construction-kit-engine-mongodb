using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class GraphQlDto
{
    [JsonIgnore]
    public object? UserContext { get; set; }
}
