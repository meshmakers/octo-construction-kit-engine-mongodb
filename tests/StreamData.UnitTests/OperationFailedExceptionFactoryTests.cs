using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

public class OperationFailedExceptionFactoryTests
{
    [Fact]
    public void PreImageCaptureNotEnabled_message_names_the_root_type_and_flag()
    {
        var rootId = new CkId<CkTypeId>(
            new CkModelId("Sample", new CkVersion("1.0.0")),
            new CkTypeId("WatchTarget"));

        var ex = OperationFailedException.PreImageCaptureNotEnabled(rootId);

        Assert.IsType<OperationFailedException>(ex);
        Assert.Contains("WatchTarget", ex.Message);
        Assert.Contains("EnableChangeStreamPreAndPostImages", ex.Message);
        Assert.Contains("BeforeFieldFilterCriteria", ex.Message);
    }
}
