using Meshmakers.Octo.Common.Shared.GraphQL;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class CkEntityDto
{
    public string? CkTypeId { get; set; }
    public string? TypeName { get; set; }

    public ScopeIdsDto ScopeId { get; set; }
    public bool IsFinal { get; set; }
    public bool IsAbstract { get; set; }

    // For client usage
    public Connection<CkEntityAttributeDto>? Attributes { get; set; }
    public Connection<CkEntityDto>? BaseType { get; set; }
    public Connection<CkEntityDto>? DerivedTypes { get; set; }
}
