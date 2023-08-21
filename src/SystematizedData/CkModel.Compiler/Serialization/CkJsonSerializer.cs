using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

public class CkJsonSerializer : ICkSerializer
{
    private readonly JsonSerializerOptions _options;

    // ReSharper disable once ConvertConstructorToMemberInitializers
    public CkJsonSerializer()
    {
        _options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }
    
    public async Task SerializeAsync(StreamWriter streamWriter, CkCompiledModelRoot compiledModel)
    {
        await JsonSerializer.SerializeAsync(streamWriter.BaseStream, compiledModel, _options);
    }
    
    public async Task SerializeAsync(StreamWriter streamWriter, CkMetaDto metaDto)
    {
        await JsonSerializer.SerializeAsync(streamWriter.BaseStream, metaDto);
    }

    public async Task SerializeAsync(StreamWriter streamWriter, CkElementsDto elementsDto)
    {
        await JsonSerializer.SerializeAsync(streamWriter.BaseStream, elementsDto);
    }

    public async Task<CkMetaDto> DeserializeMetaAsync(StreamReader streamReader)
    {
        var ckMetaDto = await JsonSerializer.DeserializeAsync<CkMetaDto>(streamReader.BaseStream, _options);
        return ckMetaDto ?? throw ModelParseException.CannotDeserializeModel();
    }

    public async Task<CkElementsDto> DeserializeElementsAsync(StreamReader streamReader)
    {
        var ckElementsDto = await JsonSerializer.DeserializeAsync<CkElementsDto>(streamReader.BaseStream, _options);
        return ckElementsDto ?? throw ModelParseException.CannotDeserializeModel();
    }

    public async Task<CkCompiledModelRoot?> DeserializeModelRootAsync(string s)
    {
        byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(s);
        using var memStream = new MemoryStream(byteArray);
        using var streamReader = new StreamReader(memStream);   
        return await DeserializeModelRootAsync(streamReader);
    }

    public async Task<CkCompiledModelRoot> DeserializeModelRootAsync(StreamReader streamReader)
    {
        var ckModelRoot = await JsonSerializer.DeserializeAsync<CkCompiledModelRoot>(streamReader.BaseStream, _options);
        return ckModelRoot ?? throw ModelParseException.CannotDeserializeModel();
    }
}
