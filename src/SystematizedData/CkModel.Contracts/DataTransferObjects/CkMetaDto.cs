using System.Text.Json.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

/// <summary>
/// Represents the content of the metadata file
/// </summary>
public class CkMetaDto
{
    public CkMetaDto()
    {
        Dependencies = new List<CkModelId>();
    }
    
    [JsonRequired]
    public CkModelId ModelId { get; set; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public List<CkModelId>? Dependencies { get; set; }
}