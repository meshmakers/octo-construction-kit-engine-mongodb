using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Resolvers;

public interface IInheritanceResolver
{
    CkModelGraph Resolve(CkAggregatedModelElements aggregatedModelElements, CkModelGraph modelGraph, OperationResult operationResult);
}