using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

/// <summary>
///     CK v2 (AB#5667): one row per <c>implements</c> entry of a type, collection
///     <c>CkTypeInterfaceImplementation</c>. Same shape and lifecycle as <see cref="CkTypeInheritance" />: owned by
///     the model that DECLARES the type (<see cref="CkModelId" />), so the interface may belong to another model.
///     Only the declared entries are stored; the inherited ones (<c>AllImplementedInterfaces</c>) are resolved
///     by the engine when the graph is rebuilt from the read-back DTOs.
/// </summary>
public class CkTypeInterfaceImplementation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId ImplementationId { get; set; }

    /// <summary>
    ///     The model that declares the implementing type.
    /// </summary>
    public CkModelId CkModelId { get; set; } = null!;

    /// <summary>
    ///     Defines the state of the construction kit model
    /// </summary>
    public ModelState ModelState { get; init; }

    /// <summary>
    ///     The implementing type.
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; set; } = null!;

    /// <summary>
    ///     The implemented interface, persisted verbatim like <see cref="CkTypeInheritance.BaseCkTypeId" />
    ///     (a major-qualified reference of a range-retaining model stays major-qualified).
    /// </summary>
    public CkId<CkInterfaceId> CkInterfaceId { get; set; } = null!;
}
