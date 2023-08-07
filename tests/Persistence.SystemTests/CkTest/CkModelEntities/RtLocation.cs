using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests.CkModelEntities;

[CkId(TestCkModel.TestCkModelId, TestCkModel.CkIdLocation)]
public class RtLocation : RtEntity
{
    public string Designation
    {
        get => GetAttributeStringValue(nameof(Designation));
        set => SetAttributeValue(nameof(Designation), AttributeValueTypes.String, value);
    }
}