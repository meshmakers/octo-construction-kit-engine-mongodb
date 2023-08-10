using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepository;

public interface ICkModelRepositoryManager
{
    public Task<CkModelRoot?> LookupCkModelAsync(CkModelId ckDependency);
}