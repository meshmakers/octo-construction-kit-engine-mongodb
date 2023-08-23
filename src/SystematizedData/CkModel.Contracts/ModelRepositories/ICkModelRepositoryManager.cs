using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.ModelRepositories;

public interface ICkModelRepositoryManager
{
    public Task<CkCompiledModelRoot?> LookupCkModelAsync(CkModelId ckModelId);
}