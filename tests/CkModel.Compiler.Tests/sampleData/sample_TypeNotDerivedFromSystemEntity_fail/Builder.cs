using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace CkModel.Compiler.Tests.sampleData.sample_TypeNotDerivedFromSystemEntity_fail;

public class Builder
{
    public static CkModelRoot Build()
    {
        return new CkModelRoot
        {
            ModelId = new CkModelId("sample1", "1.0.0"),
            Dependencies = new List<CkModelId> { new("System", "1.0.0") },
            CkAssociationRoles = new List<CkAssociationRoleDto>
            {
                new()
                {
                    AssociationRoleId = "Related", InboundMultiplicity = MultiplicitiesDto.N,
                    OutboundMultiplicity = MultiplicitiesDto.N, InboundName = "Related", OutboundName = "Related"
                }
            },
            CkAttributes = new List<CkAttributeDto>
            {
                new()
                {
                    AttributeId = "attribute1",
                    ValueType = AttributeValueTypesDto.String,
                }
            },
            CkTypes = new List<CkTypeDto>
            {
                new()
                {
                    TypeId = "Demo1",
                    Attributes = new List<CkTypeAttributeDto>
                    {
                        new() { AttributeId = "sample1/attribute1", AttributeName = "a" }
                    }
                }
            },
        };
    }
}