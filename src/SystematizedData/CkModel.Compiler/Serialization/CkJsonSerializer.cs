using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

public class CkJsonSerializer : ICkSerializer
{
    private readonly JsonSerializerOptions _options;

    public CkJsonSerializer()
    {
        _options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }
    
    public async Task SerializeAsync(StreamWriter streamWriter, CkModelRoot model)
    {
        await JsonSerializer.SerializeAsync(streamWriter.BaseStream, model, _options);
    }
    
    public async Task SerializeAsync(StreamWriter streamWriter, CkMetaDto metaDto)
    {
        await JsonSerializer.SerializeAsync(streamWriter.BaseStream, metaDto);
    }
    
    public async Task<CkMetaDto> DeserializeMetaAsync(StreamReader streamReader)
    {
        var ckMetaDto = await JsonSerializer.DeserializeAsync<CkMetaDto>(streamReader.BaseStream, _options);
        return ckMetaDto ?? throw ModelParseException.CannotDeserializeModel();
    }

    public async Task<CkModelRoot?> DeserializeModelRootAsync(string s)
    {
        byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(s);
        using var memStream = new MemoryStream(byteArray);
        using var streamReader = new StreamReader(memStream);   
        return await DeserializeModelRootAsync(streamReader);
    }

    public async Task<CkModelRoot> DeserializeModelRootAsync(StreamReader streamReader)
    {
        var ckModelRoot = await JsonSerializer.DeserializeAsync<CkModelRoot>(streamReader.BaseStream, _options);
        return ckModelRoot ?? throw ModelParseException.CannotDeserializeModel();
    }
}
