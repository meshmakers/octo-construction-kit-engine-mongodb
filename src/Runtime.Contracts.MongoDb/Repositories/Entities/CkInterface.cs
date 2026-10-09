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

    /// <summary>
    ///     CK v2 Phase 1 (F1.1-S5, AB#5915): interfaces this interface extends (members are inherited by the engine
    ///     graph; only the declared list is stored). <c>null</c> when none.
    /// </summary>
    public List<CkId<CkInterfaceId>>? Extends { get; set; }

    /// <summary>
    ///     CK v2 Phase 1 (F1.1-S5): association members, the Contracts DTO embedded as-is (like type methods).
    ///     Stored here and NOT as <c>CkTypeAssociation</c> rows: they are declarations an implementing type must
    ///     satisfy, not associations of a type (decision recorded in CLAUDE.md). <c>null</c> when none.
    /// </summary>
    public List<CkInterfaceAssociationDto>? Associations { get; set; }

    /// <summary>
    ///     CK v2 Phase 1 (F1.1-S5): method declarations, embedded like <c>CkType.Methods</c>. <c>null</c> when none.
    /// </summary>
    public List<CkMethodDto>? Methods { get; set; }

    /// <summary>
    ///     CK v2 Phase 1 (F1.1-S5): the interface is deprecated (dependents get a compile warning). <c>null</c> = not
    ///     declared.
    /// </summary>
    public bool? Deprecated { get; set; }

    /// <summary>
    ///     CK v2 Phase 1 (F1.1-S4, AB#5915): declared visibility; <c>null</c> = <c>Public</c> and absent from the document.
    /// </summary>
    public CkVisibilityDto? Visibility { get; set; }
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
