using System.Text.Json.Serialization;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

public class CkSelectionValueDto
{
    [JsonRequired] 
    public int Key { get; set; }

    [JsonRequired] 
    public string Name { get; set; } = null!;
}
