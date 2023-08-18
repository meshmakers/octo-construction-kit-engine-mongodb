using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Resolvers;

public interface IDependencyResolver
{
    Task<CkAggregatedModelElements> ResolveDependenciesAsync(ICollection<CkModelId> dependencies, CompilerResult compilerResult);
}