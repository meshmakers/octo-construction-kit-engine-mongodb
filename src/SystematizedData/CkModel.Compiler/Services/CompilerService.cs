using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Services;

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

        var typesDirectory = Path.Combine(rootPath, CompilerStatics.TypesDirectoryName);
        Directory.CreateDirectory(Path.Combine(rootPath, CompilerStatics.AssociationsDirectoryName));
        Directory.CreateDirectory(Path.Combine(rootPath, CompilerStatics.AttributesDirectoryName));
        Directory.CreateDirectory(typesDirectory);

        var modelDto = new CkMetaDto
        {
            ModelId = "Sample1",
            Dependencies = new List<CkModelId> { new("System") }
        };

        await using var streamWriter = new StreamWriter(Path.Combine(rootPath, CompilerStatics.MetadataFile));
        await _ckSerializer.SerializeAsync(streamWriter, modelDto);

        var ckTypeDto = new CkTypeDto
        {
            TypeId = "Demo1"
        };
        await using var streamWriterEntity = new StreamWriter(Path.Combine(typesDirectory, CompilerStatics.Sample1Entity));
        await _ckSerializer.SerializeAsync(streamWriterEntity, new CkCompiledModelRoot { Types = new List<CkTypeDto> { ckTypeDto } });

        if (compilerResult.HasErrors)
        {
            throw CompilerException.CompilerResultWithErrors(compilerResult);
        }
    }
}