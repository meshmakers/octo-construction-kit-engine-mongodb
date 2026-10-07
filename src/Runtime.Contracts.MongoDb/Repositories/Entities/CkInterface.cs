using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

/// <summary>
///     Persisted CK interface (CK v2, AB#5667), collection <c>CkInterface</c>. One document per interface a
///     model declares (<see cref="CkInterfaceDto" />). The implementing types are NOT stored here: each
///     <c>implements</c> entry of a type is a <c>CkTypeInterfaceImplementation</c> row, owned by the model
///     that declares the type (an interface of model A can be implemented by a type of model B).
/// </summary>
[DebuggerDisplay("{" + nameof(CkInterfaceId) + "}")]
public class CkInterface
{
    /// <summary>
    ///     The model-qualified interface id, e.g. <c>System.Identity-2.90.0/Named-1</c> (document id).
    /// </summary>
    public CkId<CkInterfaceId> CkInterfaceId { get; set; } = null!;

    /// <summary>
    ///     The model declaring the interface.
    /// </summary>
    public CkModelId CkModelId { get; set; } = null!;

    /// <summary>
    ///     Defines the state of the construction kit model
    /// </summary>
    public ModelState ModelState { get; init; }

    /// <summary>
    ///     An optional description of the interface.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     The attribute members of the interface (embedded).
    /// </summary>
    public ICollection<CkInterfaceAttribute> Attributes { get; set; } = [];
}

/// <summary>
///     An attribute member of a <see cref="CkInterface" /> (embedded), the persisted form of
///     <see cref="CkInterfaceAttributeDto" />.
/// </summary>
[DebuggerDisplay("{" + nameof(AttributeId) + "} -> {" + nameof(AttributeName) + "}")]
public class CkInterfaceAttribute
{
    /// <summary>
    ///     The attribute definition the member refers to.
    /// </summary>
    public CkId<CkAttributeId> AttributeId { get; set; } = null!;

    /// <summary>
    ///     The member name; implementing types assign the attribute under this name.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    ///     Optional members need not be assigned by implementing types.
    /// </summary>
    public bool IsOptional { get; set; }
}
