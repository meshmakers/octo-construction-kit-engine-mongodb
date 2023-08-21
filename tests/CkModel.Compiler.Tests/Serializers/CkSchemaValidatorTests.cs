using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;

namespace CkModel.Compiler.Tests.Serializers;

public class CkSchemaValidatorTests
{
    
    [Fact]
    public void ValidateElementsInJson_ok()
    {
        var schemaValidator = new CkSchemaValidator();

        var streamReader = File.OpenRead("sampleData/files/ok.json");
        var compilerResult = new CompilerResult();
        bool isValid = schemaValidator.ValidateElementsInJson(streamReader, compilerResult);
        Assert.True(isValid);
        Assert.False(compilerResult.Messages.Any());
        Assert.False(compilerResult.HasErrors);
        Assert.False(compilerResult.HasFatalErrors);
    }
    
    [Fact]
    public void ValidateElementsInJson_MalformedAttribute_Fail()
    {
        var schemaValidator = new CkSchemaValidator();

        var streamReader = File.OpenRead("sampleData/files/malformedAttribute.json");
        var compilerResult = new CompilerResult();
        var isValid = schemaValidator.ValidateElementsInJson(streamReader, compilerResult);
        Assert.False(isValid);
        Assert.Single(compilerResult.Messages);
        Assert.True(compilerResult.HasErrors);
        Assert.False(compilerResult.HasFatalErrors);
        Assert.Equal(27, compilerResult.Messages[0].MessageNumber);
    }
    
    [Fact]
    public void ValidateElementsInJson_MalformedAttributeValue_Fail()
    {
        var schemaValidator = new CkSchemaValidator();

        var streamReader = File.OpenRead("sampleData/files/malformedAttributeValue.json");
        var compilerResult = new CompilerResult();
        var isValid = schemaValidator.ValidateElementsInJson(streamReader, compilerResult);
        Assert.False(isValid);
        Assert.Single(compilerResult.Messages);
        Assert.True(compilerResult.HasErrors);
        Assert.False(compilerResult.HasFatalErrors);
        Assert.Equal(27, compilerResult.Messages[0].MessageNumber);
    }
    
    
    [Fact]
    public void ValidateElementsInYaml_ok()
    {
        var schemaValidator = new CkSchemaValidator();

        var streamReader = File.OpenText("sampleData/files/ok.yaml");
        var compilerResult = new CompilerResult();
        var isValid = schemaValidator.ValidateElementsInYaml(streamReader, compilerResult);
        Assert.True(isValid);
        Assert.False(compilerResult.Messages.Any());
        Assert.False(compilerResult.HasErrors);
        Assert.False(compilerResult.HasFatalErrors);
    }
    
    [Fact]
    public void ValidateElementsInYaml_MalformedAttribute_Fail()
    {
        var schemaValidator = new CkSchemaValidator();

        var streamReader = File.OpenText("sampleData/files/malformedAttribute.yaml");
        var compilerResult = new CompilerResult();
        var isValid = schemaValidator.ValidateElementsInYaml(streamReader, compilerResult);
        Assert.False(isValid);
        Assert.Single(compilerResult.Messages);
        Assert.True(compilerResult.HasErrors);
        Assert.False(compilerResult.HasFatalErrors);
        Assert.Equal(27, compilerResult.Messages[0].MessageNumber);
    }
    
    [Fact]
    public void ValidateElementsInYaml_MalformedAttributeValue_Fail()
    {
        var schemaValidator = new CkSchemaValidator();

        var streamReader = File.OpenText("sampleData/files/malformedAttributeValue.yaml");
        var compilerResult = new CompilerResult();
        var isValid = schemaValidator.ValidateElementsInYaml(streamReader, compilerResult);
        Assert.False(isValid);
        Assert.Single(compilerResult.Messages);
        Assert.True(compilerResult.HasErrors);
        Assert.False(compilerResult.HasFatalErrors);
        Assert.Equal(27, compilerResult.Messages[0].MessageNumber);
    }
}