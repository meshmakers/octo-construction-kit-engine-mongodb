using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepositories;

public class LocalFileSystemCkModelRepository : ICkModelRepository
{
    public Task<CkModelId> FindModelIdAsync(CkModelId modelId)
    {
        throw new NotImplementedException();
    }

    public Task<CkCompiledModelRoot> GetModelAsync(CkModelId modelId)
    {
        throw new NotImplementedException();
    }

    public Task PublishModelAsync(CkCompiledModelRoot ckCompiledModel)
    {
        throw new NotImplementedException();
    }

    public Task UpdateModelAsync(CkCompiledModelRoot ckCompiledModel)
    {
        throw new NotImplementedException();
    }
}