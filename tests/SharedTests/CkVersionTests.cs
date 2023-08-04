using Meshmakers.Octo.Common.Shared;

namespace SharedTests;

public class CkVersionTests
{
    [Fact]
    public void Create_CkTypeId_Complete()
    {
        var ckId = new CkVersion("1.0.0");
        Assert.Equal(1, ckId.Major);
        Assert.Equal(0, ckId.Minor);
        Assert.Equal(0, ckId.Revision);
    }
}