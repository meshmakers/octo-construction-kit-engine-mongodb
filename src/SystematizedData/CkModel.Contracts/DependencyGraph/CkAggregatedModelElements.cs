using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

public class CkAggregatedModelElements
{
    public CkAggregatedModelElements()
    {
        CkModelDependencies = new();
        CkTypes = new();
        CkAttributes = new();
        CkAssociationRoles = new();
    }

    public Dictionary<CkModelId, ICollection<CkModelId>> CkModelDependencies { get; }
    public Dictionary<CkId<CkTypeId>, CkTypeDto> CkTypes { get; }
    public Dictionary<CkId<CkAttributeId>, CkAttributeDto> CkAttributes { get; }
    public Dictionary<CkId<CkAssociationRoleId>, CkAssociationRoleDto> CkAssociationRoles { get; }

    public void AppendModel(CkModelRoot ckModelRoot)
    {
        CkModelDependencies.Add(ckModelRoot.ModelId, ckModelRoot.CkDependencies ?? new List<CkModelId>());
        
        if (ckModelRoot.CkAttributes != null)
        {
            foreach (var ckAttribute in ckModelRoot.CkAttributes)
            {
                CkAttributes.Add(new CkId<CkAttributeId>(ckModelRoot.ModelId, ckAttribute.AttributeId), ckAttribute);
            }
        }
        
        if (ckModelRoot.CkAssociationRoles != null)
        {
            foreach (var ckAssociationRole in ckModelRoot.CkAssociationRoles)
            {
                CkAssociationRoles.Add(new CkId<CkAssociationRoleId>(ckModelRoot.ModelId, ckAssociationRole.RoleId), ckAssociationRole);
            }
        }
        
        if (ckModelRoot.CkTypes != null)
        {
            foreach (var ckTypeDto in ckModelRoot.CkTypes)
            {
                CkTypes.Add(new CkId<CkTypeId>(ckModelRoot.ModelId, ckTypeDto.TypeId), ckTypeDto);
            }
        }
    }
}