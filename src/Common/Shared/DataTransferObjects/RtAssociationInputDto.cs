using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class RtAssociationInputDto
{
    public RtEntityId Target { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AssociationModOptionsDto? ModOption { get; set; }
}