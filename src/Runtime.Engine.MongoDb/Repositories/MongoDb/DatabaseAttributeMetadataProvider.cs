using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Attribute metadata provider backed by pre-fetched database entities.
/// Used for index creation during CK model import when the CK cache is not yet available.
/// </summary>
internal class DatabaseAttributeMetadataProvider : IAttributeMetadataProvider
{
    private readonly Dictionary<string, CkAttribute> _attributesByName;
    private readonly Dictionary<string, CkTypeAttribute> _assignmentsByName;
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
        _assignmentsByName = new Dictionary<string, CkTypeAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var typeAttr in typeAttributes)
        {
            // Review E-L7: names are matched case-insensitively (storage is camelCase); when two assignments
            // collide, a Hidden one wins so the backstop cannot be bypassed by a case variant.
            if (!_assignmentsByName.TryGetValue(typeAttr.AttributeName, out var existing) ||
                existing.Access != CkAttributeAccessDto.Hidden)
            {
                _assignmentsByName[typeAttr.AttributeName] = typeAttr;
            }

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

    /// <summary>
    ///     CK v2 F1.3-S4 (review N6): true when the attribute path reaches an assignment with <c>access: Hidden</c> —
    ///     the terminal attribute or any record attribute on the way. Matched case-insensitively, like the database
    ///     resolves field names: an index path <c>passwordHash</c> reaches the Hidden assignment <c>PasswordHash</c>.
    /// </summary>
    public bool ReachesHiddenAttribute(string attributePath)
    {
        DatabaseAttributeMetadataProvider? current = this;
        foreach (var term in RtPathEvaluator.TokenizePath(attributePath))
        {
            if (current == null)
            {
                return false;
            }

            if (term.Type != PathType.Attribute)
            {
                continue;
            }

            if (!current._assignmentsByName.TryGetValue(term.Value, out var assignment))
            {
                return false;
            }

            if (assignment.Access == CkAttributeAccessDto.Hidden)
            {
                return true;
            }

            current = current.NavigateToRecord(term.Value) as DatabaseAttributeMetadataProvider;
        }

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
