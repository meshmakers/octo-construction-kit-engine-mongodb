using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

public interface IDependencyResolver
{
    Task<CkAggregatedModelElements> ResolveDependenciesAsync(ICollection<CkModelId> dependencies, CompilerResult compilerResult);
}