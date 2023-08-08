using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepository;

public class LocalFileSystemCkModelRepository : ICkModelRepository
{
    public Task<CkModelId> FindModelIdAsync(CkModelId modelId)
    {
        throw new NotImplementedException();
    }

    public Task<CkModelRoot> GetModelAsync(CkModelId modelId)
    {
        throw new NotImplementedException();
    }

    public Task PublishModelAsync(CkModelRoot ckModel)
    {
        throw new NotImplementedException();
    }

    public Task UpdateModelAsync(CkModelRoot ckModel)
    {
        throw new NotImplementedException();
    }
}