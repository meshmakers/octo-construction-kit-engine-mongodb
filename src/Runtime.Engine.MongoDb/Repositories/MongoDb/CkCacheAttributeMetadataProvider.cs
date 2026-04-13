using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Attribute metadata provider backed by the CK cache service.
/// Used for query-time field path resolution where the CK cache is available.
/// </summary>
internal class CkCacheAttributeMetadataProvider : IAttributeMetadataProvider
{
    private readonly ICkCacheService _ckCacheService;
    private readonly string _tenantId;
    private readonly CkTypeWithAttributesGraph _graph;

    public CkCacheAttributeMetadataProvider(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph graph)
    {
        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _graph = graph;
    }

    public bool IsRecordContext => _graph is CkRecordGraph;

    public bool TryGetAttribute(string attributeName, out AttributeValueTypesDto valueType)
    {
        if (_graph.AllAttributesByName.TryGetValue(attributeName, out var ckTypeAttributeGraph))
        {
            valueType = ckTypeAttributeGraph.ValueType;
            return true;
        }

        valueType = default;
        return false;
    }

    public IAttributeMetadataProvider? NavigateToRecord(string attributeName)
    {
        if (!_graph.AllAttributesByName.TryGetValue(attributeName, out var ckTypeAttributeGraph))
        {
            return null;
        }

        if (ckTypeAttributeGraph.ValueCkRecordId == null)
        {
            return null;
        }

        var recordGraph = _ckCacheService.GetCkRecord(_tenantId, ckTypeAttributeGraph.ValueCkRecordId);
        return new CkCacheAttributeMetadataProvider(_ckCacheService, _tenantId, recordGraph);
    }
}
