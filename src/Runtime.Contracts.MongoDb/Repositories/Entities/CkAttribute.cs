using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

/// <summary>
///     Represents a construction kit attribute in database
/// </summary>
[DebuggerDisplay("{" + nameof(CkAttributeId) + "}")]
public class CkAttribute
{
    /// <summary>
    ///     Gets or sets the construction kit model id
    /// </summary>
    public CkModelId CkModelId { get; set; } = null!;

    /// <summary>
    ///     Defines the state of the construction kit model
    /// </summary>
    public ModelState ModelState { get; init; }

    /// <summary>
    ///     The id of the attribute
    /// </summary>
    public CkId<CkAttributeId> CkAttributeId { get; set; } = null!;

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

    /// <summary>
    ///     Marks the attribute as runtime state owned by services/operators/users at runtime
    ///     (e.g. deployment status, archive lifecycle status, sync counters). When true, an Upsert
    ///     import preserves the existing value instead of overwriting it with the imported value
    ///     (see ImportRtModelCommand.PreserveRuntimeStateAttributesAsync, AB#4582 / AB#4589).
    /// </summary>
    public bool IsRuntimeState { get; set; }

    /// <summary>
    ///     Optional meta data of the attribute
    /// </summary>
    public ICollection<CkAttributeMetaData>? MetaData { get; set; }
}
