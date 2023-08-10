using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace CkModel.Compiler.Tests.sampleData.systemFake;

public class Builder
{
    public static CkModelRoot Build()
    {
        return new CkModelRoot
        {
            ModelId = new CkModelId("System", "1.0.0"),
            CkEntities = new List<CkEntityDto>
            {
                new()
                {
                    TypeId = "Entity",
                    IsAbstract = true
                }
            },
            CkAssociationRoles = new List<CkAssociationRoleDto>
            {
                new()
                {
                    RoleId = "ParentChild", InboundMultiplicity = MultiplicitiesDto.One,
                    OutboundMultiplicity = MultiplicitiesDto.N, InboundName = "Parent", OutboundName = "Children"
                }
            }
        };
    }
}