using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkEntityAttribute
{
    public string AttributeId { get; set; }

    public string AttributeName { get; set; }

    public bool IsAutoCompleteEnabled { get; set; }

    public string AutoCompleteFilter { get; set; }

    public int AutoCompleteLimit { get; set; }

    public string AutoIncrementReference { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ICollection<string> AutoCompleteTexts { get; set; }
}
