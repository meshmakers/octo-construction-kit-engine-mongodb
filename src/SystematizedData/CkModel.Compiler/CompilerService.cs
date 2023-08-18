using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

public class CompilerService
{
    private readonly ICkSerializer _ckSerializer;

    public CompilerService(ICkSerializer ckSerializer)
    {
        _ckSerializer = ckSerializer;
    }
    
    public async Task CreateNewAsync(string rootPath)
    {
        ArgumentValidation.ValidateDirectoryPath(nameof(rootPath), rootPath);

        CompilerResult compilerResult = new CompilerResult();


        if (Directory.Exists(rootPath) && !Directory.EnumerateFileSystemEntries(rootPath).Any())
        {
            compilerResult.AddMessage(MessageCodes.DirectoryMustBeEmpty(rootPath));
            throw CompilerException.DirectoryMustBeEmpty(rootPath);
        }

        Directory.CreateDirectory(Path.Combine(rootPath, CompilerStatics.AssociationsDirectoryName));
        Directory.CreateDirectory(Path.Combine(rootPath, CompilerStatics.AttributesDirectoryName));
        Directory.CreateDirectory(Path.Combine(rootPath, CompilerStatics.TypesDirectoryName));
     
        var modelDto = new CkMetaDto
        {
            ModelId = "Sample1",
            Dependencies = new List<CkModelId> { new("System") }
        };

        await using var streamWriter = new StreamWriter(Path.Combine(rootPath, CompilerStatics.MetadataFile));
        await _ckSerializer.SerializeAsync(streamWriter, modelDto);
        
        if (compilerResult.HasErrors)
        {
            throw CompilerException.CompilerResultWithErrors(compilerResult);
        }
    }
}

