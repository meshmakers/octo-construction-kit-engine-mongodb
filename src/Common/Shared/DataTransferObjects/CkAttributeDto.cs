using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class CkAttributeDto
{
    public CkId<CkAttributeId> CkAttributeId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AttributeValueTypesDto AttributeValueType { get; set; }

    public object? DefaultValue { get; set; }

    public ICollection<object>? DefaultValues { get; set; }

    public ICollection<CkSelectionValueDto>? SelectionValues { get; set; }
}