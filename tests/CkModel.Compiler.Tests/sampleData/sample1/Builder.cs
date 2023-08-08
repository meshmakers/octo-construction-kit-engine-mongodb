using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;

namespace CkModel.Compiler.Tests.sampleData.sample1;

public class Builder
{
    public static CkModelRoot Build()
    {
        return new CkModelRoot
        {
            ModelId = new CkModelId("sample1", "1.0.0"),
            CkDependencies = new List<CkModelId> { new("System", "1.0.0") },
            CkAttributes = new List<CkAttribute>
            {
              new()
              {
                  AttributeId = "attribute1",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attribute2",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attribute3",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attribute4",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attribute5",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attribute6",
                  ValueType = AttributeValueTypes.String,
              },
              new()
              {
                  AttributeId = "attribute7",
                  ValueType = AttributeValueTypes.String,
              }
            },
            CkEntities = new List<CkEntity>
            {
                new()
                {
                    TypeId = "Demo1",
                    DerivedCkTypeId = "System/Entity",
                    Attributes = new List<CkEntityAttribute>
                    {
                        new() { AttributeId = "sample1/attribute1", AttributeName = "a" },
                        new() { AttributeId = "sample1/attribute2", AttributeName = "b" },
                        new() { AttributeId = "sample1/attribute3", AttributeName = "c" }
                    }
                },
                new()
                {
                    TypeId = "Demo2",
                    DerivedCkTypeId = "sample1/Demo1",
                    Attributes = new List<CkEntityAttribute>
                    {
                        new() { AttributeId = "sample1/attribute4", AttributeName = "d" },
                        new() { AttributeId = "sample1/attribute5", AttributeName = "e" },
                        new() { AttributeId = "sample1/attribute6", AttributeName = "f" }
                    }
                }
            },
        };
    }
}