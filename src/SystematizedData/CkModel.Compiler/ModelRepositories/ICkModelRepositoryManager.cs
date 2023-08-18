using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepositories;

public interface ICkModelRepositoryManager
{
    public Task<CkModelRoot?> LookupCkModelAsync(CkModelId ckDependency);
}