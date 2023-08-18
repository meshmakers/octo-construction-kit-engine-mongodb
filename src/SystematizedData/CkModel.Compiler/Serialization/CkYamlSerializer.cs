using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using YamlDotNet.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

public class CkYamlSerializer : ICkSerializer
{
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public CkYamlSerializer()
    {
        _serializer = new SerializerBuilder().WithTypeConverter(new CkModelIdConverter()).Build();
        _deserializer = new DeserializerBuilder().WithTypeConverter(new CkModelIdConverter()).Build();
    }

    public Task SerializeAsync(StreamWriter streamWriter, CkModelRoot model)
    {
        _serializer.Serialize(streamWriter, model);
        return Task.CompletedTask;
    }

    public Task SerializeAsync(StreamWriter streamWriter, CkMetaDto metaDto)
    {
        _serializer.Serialize(streamWriter, metaDto);
        return Task.CompletedTask;
    }
    
    
    public Task<CkMetaDto> DeserializeMetaAsync(StreamReader streamReader)
    {
        var ckMetaDto = _deserializer.Deserialize<CkMetaDto>(streamReader);
        return Task.FromResult(ckMetaDto);
    }

    public async Task<CkModelRoot?> DeserializeModelRootAsync(string s)
    {
        byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(s);
        using var memStream = new MemoryStream(byteArray);
        using var streamReader = new StreamReader(memStream);   
        return await DeserializeModelRootAsync(streamReader);
    }

    public Task<CkModelRoot> DeserializeModelRootAsync(StreamReader streamReader)
    {
        var ckModelRoot = _deserializer.Deserialize<CkModelRoot>(streamReader);
        return Task.FromResult(ckModelRoot);
    }
}