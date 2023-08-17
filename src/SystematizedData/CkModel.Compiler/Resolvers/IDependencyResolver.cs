using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Resolvers;

public interface IDependencyResolver
{
    Task<CkAggregatedModelElements> ResolveDependenciesAsync(ICollection<CkModelId> dependencies, CompilerResult compilerResult);
}