using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepositories;

public interface ICkModelRepository
{
    Task<CkModelId> FindModelIdAsync(CkModelId modelId);
    
    Task<CkCompiledModelRoot> GetModelAsync(CkModelId modelId);
    
    Task PublishModelAsync(CkCompiledModelRoot ckCompiledModel); 
    
    Task UpdateModelAsync(CkCompiledModelRoot ckCompiledModel);
}