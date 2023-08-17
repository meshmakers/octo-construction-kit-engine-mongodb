using System.Collections.ObjectModel;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

public class CkModelGraph
{
    private readonly IDictionary<CkId<CkTypeId>, CkEntityGraph> _entities;
    private readonly IDictionary<CkId<CkAttributeId>, CkAttributeGraph> _attributes;
    private readonly IDictionary<CkId<CkAssociationRoleId>, CkAssociationRoleGraph> _associationRoles;

    public CkModelGraph()
    {
        _entities = new Dictionary<CkId<CkTypeId>, CkEntityGraph>();
        _attributes = new Dictionary<CkId<CkAttributeId>, CkAttributeGraph>();
        _associationRoles = new Dictionary<CkId<CkAssociationRoleId>, CkAssociationRoleGraph>();
        Entities = new ReadOnlyDictionary<CkId<CkTypeId>, CkEntityGraph>(_entities);
        Attributes = new Dictionary<CkId<CkAttributeId>, CkAttributeGraph>(_attributes);
        AssociationRoles = new Dictionary<CkId<CkAssociationRoleId>, CkAssociationRoleGraph>(_associationRoles);
    }

    public IReadOnlyDictionary<CkId<CkTypeId>, CkEntityGraph> Entities { get; }
    public Dictionary<CkId<CkAttributeId>, CkAttributeGraph> Attributes { get; }
    public Dictionary<CkId<CkAssociationRoleId>, CkAssociationRoleGraph> AssociationRoles { get; }

    public CkAttributeGraph GetOrCreateAttribute(CkId<CkAttributeId> ckAttributeId, CkAttributeDto ckAttributeDto)
    {
        if (_attributes.TryGetValue(ckAttributeId, out var ckAttributeGraph))
        {
            return ckAttributeGraph;
        }
        
        ckAttributeGraph = new(ckAttributeId, ckAttributeDto);
        _attributes.Add(ckAttributeId, ckAttributeGraph);
        return ckAttributeGraph;
    }

    public CkEntityGraph GetOrCreateEntity(CkId<CkTypeId> ckTypeId, CkEntityDto ckEntityDto)
    {
        if (_entities.TryGetValue(ckTypeId, out var ckEntityGraph))
        {
            return ckEntityGraph;
        }
        
        ckEntityGraph = new(ckTypeId, ckEntityDto.IsAbstract, ckEntityDto.IsFinal);
        _entities.Add(ckTypeId, ckEntityGraph);
        return ckEntityGraph;
    }

    public CkAssociationRoleGraph GetOrCreateAssociationRoles(CkId<CkAssociationRoleId> ckAssociationId, CkAssociationRoleDto ckAssociationRole)
    {
        if (_associationRoles.TryGetValue(ckAssociationId, out var ckAssociationRoleGraph))
        {
            return ckAssociationRoleGraph;
        }
        
        ckAssociationRoleGraph = new(ckAssociationId, ckAssociationRole);
        _associationRoles.Add(ckAssociationId, ckAssociationRoleGraph);
        return ckAssociationRoleGraph;
    }
}