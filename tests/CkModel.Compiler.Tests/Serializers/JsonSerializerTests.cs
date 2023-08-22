using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;

namespace CkModel.Compiler.Tests.Serializers;

public class JsonSerializerTests
{
    [Fact]
    public async Task DeserializeElementsAsync_ok()
    {
        var ckJsonSerializer = new CkJsonSerializer();
    
        var stream = File.OpenRead("sampleData/files/ok.json");
        var operationResult = new OperationResult();
        await ckJsonSerializer.DeserializeElementsAsync(stream, operationResult);
        Assert.Empty(operationResult.Messages);
        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
    }
    
    [Fact]
    public async Task DeserializeElementsAsync_noSchema_ok()
    {
        var ckJsonSerializer = new CkJsonSerializer();
    
        var stream = File.OpenRead("sampleData/files/noSchema.json");
        var operationResult = new OperationResult();
        await ckJsonSerializer.DeserializeElementsAsync(stream, operationResult);
        Assert.Empty(operationResult.Messages);
        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
    }
    
    [Fact]
    public async Task DeserializeElementsAsync_noSchema_malFormed_fail()
    {
        var ckJsonSerializer = new CkJsonSerializer();
    
        var stream = File.OpenRead("sampleData/files/noSchema_malformed.json");
        var operationResult = new OperationResult();
        await Assert.ThrowsAsync<ModelParseException>(async () => await ckJsonSerializer.DeserializeElementsAsync(stream, operationResult));
        Assert.Single(operationResult.Messages);
        Assert.True(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
        Assert.Equal(27, operationResult.Messages[0].MessageNumber);
    }
    
    [Fact]
    public async Task DeserializeElementsAsync_MalformedAttribute_Fail()
    {
        var ckJsonSerializer = new CkJsonSerializer();
    
        var stream = File.OpenRead("sampleData/files/malformedAttribute.json");
        var operationResult = new OperationResult();
        await Assert.ThrowsAsync<ModelParseException>(async () => await ckJsonSerializer.DeserializeElementsAsync(stream, operationResult));
        Assert.Single(operationResult.Messages);
        Assert.True(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
        Assert.Equal(27, operationResult.Messages[0].MessageNumber);
    }
        
    [Fact]
    public async Task DeserializeElementsAsync_MalformedAttributeValue_Fail()
    {
        var ckJsonSerializer = new CkJsonSerializer();
    
        var stream = File.OpenRead("sampleData/files/malformedAttributeValue.json");
        var operationResult = new OperationResult();
        await Assert.ThrowsAsync<ModelParseException>(async () => await ckJsonSerializer.DeserializeElementsAsync(stream, operationResult));
        Assert.Single(operationResult.Messages);
        Assert.True(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
        Assert.Equal(27, operationResult.Messages[0].MessageNumber);
    }
}