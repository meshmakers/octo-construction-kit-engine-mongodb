using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace CkModel.Compiler.Tests.sampleData.sample_assocs_sameRoleIdDifferentTrees_ok;

public class Builder
{
    public static CkModelRoot Build()
    {
        return new CkModelRoot
        {
            ModelId = new CkModelId("sample1", "1.0.0"),
            CkDependencies = new List<CkModelId> { new("System", "1.0.0") },
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
            CkEntities = new List<CkEntityDto>
            {
                new()
                {
                    TypeId = "Alpha1",
                    DerivedFromCkTypeId = "System/Entity",
                    Attributes = new List<CkEntityAttributeDto>
                    {
                        new() { AttributeId = "sample1/attribute1", AttributeName = "a" },
                        new() { AttributeId = "sample1/attribute2", AttributeName = "b" },
                        new() { AttributeId = "sample1/attribute3", AttributeName = "c" }
                    }
                },
                new()
                {
                    TypeId = "Alpha2",
                    DerivedFromCkTypeId = "sample1/Alpha1",
                    Attributes = new List<CkEntityAttributeDto>
                    {
                        new() { AttributeId = "sample1/attribute4", AttributeName = "d" },
                        new() { AttributeId = "sample1/attribute5", AttributeName = "e" },
                        new() { AttributeId = "sample1/attribute6", AttributeName = "f" }
                    }
                },
                new()
                {
                    TypeId = "Alpha3",
                    DerivedFromCkTypeId = "sample1/Alpha2",
                    Associations = new List<CkEntityAssociationDto>
                    {
                        new() { RoleId = "sample1/Related", TargetCkTypeId = "sample1/Alpha1" }, // here
                    }
                    
                },
                
                new()
                {
                    TypeId = "Beta1",
                    DerivedFromCkTypeId = "System/Entity",
                    Attributes = new List<CkEntityAttributeDto>
                    {
                        new() { AttributeId = "sample1/attribute1", AttributeName = "a" },
                        new() { AttributeId = "sample1/attribute2", AttributeName = "b" },
                        new() { AttributeId = "sample1/attribute3", AttributeName = "c" }
                    }
                },
                new()
                {
                    TypeId = "Beta2",
                    DerivedFromCkTypeId = "sample1/Beta1",
                    Attributes = new List<CkEntityAttributeDto>
                    {
                        new() { AttributeId = "sample1/attribute4", AttributeName = "d" },
                        new() { AttributeId = "sample1/attribute5", AttributeName = "e" },
                        new() { AttributeId = "sample1/attribute6", AttributeName = "f" }
                    }
                },
                new()
                {
                    TypeId = "Beta3",
                    DerivedFromCkTypeId = "sample1/Beta2",
                    Associations = new List<CkEntityAssociationDto>
                    {
                        new() { RoleId = "sample1/Related", TargetCkTypeId = "sample1/Beta2" }, // here
                    }
                    
                }
            },
        };
    }
}