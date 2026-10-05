using System.Collections;

using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories;

/// <summary>
///     Read-side normalisation of <c>Secret</c> attributes with CK knowledge (AB#5533, concept §3.3 /
///     §3.4): a plain string found in a Secret slot - at the top level or inside a record /
///     record-array element - is a value stored before the attribute became Secret (plaintext or
///     <c>enc:v1</c>) and is handed out as <see cref="RtSecretValue.LegacyPlaintext" />, never as a
///     string. Protected sub-documents need no CK knowledge: the BSON serializer already reads them as
///     <see cref="RtSecretValue.Protected" /> in every slot.
/// </summary>
/// <remarks>
///     One instance per query execution: the per-type shape (which attributes are Secret, which
///     records contain Secret sub-attributes) is computed once per CK type and reused for every
///     entity of the result. Types without any Secret attribute cost one dictionary lookup per entity.
/// </remarks>
internal sealed class SecretAttributeReadNormalizer(ICkCacheService ckCacheService, string tenantId)
{
    private readonly Dictionary<string, SecretShape?> _typeShapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SecretShape?> _recordShapes = new(StringComparer.Ordinal);

    /// <summary>
    ///     Normalises the Secret slots of every entity of a result.
    /// </summary>
    public void Normalize<TEntity>(IEnumerable<TEntity> entities) where TEntity : RtEntity
    {
        foreach (var entity in entities)
        {
            Normalize(entity);
        }
    }

    /// <summary>
    ///     Normalises the Secret slots of one entity.
    /// </summary>
    public void Normalize(RtEntity entity)
    {
        if (entity.CkTypeId == null)
        {
            return;
        }

        var key = entity.CkTypeId.FullName;
        if (!_typeShapes.TryGetValue(key, out var shape))
        {
            shape = ckCacheService.TryGetRtCkType(tenantId, entity.CkTypeId, out var graph)
                ? BuildShape(graph, new HashSet<string>(StringComparer.Ordinal))
                : null;
            _typeShapes[key] = shape;
        }

        if (shape != null)
        {
            Apply(entity, shape);
        }
    }

    private SecretShape? BuildShape(CkTypeWithAttributesGraph graph, HashSet<string> visitedRecords)
    {
        List<string>? secretAttributes = null;
        List<(string AttributeName, SecretShape Shape)>? records = null;

        foreach (var (name, attribute) in graph.AllAttributesByName)
        {
            switch (attribute.ValueType)
            {
                case AttributeValueTypesDto.Secret:
                    (secretAttributes ??= []).Add(name);
                    break;
                case AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray
                    when attribute.ValueCkRecordId != null:
                {
                    var recordShape = GetRecordShape(attribute.ValueCkRecordId, visitedRecords);
                    if (recordShape != null)
                    {
                        (records ??= []).Add((name, recordShape));
                    }

                    break;
                }
            }
        }

        return secretAttributes == null && records == null
            ? null
            : new SecretShape(secretAttributes ?? [], records ?? []);
    }

    private SecretShape? GetRecordShape(ConstructionKit.Contracts.CkId<ConstructionKit.Contracts.CkRecordId> recordId,
        HashSet<string> visitedRecords)
    {
        var key = recordId.FullName;
        if (_recordShapes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // A record that (indirectly) contains itself cannot hold a Secret on the cycle beyond what
        // the first visit already found.
        if (!visitedRecords.Add(key))
        {
            return null;
        }

        var shape = ckCacheService.TryGetCkRecord(tenantId, recordId, out var recordGraph)
            ? BuildShape(recordGraph, visitedRecords)
            : null;
        _recordShapes[key] = shape;
        return shape;
    }

    private static void Apply(RtTypeWithAttributes target, SecretShape shape)
    {
        foreach (var name in shape.SecretAttributes)
        {
            if (target.Attributes.TryGetValue(name, out var value) && value is string legacy)
            {
                target.SetAttributeRawValue(name, RtSecretValue.LegacyPlaintext(legacy));
            }
        }

        foreach (var (name, recordShape) in shape.Records)
        {
            if (!target.Attributes.TryGetValue(name, out var value) || value == null)
            {
                continue;
            }

            switch (value)
            {
                case RtRecord record:
                    Apply(record, recordShape);
                    break;
                case IEnumerable items and not string:
                    foreach (var item in items)
                    {
                        if (item is RtRecord element)
                        {
                            Apply(element, recordShape);
                        }
                    }

                    break;
            }
        }
    }

    private sealed record SecretShape(
        IReadOnlyList<string> SecretAttributes,
        IReadOnlyList<(string AttributeName, SecretShape Shape)> Records);
}
