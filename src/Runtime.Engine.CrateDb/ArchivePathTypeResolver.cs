using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Resolves user-picked archive column paths against a target CK type and produces the
/// pre-resolved <see cref="ArchiveColumnDdl"/> entries the <see cref="ArchiveDdlGenerator"/> needs.
/// Walks the type's attribute tree (and any record subtrees the path crosses) so paths like
/// <c>sensor.reading.value</c> resolve correctly through nested records.
/// </summary>
/// <remarks>
/// Concept §6: paths picked via <c>availableArchivePaths</c> are stored verbatim on the archive
/// entity without their type metadata. At activation time the type must be re-resolved against the
/// current CK model so the DDL reflects the actual schema, not a snapshot from when the user
/// configured the archive (the model could have changed in the meantime — incompatible changes
/// surface as <see cref="UnresolvableArchivePathException"/>).
/// </remarks>
internal static class ArchivePathTypeResolver
{
    public static IReadOnlyList<ArchiveColumnDdl> Resolve(
        ICkCacheService ckCache,
        string tenantId,
        RtCkId<CkTypeId> targetCkTypeId,
        IReadOnlyList<CkArchiveColumnSpec> columns)
    {
        if (columns.Count == 0) return Array.Empty<ArchiveColumnDdl>();

        var resolved = new List<ArchiveColumnDdl>(columns.Count);

        // The target CK type is only needed to resolve ingested-column paths; a computed-only
        // archive (all columns are formulas) does not require it. Resolve lazily.
        CkTypeGraph? ckType = null;

        foreach (var column in columns)
        {
            if (column.IsComputed)
            {
                // Computed columns have no CK attribute path: the type comes from the declared
                // ResultType and the name from Name. They are always nullable — the formula may
                // evaluate to null / NaN, stored as NULL. Concept §4 / §5.
                resolved.Add(ComputedColumnDdl.Build(column));
                continue;
            }

            ckType ??= ckCache.GetRtCkType(tenantId, targetCkTypeId);
            var crateType = ResolvePath(ckCache, tenantId, ckType, column.Path);
            resolved.Add(new ArchiveColumnDdl(column.Path, crateType, column.Required, column.Indexed));
        }

        return resolved;
    }

    private static CrateColumnType ResolvePath(
        ICkCacheService ckCache, string tenantId, CkTypeGraph rootType, string path)
    {
        var segments = path.Split('.');
        if (segments.Length == 0 || string.IsNullOrEmpty(segments[0]))
        {
            throw new UnresolvableArchivePathException(path, "path is empty.");
        }

        if (!rootType.AllAttributesByName.TryGetValue(segments[0], out var attribute))
        {
            throw new UnresolvableArchivePathException(path,
                $"attribute '{segments[0]}' is not defined on type '{rootType.CkTypeId}'.");
        }

        return ResolveSegment(ckCache, tenantId, attribute, segments, segmentIndex: 0, path);
    }

    private static CrateColumnType ResolveSegment(
        ICkCacheService ckCache,
        string tenantId,
        CkTypeAttributeGraph attribute,
        string[] segments,
        int segmentIndex,
        string fullPath)
    {
        var isLast = segmentIndex == segments.Length - 1;
        var valueType = attribute.ValueType;

        switch (valueType)
        {
            case AttributeValueTypesDto.Record:
            case AttributeValueTypesDto.RecordArray:
            {
                if (isLast)
                {
                    // The user picked the record itself as a column — emit a strict-object whose
                    // shape mirrors the record's primitive attributes. Nested records inside the
                    // record are not flattened here; they would have been listed as separate paths
                    // by `availableArchivePaths`.
                    var recordType = BuildRecordObject(ckCache, tenantId, attribute.ValueCkRecordId, fullPath);
                    return valueType == AttributeValueTypesDto.RecordArray
                        ? new CrateColumnType.Array(recordType)
                        : recordType;
                }

                if (attribute.ValueCkRecordId == null)
                {
                    throw new UnresolvableArchivePathException(fullPath,
                        $"record-typed attribute lacks a record id (segment '{segments[segmentIndex]}').");
                }
                if (!ckCache.TryGetCkRecord(tenantId, attribute.ValueCkRecordId, out var record) || record == null)
                {
                    throw new UnresolvableArchivePathException(fullPath,
                        $"referenced record '{attribute.ValueCkRecordId}' could not be loaded.");
                }

                var nextName = segments[segmentIndex + 1];
                if (!record.AllAttributesByName.TryGetValue(nextName, out var nextAttribute))
                {
                    throw new UnresolvableArchivePathException(fullPath,
                        $"attribute '{nextName}' is not defined on record '{attribute.ValueCkRecordId}'.");
                }

                return ResolveSegment(ckCache, tenantId, nextAttribute, segments, segmentIndex + 1, fullPath);
            }

            default:
            {
                if (!isLast)
                {
                    throw new UnresolvableArchivePathException(fullPath,
                        $"non-record attribute '{segments[segmentIndex]}' cannot be navigated further.");
                }
                return MapPrimitive(valueType, fullPath);
            }
        }
    }

    private static CrateColumnType MapPrimitive(AttributeValueTypesDto valueType, string fullPath)
    {
        // Array primitives (StringArray, IntegerArray) are modeled as ARRAY(<element>). The DTO enum
        // doesn't distinguish array variants for primitives beyond strings/ints, so the explicit
        // mapping below covers the cases we actually emit; everything else surfaces an error at
        // activation time (caller flips the archive to Failed).
        return valueType switch
        {
            AttributeValueTypesDto.StringArray => new CrateColumnType.Array(new CrateColumnType.Primitive("TEXT")),
            AttributeValueTypesDto.IntegerArray => new CrateColumnType.Array(new CrateColumnType.Primitive("INTEGER")),
            _ => CrateTypeMapper.ToCratePrimitive(valueType)
        };
    }

    private static CrateColumnType.StrictObject BuildRecordObject(
        ICkCacheService ckCache, string tenantId, CkId<CkRecordId>? recordId, string fullPath)
    {
        if (recordId == null)
        {
            throw new UnresolvableArchivePathException(fullPath, "record-typed attribute lacks a record id.");
        }
        if (!ckCache.TryGetCkRecord(tenantId, recordId, out var record) || record == null)
        {
            throw new UnresolvableArchivePathException(fullPath,
                $"referenced record '{recordId}' could not be loaded.");
        }

        var fields = new List<RecordField>();
        foreach (var (name, attr) in record.AllAttributesByName)
        {
            // Skip nested-record fields when materialising the object: they should be picked as
            // their own paths if the user wants them. CrateDB strict objects with deeply-nested
            // dynamic schemas tend to surprise people on activation.
            if (attr.ValueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray)
            {
                continue;
            }
            fields.Add(new RecordField(name, MapPrimitive(attr.ValueType, $"{fullPath}.{name}")));
        }
        return new CrateColumnType.StrictObject(fields);
    }
}

/// <summary>
/// Thrown when an archive column path cannot be resolved against the current CK model — for
/// example because the attribute was renamed or removed since the archive was created. The
/// lifecycle service catches this, flips the archive to <c>Failed</c>, and records the reason in
/// the audit trail.
/// </summary>
public sealed class UnresolvableArchivePathException : Exception
{
    public UnresolvableArchivePathException(string path, string reason)
        : base($"Archive column path '{path}' could not be resolved: {reason}")
    {
        Path = path;
        Reason = reason;
    }

    public string Path { get; }
    public string Reason { get; }
}
