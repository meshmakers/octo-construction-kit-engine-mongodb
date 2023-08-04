using Meshmakers.Octo.Common.Shared;

namespace SharedTests;

public class CkTypeIdTests
{
    [Fact]
    public void Create_CkTypeId_Complete()
    {
        var ckId = new CkTypeId("TestEntity-1.0.0");
        Assert.Equal("TestEntity", ckId.TypeId);
        Assert.Equal("1.0.0", ckId.Version);
    }
    
    [Fact]
    public void Create_CkTypeId_WithoutVersion()
    {
        var ckId = new CkTypeId("TestEntity");
        Assert.Equal("TestEntity", ckId.TypeId);
        Assert.Equal("1.0.0", ckId.Version);
    }
    
    [Fact]
    public void Create_CkTypeId_FromString()
    {
        CkTypeId ckId = "TestEntity-1.0.0";
        Assert.Equal("TestEntity", ckId.TypeId);
        Assert.Equal("1.0.0", ckId.Version);
    }
    
    [Fact]
    public void Create_CkTypeId_Malformed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CkTypeId(""));
    }
    
    [Fact]
    public void Compare_CkTypeId_Same()
    {
        var ckId1 = new CkTypeId("System/TestEntity-1.0.0");
        var ckId2 = new CkTypeId("System/TestEntity-1.0.0");
        Assert.Equal(0, ckId1.CompareTo(ckId2));
    }

    [Fact]
    public void Compare_CkTypeId_HigherVersion()
    {
        var ckId1 = new CkTypeId("System/TestEntity-1.0.0");
        var ckId2 = new CkTypeId("System/TestEntity-1.0.1");
        Assert.Equal(-1, ckId1.CompareTo(ckId2));
    }
    
    [Fact]
    public void Compare_CkTypeId_LowerVersionVersion()
    {
        var ckId1 = new CkTypeId("System/TestEntity-1.0.1");
        var ckId2 = new CkTypeId("System/TestEntity-1.0.0");
        Assert.Equal(1, ckId1.CompareTo(ckId2));
    }
    
    [Fact]
    public void Compare_CkTypeId_DifferentTypeId()
    {
        var ckId1 = new CkTypeId("TestEntity1-1.0.0");
        var ckId2 = new CkTypeId("TestEntity2-1.0.0");
        Assert.Equal(-1, ckId1.CompareTo(ckId2));
    }
    
    [Fact]
    public void Equal_CkTypeId_Same()
    {
        var ckId1 = new CkTypeId("TestEntity-1.0.0");
        var ckId2 = new CkTypeId("TestEntity-1.0.0");
        Assert.True(ckId1.Equals(ckId2));
    }
    
    [Fact]
    public void Equal_CkTypeId_DifferentVersion()
    {
        var ckId1 = new CkTypeId("TestEntity-1.0.0");
        var ckId2 = new CkTypeId("TestEntity-1.0.1");
        Assert.False(ckId1.Equals(ckId2));
    }
    
    [Fact]
    public void Equal_CkTypeId_DifferentTypeId()
    {
        var ckId1 = new CkTypeId("TestEntity1-1.0.0");
        var ckId2 = new CkTypeId("TestEntity2-1.0.0");
        Assert.False(ckId1.Equals(ckId2));
    }
    
}