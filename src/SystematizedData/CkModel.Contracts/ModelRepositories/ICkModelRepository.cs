using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.ModelRepositories;

public interface ICkModelRepository
{
    int Order { get; }
    string RepositoryName { get; }
    
    Task<bool> LookupModelIdAsync(CkModelId modelId);
    
    Task<CkCompiledModelRoot> GetModelAsync(CkModelId modelId);
    
    Task PublishModelAsync(CkCompiledModelRoot ckCompiledModel); 
    
    Task UpdateModelAsync(CkCompiledModelRoot ckCompiledModel);
}