using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class CkAttributeDto
{
    public string? AttributeId { get; set; }

    public ScopeIdsDto ScopeId { get; set; }

    [JsonConverter(typeof(StringEnumConverter), typeof(ConstantCaseNamingStrategy))]

    public AttributeValueTypesDto AttributeValueType { get; set; }

    public object? DefaultValue { get; set; }

    public ICollection<object>? DefaultValues { get; set; }

    public ICollection<CkSelectionValueDto>? SelectionValues { get; set; }
}
