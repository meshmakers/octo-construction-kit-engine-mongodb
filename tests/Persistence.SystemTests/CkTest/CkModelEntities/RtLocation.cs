using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.CkTest.CkModelEntities;

[CkId(TestCkModel.TestCkModelId, TestCkModel.LocationTypeId)]
public class RtLocation : RtEntity
{
    public string Designation
    {
        get => GetAttributeStringValue(nameof(Designation));
        set => SetAttributeValue(nameof(Designation), AttributeValueTypes.String, value);
    }
}