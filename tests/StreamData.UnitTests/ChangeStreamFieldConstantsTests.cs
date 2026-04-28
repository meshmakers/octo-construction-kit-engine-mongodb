using MongoDbConstants = Meshmakers.Octo.Runtime.Engine.MongoDb.Constants;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

public class ChangeStreamFieldConstantsTests
{
    [Fact]
    public void ChangeStreamFullDocument_matches_mongodb_field_name()
    {
        Assert.Equal("fullDocument", MongoDbConstants.ChangeStreamFullDocument);
    }

    [Fact]
    public void ChangeStreamFullDocumentBeforeChange_matches_mongodb_field_name()
    {
        Assert.Equal("fullDocumentBeforeChange", MongoDbConstants.ChangeStreamFullDocumentBeforeChange);
    }
}
