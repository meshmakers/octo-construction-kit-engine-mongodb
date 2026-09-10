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
    ///     DEPRECATED mirror of <see cref="Ownership" /> meaning "preserved on Upsert"
    ///     (<c>AttributeOwnership.IsPreservedOnUpsert</c>): true for TenantOwned, RuntimeState and
    ///     Secret. Persisted unconditionally so an engine that does not know <see cref="Ownership" />
    ///     yet still reads a tenant-owned or secret attribute as runtime state and degrades to
    ///     preserve-on-upsert instead of "seed wins", which would reset credentials
    ///     (see ImportRtModelCommand.PreserveRuntimeStateAttributesAsync, AB#4582 / AB#4589 / AB#5187).
    /// </summary>
    public bool IsRuntimeState { get; set; }

    /// <summary>
    ///     Declared ownership of the attribute DEFINITION (AB#5187): who owns the value and whether
    ///     it is part of the entity's portable definition. <c>null</c> means "not declared" — the
    ///     state of every document written before AB#5187 and of every model that still uses the
    ///     deprecated <c>isRuntimeState</c> alias. The read path resolves <c>null</c> through
    ///     <c>AttributeOwnership.Resolve(ownership, isRuntimeState)</c>, so a legacy document keeps
    ///     exactly today's behaviour (true → RuntimeState, false → SeedOwned) and never falls back
    ///     to SeedOwned for an attribute that was flagged.
    /// </summary>
    public AttributeOwnershipDto? Ownership { get; set; }

    /// <summary>
    ///     Optional meta data of the attribute
    /// </summary>
    public ICollection<CkAttributeMetaData>? MetaData { get; set; }
}
