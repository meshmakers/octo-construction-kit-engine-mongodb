using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Resolvers;

public interface IElementResolver
{
    CkModelGraph Resolve(CkModelRoot ckModelRoot, CompilerResult validationResult);
}