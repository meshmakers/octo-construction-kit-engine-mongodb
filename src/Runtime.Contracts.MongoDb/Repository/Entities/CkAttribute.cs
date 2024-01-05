using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

/// <summary>
///     Represents a construction kit attribute in database
/// </summary>
[DebuggerDisplay("{" + nameof(CkAttributeId) + "}")]
public class CkAttribute
{
    /// <summary>
    ///     Gets or sets the construction kit model id
    /// </summary>
    public CkModelId CkModelId { get; set; }

    /// <summary>
    ///     The id of the attribute
    /// </summary>
    public CkId<CkAttributeId> CkAttributeId { get; set; }

    /// <summary>
    ///     Value type of the attribute
    /// </summary>
    public AttributeValueTypesDto AttributeValueType { get; set; }

    /// <summary>
    ///     Default value of the attribute
    /// </summary>
    public ICollection<object>? DefaultValues { get; set; }

    /// <summary>
    ///     Defines the enum of the attribute if the value type is a enum.
    /// </summary>
    public CkId<CkEnumId>? ValueCkEnumId { get; set; }

    /// <summary>
    ///     Defines the record of the attribute if the value type is a record.
    /// </summary>
    public CkId<CkRecordId>? ValueCkRecordId { get; set; }

    /// <summary>
    ///     An optional description of the attribute
    /// </summary>
    public string? Description { get; set; }
}