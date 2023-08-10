using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

public interface IDependencyResolver
{
    Task<CkAggregatedModelElements> ResolveDependenciesAsync(ICollection<CkModelId> dependencies, CompilerResult compilerResult);
}