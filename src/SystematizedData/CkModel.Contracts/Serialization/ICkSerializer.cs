using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

public interface ICkSerializer
{
    Task SerializeAsync(StreamWriter streamWriter, CkCompiledModelRoot compiledModel);
    Task SerializeAsync(StreamWriter streamWriter, CkMetaDto metaDto);
    Task SerializeAsync(StreamWriter streamWriter, CkElementsDto elementsDto);
    Task<CkMetaDto> DeserializeMetaAsync(StreamReader streamReader);
    Task<CkElementsDto> DeserializeElementsAsync(StreamReader streamReader);
    Task<CkCompiledModelRoot?> DeserializeModelRootAsync(string s);
    Task<CkCompiledModelRoot> DeserializeModelRootAsync(StreamReader streamReader);
}