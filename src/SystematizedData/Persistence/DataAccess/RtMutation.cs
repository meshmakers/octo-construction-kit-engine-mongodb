
using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class RtMutation<TEntity> : Mutation<TEntity> where TEntity : RtEntity, new()
{
    private readonly IDatabaseCollection<TEntity> _databaseCollection;

    public RtMutation(IDatabaseCollection<TEntity> databaseCollection)
    {
        _databaseCollection = databaseCollection;
    }
    
    public async Task ExecuteDeleteOneAsync(IOctoSession session)
    {
        using var performanceMonitor = new PerformanceMonitor();
        
        // Filter for fields
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw OperationFailedException.NoFilterSet();
        }
        
        performanceMonitor.SetCheckPoint("definitions created");

        await _databaseCollection.DeleteOneAsync(session, filterDefinitions);
    }
    
    protected override string ResolveAttributeName(string attributeName)
    {
        var baseResolve = base.ResolveAttributeName(attributeName);
        if (!string.IsNullOrEmpty(baseResolve))
        {
            return baseResolve;
        }

        if (typeof(RtEntity).GetProperty(attributeName) != null)
        {
            return attributeName.ToCamelCase();
        }

        return $"{Constants.AttributesName}.{attributeName.ToCamelCase()}";
    }
}