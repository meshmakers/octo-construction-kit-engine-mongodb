using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepository;

public interface ICkModelRepository
{
    Task<CkModelId> FindModelIdAsync(CkModelId modelId);
    
    Task<CkModelRoot> GetModelAsync(CkModelId modelId);
    
    Task PublishModelAsync(CkModelRoot ckModel); 
    
    Task UpdateModelAsync(CkModelRoot ckModel);
}