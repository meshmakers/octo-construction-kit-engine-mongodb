using CkModel.Compiler.SystemTests.Fixtures;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Services;

namespace CkModel.Compiler.SystemTests;

public class CompilerTests : IClassFixture<TemporaryDirectoryFixture>
{
    private readonly TemporaryDirectoryFixture _fixture;

    public CompilerTests(TemporaryDirectoryFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateNew()
    {
        // Access _fixture.TempDirectoryPath to work with the temporary directory
        string filePath = Path.Combine(_fixture.TempDirectoryPath, "CreateNew");
        CompilerService compilerService = new CompilerService(new CkYamlSerializer(new CkSchemaValidator()));
        await compilerService.CreateNewAsync(filePath);

        Assert.True(Directory.Exists(filePath));
        Assert.True(File.Exists(Path.Combine(filePath, CompilerStatics.MetadataFile)));
        Assert.True(Directory.Exists(Path.Combine(filePath, CompilerStatics.TypesDirectoryName)));
        Assert.True(Directory.Exists(Path.Combine(filePath, CompilerStatics.AssociationsDirectoryName)));
        Assert.True(Directory.Exists(Path.Combine(filePath, CompilerStatics.AttributesDirectoryName)));
    }
}