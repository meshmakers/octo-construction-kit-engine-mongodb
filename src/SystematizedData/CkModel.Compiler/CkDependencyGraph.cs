using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

public class CkDependencyGraph
{
    public CkDependencyGraph(CkAggregatedModelElements aggregatedModelElements)
    {
        AggregatedModelElements = aggregatedModelElements;
    }

    public CkAggregatedModelElements AggregatedModelElements { get; set; }

    public ICollection<CkId<CkTypeId>> GetBaseTypes(CkId<CkTypeId> baseCkTypeId)
    {
        var ckTypeIds = new List<CkId<CkTypeId>>();
        
        while (AggregatedModelElements.CkEntities.TryGetValue(baseCkTypeId, out var ckEntity))
        {
            ckTypeIds.Add(baseCkTypeId);
            
            if (ckEntity.DerivedCkTypeId != null)
            {
                baseCkTypeId = ckEntity.DerivedCkTypeId.Value;
            }
        }

        return ckTypeIds;
    }
}