using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Attribute metadata provider backed by pre-fetched database entities.
/// Used for index creation during CK model import when the CK cache is not yet available.
/// </summary>
internal class DatabaseAttributeMetadataProvider : IAttributeMetadataProvider
{
    private readonly Dictionary<string, CkAttribute> _attributesByName;
    private readonly IReadOnlyDictionary<CkId<CkAttributeId>, CkAttribute> _allCkAttributes;
    private readonly IReadOnlyDictionary<CkId<CkRecordId>, CkRecord> _allCkRecords;

    public DatabaseAttributeMetadataProvider(
        IEnumerable<CkTypeAttribute> typeAttributes,
        IReadOnlyDictionary<CkId<CkAttributeId>, CkAttribute> allCkAttributes,
        IReadOnlyDictionary<CkId<CkRecordId>, CkRecord> allCkRecords,
        bool isRecordContext)
    {
        _allCkAttributes = allCkAttributes;
        _allCkRecords = allCkRecords;
        IsRecordContext = isRecordContext;

        // Build lookup: AttributeName (PascalCase) → CkAttribute (with ValueType)
        _attributesByName = new Dictionary<string, CkAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var typeAttr in typeAttributes)
        {
            if (_allCkAttributes.TryGetValue(typeAttr.AttributeId, out var ckAttribute))
            {
                _attributesByName[typeAttr.AttributeName] = ckAttribute;
            }
        }
    }

    public bool IsRecordContext { get; }

    public bool TryGetAttribute(string attributeName, out AttributeValueTypesDto valueType)
    {
        if (_attributesByName.TryGetValue(attributeName, out var ckAttribute))
        {
            valueType = ckAttribute.AttributeValueType;
            return true;
        }

        valueType = default;
        return false;
    }

    public IAttributeMetadataProvider? NavigateToRecord(string attributeName)
    {
        if (!_attributesByName.TryGetValue(attributeName, out var ckAttribute))
        {
            return null;
        }

        if (ckAttribute.ValueCkRecordId == null)
        {
            return null;
        }

        if (!_allCkRecords.TryGetValue(ckAttribute.ValueCkRecordId, out var ckRecord))
        {
            return null;
        }

        return new DatabaseAttributeMetadataProvider(
            ckRecord.Attributes,
            _allCkAttributes,
            _allCkRecords,
            isRecordContext: true);
    }
}
