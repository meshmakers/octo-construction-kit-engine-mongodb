using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;

namespace CkModel.Compiler.Tests.sampleData.sample2;

public class Builder
{
    public static CkModelRoot Build()
    {
        return new CkModelRoot
        {
            ModelId = new CkModelId("sample2", "1.0.0"),
            CkDependencies = new List<CkModelId> { new("System", "1.0.0"), new ("sample1", "1.0.0") },
            CkAttributes = new List<CkAttribute>
            {
              new()
              {
                  AttributeId = "attributeA",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attributeB",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attributeC",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attributeD",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attributeE",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attributeF",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attributeG",
                  ValueType = AttributeValueTypes.String,
              }
            },
            CkEntities = new List<CkEntity>
            {
                new()
                {
                    TypeId = "Sample2Demo2",
                    DerivedCkTypeId = "System/Entity",
                    Attributes = new List<CkEntityAttribute>
                    {
                        new() { AttributeId = "sample1/attribute1", AttributeName = "a" },
                        new() { AttributeId = "sample2/attributeA", AttributeName = "b" },
                        new() { AttributeId = "sample3/attributeB", AttributeName = "c" }
                    }
                },
                new()
                {
                    TypeId = "Demo2",
                    DerivedCkTypeId = "sample1/Demo1",
                    Attributes = new List<CkEntityAttribute>
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