using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;

public class CkAggregatedModelElements
{
    public CkAggregatedModelElements()
    {
        CkModelDependencies = new();
        CkEntities = new();
        CkAttributes = new();
        CkAssociationRoles = new();
    }

    public Dictionary<CkModelId, ICollection<CkModelId>> CkModelDependencies { get; }
    public Dictionary<CkId<CkTypeId>, CkEntity> CkEntities { get; }
    public Dictionary<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    public Dictionary<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }

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
        
        if (ckModelRoot.CkEntities != null)
        {
            foreach (var ckEntity in ckModelRoot.CkEntities)
            {
                CkEntities.Add(new CkId<CkTypeId>(ckModelRoot.ModelId, ckEntity.TypeId), ckEntity);
            }
        }
    }
}