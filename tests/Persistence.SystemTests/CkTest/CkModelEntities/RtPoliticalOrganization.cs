using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.CkTest.CkModelEntities;

[CkId(TestCkModel.TestCkModelId, TestCkModel.PoliticalOrganizationTypeId)]
public class RtPoliticalOrganization : RtEntity
{
    public string Designation
    {
        get => GetAttributeStringValue(nameof(Designation));
        set => SetAttributeValue(nameof(Designation), AttributeValueTypes.String, value);
    }
    
   // [QlConnection("relatesTo", "meshmakersCityConnection")]
   // public QlItemsContainer<RtCity>? RelatedCities { get; set; }
}