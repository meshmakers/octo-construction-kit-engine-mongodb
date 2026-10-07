using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

[DebuggerDisplay("{" + nameof(Id) + "}")]
public class CkModel
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public CkModel()
    {
        Dependencies = [];
    }

    /// <summary>
    ///     Defines the id of the construction kit model
    /// </summary>
    public CkModelId Id { get; init; } = null!;

    /// <summary>
    /// Defines the name of construction kit model without version
    /// </summary>
    public string ModelId { get; init; } = null!;
    
    /// <summary>
    ///     Defines the state of the construction kit model
    /// </summary>
    public ModelState ModelState { get; init; }

    /// <summary>
    ///     Defines the dependencies of the construction kit
    /// </summary>
    public CkModelId[]? Dependencies { get; init; }
    
    /// <summary>
    ///     CK v2 range retention (AB#5665): declared range + floor per direct dependency of a range-retaining
    ///     model; <c>null</c> for classic exact-pinned models (and absent from their documents).
    /// </summary>
    public CkModelDependency[]? DependencyRanges { get; init; }

    /// <summary>
    ///     CK v2 (AB#5584): the CK language version the model declares (<c>ckLanguage</c>). <c>null</c> means 1
    ///     and stays absent from the document, so classic models keep their pre-v2 shape.
    /// </summary>
    public int? CkLanguage { get; init; }

    /// <summary>
    ///     An optional description of the model
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
///     A range-retaining dependency of a persisted CK model (AB#5665), the persisted form of
///     <see cref="CkModelDependencyDto" />.
/// </summary>
public class CkModelDependency
{
    /// <summary>
    ///     The declared range, e.g. <c>System-[2.4,3.0)</c>.
    /// </summary>
    public string Range { get; init; } = null!;

    /// <summary>
    ///     The floor version, e.g. <c>2.4.0</c>.
    /// </summary>
    public string Floor { get; init; } = null!;

    /// <summary>Maps a DTO to the persisted form.</summary>
    public static CkModelDependency FromDto(CkModelDependencyDto dto) =>
        new() { Range = dto.Range.FullName, Floor = dto.Floor };

    /// <summary>Maps the persisted form back to the DTO.</summary>
    public CkModelDependencyDto ToDto() => new() { Range = new CkModelIdVersionRange(Range), Floor = Floor };
}
