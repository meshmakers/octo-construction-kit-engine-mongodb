using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace CkModel.Compiler.Tests.sampleData.sample2;

public class Builder
{
    public static CkModelRoot Build()
    {
        return new CkModelRoot
        {
            ModelId = new CkModelId("sample2", "1.0.0"),
            CkDependencies = new List<CkModelId> { new("System", "1.0.0"), new ("sample1", "1.0.0") },
            CkAttributes = new List<CkAttributeDto>
            {
              new()
              {
                  AttributeId = "attributeA",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attributeB",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attributeC",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attributeD",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attributeE",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attributeF",
                  ValueType = AttributeValueTypesDto.String,
              },
              new()
              {
                  AttributeId = "attributeG",
                  ValueType = AttributeValueTypesDto.String,
              }
            },
            CkEntities = new List<CkEntityDto>
            {
                new()
                {
                    TypeId = "Sample2Demo2",
                    DerivedFromCkTypeId = "System/Entity",
                    Attributes = new List<CkEntityAttributeDto>
                    {
                        new() { AttributeId = "sample1/attribute1", AttributeName = "a" },
                        new() { AttributeId = "sample2/attributeA", AttributeName = "b" },
                        new() { AttributeId = "sample3/attributeB", AttributeName = "c" }
                    }
                },
                new()
                {
                    TypeId = "Demo2",
                    DerivedFromCkTypeId = "sample1/Demo1",
                    Attributes = new List<CkEntityAttributeDto>
                    {
                        new() { AttributeId = "sample1/attributeC", AttributeName = "d" },
                        new() { AttributeId = "sample1/attributeD", AttributeName = "e" },
                        new() { AttributeId = "sample1/attributeE", AttributeName = "f" }
                    }
                }
            },
        };
    }
}