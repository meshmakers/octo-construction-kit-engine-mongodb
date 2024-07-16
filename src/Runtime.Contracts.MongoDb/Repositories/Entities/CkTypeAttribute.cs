using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

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
}