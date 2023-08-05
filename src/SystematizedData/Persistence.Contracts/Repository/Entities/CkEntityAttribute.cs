using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkEntityAttribute 
{
    public CkId<CkAttributeId> AttributeId { get; set; }

    public string AttributeName { get; set; } = null!;

    public bool IsAutoCompleteEnabled { get; set; }

    public string? AutoCompleteFilter { get; set; }

    public int? AutoCompleteLimit { get; set; }

    public string? AutoIncrementReference { get; set; }

    public ICollection<string>? AutoCompleteTexts { get; set; }
}
