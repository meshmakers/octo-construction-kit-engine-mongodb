using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;

namespace CkModel.Compiler.Tests.Serializers;

public class YamlSerializerTests
{
    [Fact]
    public async Task DeserializeElementsAsync_types_ok()
    {
        var ckYamlSerializer = new CkYamlSerializer(new CkSchemaValidator());

        var stream = File.OpenRead("sampleData/files/types-ok.yaml");
        var operationResult = new OperationResult();
        var ckElementsDto = await ckYamlSerializer.DeserializeElementsAsync(stream, operationResult);
        Assert.NotNull(ckElementsDto);
        Assert.Empty(operationResult.Messages);
        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
    }

    [Fact]
    public async Task DeserializeElementsAsync_attributes_ok()
    {
        var ckYamlSerializer = new CkYamlSerializer(new CkSchemaValidator());

        var stream = File.OpenRead("sampleData/files/attributes-ok.yaml");
        var operationResult = new OperationResult();
        var ckElementsDto = await ckYamlSerializer.DeserializeElementsAsync(stream, operationResult);
        Assert.NotNull(ckElementsDto);
        Assert.Empty(operationResult.Messages);
        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
    }
    
    [Fact]
    public async Task DeserializeElementsAsync_associations_ok()
    {
        var ckYamlSerializer = new CkYamlSerializer(new CkSchemaValidator());

        var stream = File.OpenRead("sampleData/files/associations-ok.yaml");
        var operationResult = new OperationResult();
        var ckElementsDto = await ckYamlSerializer.DeserializeElementsAsync(stream, operationResult);
        Assert.NotNull(ckElementsDto);
        Assert.Empty(operationResult.Messages);
        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
    }

    [Fact]
    public async Task DeserializeElementsAsync_noSchema_ok()
    {
        var ckYamlSerializer = new CkYamlSerializer(new CkSchemaValidator());

        var stream = File.OpenRead("sampleData/files/noSchema.yaml");
        var operationResult = new OperationResult();
        var ckElementsDto = await ckYamlSerializer.DeserializeElementsAsync(stream, operationResult);
        Assert.NotNull(ckYamlSerializer);
        Assert.Equal(4, ckElementsDto.Types?.Count);
        Assert.Empty(operationResult.Messages);
        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
    }

    [Fact]
    public async Task DeserializeElementsAsync_noSchema_malFormed_fail()
    {
        var ckYamlSerializer = new CkYamlSerializer(new CkSchemaValidator());

        var stream = File.OpenRead("sampleData/files/noSchema_malformed.yaml");
        var operationResult = new OperationResult();
        await Assert.ThrowsAsync<ModelParseException>(async () => await ckYamlSerializer.DeserializeElementsAsync(stream, operationResult));
        Assert.Single(operationResult.Messages);
        Assert.True(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
        Assert.Equal(27, operationResult.Messages[0].MessageNumber);
    }

    [Fact]
    public async Task DeserializeElementsAsync_MalformedAttribute_Fail()
    {
        var ckYamlSerializer = new CkYamlSerializer(new CkSchemaValidator());

        var stream = File.OpenRead("sampleData/files/malformedAttribute.yaml");
        var operationResult = new OperationResult();
        await Assert.ThrowsAsync<ModelParseException>(async () => await ckYamlSerializer.DeserializeElementsAsync(stream, operationResult));
        Assert.Single(operationResult.Messages);
        Assert.True(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
        Assert.Equal(27, operationResult.Messages[0].MessageNumber);
    }

    [Fact]
    public async Task DeserializeElementsAsync_MalformedAttributeValue_Fail()
    {
        var ckYamlSerializer = new CkYamlSerializer(new CkSchemaValidator());

        var stream = File.OpenRead("sampleData/files/malformedAttributeValue.yaml");
        var operationResult = new OperationResult();
        await Assert.ThrowsAsync<ModelParseException>(async () => await ckYamlSerializer.DeserializeElementsAsync(stream, operationResult));
        Assert.Single(operationResult.Messages);
        Assert.True(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
        Assert.Equal(27, operationResult.Messages[0].MessageNumber);
    }
}