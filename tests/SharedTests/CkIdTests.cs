using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace SharedTests;

public class CkIdTests
{
    [Fact]
    public void Copy_CkId()
    {
        var ckId = new CkId<CkTypeId>("System/Designation-1.0.0");
        var ckId2 = ckId;
        test(ckId2);
    }

    private void test(CkId<CkTypeId> ckId2)
    {
        Assert.Equal("System", ckId2.ModelId.ModelId);
        Assert.Equal("Designation", ckId2.Key.TypeId);
        Assert.Equal("1.0.0", ckId2.ModelId.ModelVersion);
        Assert.Equal("1.0.0", ckId2.Key.Version);
    }
}