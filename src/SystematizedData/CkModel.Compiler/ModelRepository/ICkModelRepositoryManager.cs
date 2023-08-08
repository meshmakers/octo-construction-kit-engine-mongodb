using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepository;

public interface ICkModelRepositoryManager
{
    public Task<CkModelRoot?> LookupCkModelAsync(CkModelId ckDependency);
}