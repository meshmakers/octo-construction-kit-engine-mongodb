using System.Text.Json.Serialization;
using Json.Schema.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization.Schema;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

/// <summary>
/// Represents the content of the metadata file
/// </summary>
[OctoJsonSchema(typeof(CkSchema), nameof(CkSchema.MetaSchema))]
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