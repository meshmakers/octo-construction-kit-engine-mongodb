using System.Collections.ObjectModel;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

public class CkDependencyGraph
{
    private IDictionary<CkId<CkTypeId>, CkEntityGraph> _entities;

    public CkDependencyGraph()
    {
        _entities = new Dictionary<CkId<CkTypeId>, CkEntityGraph>();
        Entities = new ReadOnlyDictionary<CkId<CkTypeId>, CkEntityGraph>(_entities);
    }

    public IReadOnlyDictionary<CkId<CkTypeId>, CkEntityGraph> Entities { get; }


    public CkEntityGraph AddEntity(CkId<CkTypeId> ckTypeId, CkEntityDto ckEntityDto,
        ICollection<CkGraphTypeInheritance> baseTypes)
    {
        CkEntityGraph ckEntityGraph = new(ckTypeId, ckEntityDto.IsAbstract, ckEntityDto.IsFinal, baseTypes);

        _entities.Add(ckTypeId, ckEntityGraph);

        return ckEntityGraph;
    }
}