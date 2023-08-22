using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

/// <summary>
/// Implements a serializer for the CK model in YAML format.
/// </summary>
/// <remarks>
/// Currently there is no YAML serializer that supports JSON schema validation
/// out of the box. Therefore we use the YamlDotNet library and implement the validation
/// using the <see cref="ICkSchemaValidator"/> interface. That results that the stream
/// is used twice: for validation and for deserialization. This is not optimal.
/// </remarks>
public class CkYamlSerializer : ICkSerializer
{
    private readonly ICkSchemaValidator _ckSchemaValidator;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    // ReSharper disable once ConvertConstructorToMemberInitializers
    public CkYamlSerializer(ICkSchemaValidator ckSchemaValidator)
    {
        _ckSchemaValidator = ckSchemaValidator;

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new CkModelIdConverter())
            .WithTypeConverter(new CkTypeIdConverter())
            .WithTypeConverter(new CkAttributeIdConverter())
            .WithTypeConverter(new CkAssociationIdConverter())
            .WithTypeConverter(new CkIdAttributeIdConverter())
            .WithTypeConverter(new CkIdTypeIdConverter())
            .WithTypeConverter(new CkIdAssociationIdConverter())
            .Build();
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new CkModelIdConverter())
            .WithTypeConverter(new CkTypeIdConverter())
            .WithTypeConverter(new CkAttributeIdConverter())
            .WithTypeConverter(new CkAssociationIdConverter())
            .WithTypeConverter(new CkIdAttributeIdConverter())
            .WithTypeConverter(new CkIdTypeIdConverter())
            .WithTypeConverter(new CkIdAssociationIdConverter())
            .IgnoreUnmatchedProperties() // set because $schema is not in the model and we don't want to fail on it
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

    public Task<CkMetaDto> DeserializeMetaAsync(Stream stream, OperationResult operationResult)
    {
        _ckSchemaValidator.ValidateMetaInYaml(stream, operationResult);
        if (operationResult.HasErrors)
        {
            throw ModelParseException.SchemaValidationFailed();
        }

        using var streamReader = new StreamReader(stream);
        var ckMetaDto = _deserializer.Deserialize<CkMetaDto>(streamReader);
        return Task.FromResult(ckMetaDto);
    }

    public Task<CkElementsDto> DeserializeElementsAsync(Stream stream, OperationResult operationResult)
    {
        _ckSchemaValidator.ValidateElementsInYaml(stream, operationResult);
        if (operationResult.HasErrors)
        {
            throw ModelParseException.SchemaValidationFailed();
        }

        using var streamReader = new StreamReader(stream);
        var ckElementsDto = _deserializer.Deserialize<CkElementsDto>(streamReader);
        return Task.FromResult(ckElementsDto);
    }

    public async Task<CkCompiledModelRoot?> DeserializeModelRootAsync(string s, OperationResult operationResult)
    {
        byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(s);
        using var memStream = new MemoryStream(byteArray);
        return await DeserializeModelRootAsync(memStream, operationResult);
    }

    public Task<CkCompiledModelRoot> DeserializeModelRootAsync(Stream stream, OperationResult operationResult)
    {
        _ckSchemaValidator.ValidateCompiledModelInYaml(stream, operationResult);
        if (operationResult.HasErrors)
        {
            throw ModelParseException.SchemaValidationFailed();
        }

        using var streamReader = new StreamReader(stream);
        var ckModelRoot = _deserializer.Deserialize<CkCompiledModelRoot>(streamReader);
        return Task.FromResult(ckModelRoot);
    }
}