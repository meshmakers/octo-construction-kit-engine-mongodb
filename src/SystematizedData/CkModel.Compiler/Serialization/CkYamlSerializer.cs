using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using YamlDotNet.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

public class CkYamlSerializer : ICkSerializer
{
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    // ReSharper disable once ConvertConstructorToMemberInitializers
    public CkYamlSerializer()
    {
        _serializer = new SerializerBuilder()
            .WithTypeConverter(new CkModelIdConverter())
            .WithTypeConverter(new CkTypeIdConverter())
            .WithTypeConverter(new CkAttributeIdConverter())
            .WithTypeConverter(new CkAssociationIdConverter())
            .Build();
        _deserializer = new DeserializerBuilder()
            .WithTypeConverter(new CkModelIdConverter())
            .WithTypeConverter(new CkTypeIdConverter())
            .WithTypeConverter(new CkAttributeIdConverter())
            .WithTypeConverter(new CkAssociationIdConverter())
            .Build();
    }

    public Task SerializeAsync(StreamWriter streamWriter, CkCompiledModelRoot compiledModel)
    {
        _serializer.Serialize(streamWriter, compiledModel);
        return Task.CompletedTask;
    }

    public Task SerializeAsync(StreamWriter streamWriter, CkMetaDto metaDto)
    {
        _serializer.Serialize(streamWriter, metaDto);
        return Task.CompletedTask;
    }

    public Task SerializeAsync(StreamWriter streamWriter, CkElementsDto elementsDto)
    {
        _serializer.Serialize(streamWriter, elementsDto);
        return Task.CompletedTask;
    }

    public Task<CkMetaDto> DeserializeMetaAsync(StreamReader streamReader)
    {
        var ckMetaDto = _deserializer.Deserialize<CkMetaDto>(streamReader);
        return Task.FromResult(ckMetaDto);
    }

    public Task<CkElementsDto> DeserializeElementsAsync(StreamReader streamReader)
    {
        var ckElementsDto = _deserializer.Deserialize<CkElementsDto>(streamReader);
        return Task.FromResult(ckElementsDto);
    }

    public async Task<CkCompiledModelRoot?> DeserializeModelRootAsync(string s)
    {
        byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(s);
        using var memStream = new MemoryStream(byteArray);
        using var streamReader = new StreamReader(memStream);   
        return await DeserializeModelRootAsync(streamReader);
    }

    public Task<CkCompiledModelRoot> DeserializeModelRootAsync(StreamReader streamReader)
    {
        var ckModelRoot = _deserializer.Deserialize<CkCompiledModelRoot>(streamReader);
        return Task.FromResult(ckModelRoot);
    }
}