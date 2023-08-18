using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace CkModel.Compiler.Tests.sampleData.sample_assocs_sameIdAndBaseTargetAtInheritance_fail;

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
                    RoleId = "Related", InboundMultiplicity = MultiplicitiesDto.N,
                    OutboundMultiplicity = MultiplicitiesDto.N, InboundName = "Related", OutboundName = "Related"
                }
            },
            CkAttributes = new List<CkAttributeDto>
            {
              new()
              {
                  AttributeId = "attribute1",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attribute2",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attribute3",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attribute4",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attribute5",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attribute6",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attribute7",
                  ValueType = AttributeValueTypesDto.String,
              }
            },
            CkTypes = new List<CkTypeDto>
            {
                new()
                {
                    TypeId = "Demo1",
                    DerivedFromCkTypeId = "System/Entity",
                    Attributes = new List<CkTypeAttributeDto>
                    {
                        new() { AttributeId = "sample1/attribute1", AttributeName = "a" },
                        new() { AttributeId = "sample1/attribute2", AttributeName = "b" },
                        new() { AttributeId = "sample1/attribute3", AttributeName = "c" }
                    }
                },
                new()
                {
                    TypeId = "Demo2",
                    DerivedFromCkTypeId = "sample1/Demo1",
                    Attributes = new List<CkTypeAttributeDto>
                    {
                        new() { AttributeId = "sample1/attribute4", AttributeName = "d" },
                        new() { AttributeId = "sample1/attribute5", AttributeName = "e" },
                        new() { AttributeId = "sample1/attribute6", AttributeName = "f" }
                    },
                    Associations = new List<CkTypeAssociationDto>
                    {
                        new() { RoleId = "System/ParentChild", TargetCkTypeId = "sample1/Demo1" },
                        new() { RoleId = "sample1/Related", TargetCkTypeId = "sample1/Demo1" } // here
                    }
                    
                },
                new()
                {
                    TypeId = "Demo3",
                    DerivedFromCkTypeId = "sample1/Demo2",
                    Associations = new List<CkTypeAssociationDto>
                    {
                        new() { RoleId = "sample1/Related", TargetCkTypeId = "System/Entity" }, // here
                    }
                    
                }
            },
        };
    }
}