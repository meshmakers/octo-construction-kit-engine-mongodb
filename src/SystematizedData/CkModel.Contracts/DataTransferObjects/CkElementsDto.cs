using Json.Schema.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization.Schema;
using YamlDotNet.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

/// <summary>
/// A part of a CK model.
/// </summary>
[OctoJsonSchema(typeof(CkSchema), nameof(CkSchema.ElementsSchema))]
public class CkElementsDto
{
    // ReSharper disable once CollectionNeverUpdated.Global
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public List<CkTypeDto>? Types { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public List<CkAssociationRoleDto>? AssociationRoles { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public List<CkAttributeDto>? Attributes { get; set; }
}