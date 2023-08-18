using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

public interface ICkSerializer
{
    Task SerializeAsync(StreamWriter streamWriter, CkModelRoot model);
    Task SerializeAsync(StreamWriter streamWriter, CkMetaDto metaDto);
    Task<CkMetaDto> DeserializeMetaAsync(StreamReader streamReader);
    Task<CkModelRoot?> DeserializeModelRootAsync(string s);
    Task<CkModelRoot> DeserializeModelRootAsync(StreamReader streamReader);
}