using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepositories;

public interface ICkModelRepository
{
    Task<CkModelId> FindModelIdAsync(CkModelId modelId);
    
    Task<CkModelRoot> GetModelAsync(CkModelId modelId);
    
    Task PublishModelAsync(CkModelRoot ckModel); 
    
    Task UpdateModelAsync(CkModelRoot ckModel);
}