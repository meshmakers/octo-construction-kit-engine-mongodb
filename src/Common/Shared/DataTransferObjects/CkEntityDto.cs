using Meshmakers.Octo.Common.Shared.GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class CkEntityDto
{
    public CkId<CkTypeId> CkTypeId { get; set; }
    public string TypeName { get; set; } = null!;

    public ScopeIdsDto ScopeId { get; set; }
    public bool IsFinal { get; set; }
    public bool IsAbstract { get; set; }

    // For client usage
    public Connection<CkEntityAttributeDto>? Attributes { get; set; }
    public Connection<CkEntityDto>? BaseType { get; set; }
    public Connection<CkEntityDto>? DerivedTypes { get; set; }
}