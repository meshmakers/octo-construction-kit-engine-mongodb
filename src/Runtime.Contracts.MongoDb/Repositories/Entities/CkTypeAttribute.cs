using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkTypeAttribute
{
    public CkId<CkAttributeId> AttributeId { get; set; } = null!;

    public string AttributeName { get; set; } = null!;

    public ICollection<object>? AutoCompleteValues { get; set; }
    public string? AutoIncrementReference { get; set; }

    /// <summary>
    ///     If true, the attribute is optional, that means it can be null
    /// </summary>
    public bool IsOptional { get; set; }

    /// <summary>
    ///     Per-ASSIGNMENT override of the attribute definition's ownership (AB#5187).
    ///     <c>null</c> means "inherit from the definition" — what every assignment written before
    ///     AB#5187 declares, and the only value legacy documents can have. Unlike the definition
    ///     there is no boolean fallback here (assignments never carried one), so dropping this
    ///     member would silently disable the override entirely: the value must round-trip.
    ///     Covers type attributes, record attributes and association-role attributes alike — they
    ///     all persist through this entity.
    /// </summary>
    public AttributeOwnershipDto? Ownership { get; set; }

    /// <summary>
    ///     CK v2 (AB#5668): per-assignment API access (<c>ReadWrite | ReadOnly | MethodOnly | Hidden</c>).
    ///     <c>null</c> means <see cref="CkAttributeAccessDto.ReadWrite" /> and is what every pre-v2 document
    ///     says (the element stays absent). Like <see cref="Ownership" /> it is read back at three sites —
    ///     records, types and association roles — and must round-trip at all of them.
    /// </summary>
    public CkAttributeAccessDto? Access { get; set; }
}