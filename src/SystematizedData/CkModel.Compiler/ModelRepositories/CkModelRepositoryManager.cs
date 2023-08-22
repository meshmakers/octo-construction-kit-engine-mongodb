using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepositories;

public class CkModelRepositoryManager : ICkModelRepositoryManager
{
    public Task<CkCompiledModelRoot?> LookupCkModelAsync(CkModelId ckDependency)
    {
        throw new NotImplementedException();
    }
}