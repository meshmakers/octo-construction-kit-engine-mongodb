using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

/// <summary>
/// Service responsible for tracking index creation operations and managing index states on CkTypes
/// </summary>
internal class IndexStateService(IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> ckTypes, ILogger logger) {
    private IndexOperationTracker? _currentTracker;

    public void BeginTracking()
    {
        _currentTracker = new IndexOperationTracker();
    }

    public void EndTracking()
    {
        _currentTracker = null;
    }

    public void TrackIndexOperation(CkId<CkTypeId> typeId, string indexName, string collectionName,
        IndexState state, string? errorMessage = null)
    {
        if (_currentTracker == null)
        {
            logger.LogWarning("TrackIndexOperation called without active tracking context");
            return;
        }

        var indexState = new CkIndexState
        {
            Name = indexName,
            CollectionName = collectionName,
            State = state,
            ErrorMessage = errorMessage,
            AppliedAt = DateTime.UtcNow
        };

        _currentTracker.AddIndexState(typeId, indexState);
    }

    public void RemoveIndexState(CkId<CkTypeId> typeId, string indexName, string collectionName)
    {
        if (_currentTracker == null)
        {
            logger.LogWarning("RemoveIndexState called without active tracking context");
            return;
        }

        _currentTracker.RemoveIndexState(typeId, indexName, collectionName);
    }

    public async Task BulkUpdateIndexStatesAsync(IOctoSession session)
    {
        if (_currentTracker == null)
        {
            logger.LogDebug("No active tracking context, skipping index state update");
            return;
        }

        var indexStatesByType = _currentTracker.GetIndexStatesByType();
        if (!indexStatesByType.Any())
        {
            logger.LogDebug("No index states to update");
            return;
        }

        var typesToUpdate = new List<CkType>();

        foreach (var (typeId, indexStates) in indexStatesByType)
        {
            try
            {
                var ckType = await ckTypes.DocumentAsync(session, typeId);
                if (ckType != null)
                {
                    // Merge with existing index states for other collections
                    var existingStates = ckType.IndexStates?.ToList() ?? new List<CkIndexState>();

                    // Remove existing states for the same collection to avoid duplicates
                    var collectionsInNewStates = indexStates.Select(s => s.CollectionName).Distinct().ToHashSet();
                    existingStates.RemoveAll(existing => collectionsInNewStates.Contains(existing.CollectionName));

                    // Add the new states
                    existingStates.AddRange(indexStates);

                    ckType.IndexStates = existingStates;
                    typesToUpdate.Add(ckType);
                }
                else
                {
                    logger.LogWarning("CkType '{CkTypeId}' not found when updating index states", typeId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load CkType '{CkTypeId}' for index state update", typeId);
            }
        }

        // Perform bulk update if we have types to update
        if (typesToUpdate.Any())
        {
            try
            {
                await ckTypes.ReplaceManyAsync(session, typesToUpdate);
                logger.LogInformation("Successfully updated index states for {Count} CkTypes", typesToUpdate.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update index states for CkTypes");
                throw;
            }
        }
    }

    public async Task ClearIndexStatesForCollectionAsync(IOctoSession session, string collectionName,
        IEnumerable<CkId<CkTypeId>> affectedTypes)
    {
        var typesToUpdate = new List<CkType>();

        foreach (var typeId in affectedTypes)
        {
            try
            {
                var ckType = await ckTypes.DocumentAsync(session, typeId);
                if (ckType?.IndexStates != null)
                {
                    // Remove states for this specific collection
                    var originalCount = ckType.IndexStates.Count;
                    ckType.IndexStates = ckType.IndexStates
                        .Where(state =>
                            !string.Equals(state.CollectionName, collectionName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (ckType.IndexStates.Count < originalCount)
                    {
                        typesToUpdate.Add(ckType);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to clear index states for CkType '{CkTypeId}' in collection '{CollectionName}'",
                    typeId, collectionName);
            }
        }

        if (typesToUpdate.Any())
        {
            try
            {
                await ckTypes.ReplaceManyAsync(session, typesToUpdate);
                logger.LogInformation("Cleared index states for collection '{CollectionName}' in {Count} CkTypes",
                    collectionName, typesToUpdate.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear index states for collection '{CollectionName}'", collectionName);
                throw;
            }
        }
    }

    /// <summary>
    /// Internal tracker for index operations during the update process
    /// </summary>
    private class IndexOperationTracker
    {
        private readonly Dictionary<CkId<CkTypeId>, Dictionary<string, CkIndexState>> _indexStatesByType = new();

        /// <summary>
        /// Adds or updates an index state for a specific CkType
        /// </summary>
        public void AddIndexState(CkId<CkTypeId> typeId, CkIndexState state)
        {
            if (!_indexStatesByType.TryGetValue(typeId, out var states))
            {
                states = new Dictionary<string, CkIndexState>();
                _indexStatesByType[typeId] = states;
            }

            // Use composite key of index name and collection name for uniqueness
            var key = $"{state.Name}_{state.CollectionName}";
            states[key] = state;
        }

        /// <summary>
        /// Removes index states for a specific index name and collection
        /// </summary>
        public void RemoveIndexState(CkId<CkTypeId> typeId, string indexName, string collectionName)
        {
            if (_indexStatesByType.TryGetValue(typeId, out var states))
            {
                var key = $"{indexName}_{collectionName}";
                states.Remove(key);
            }
        }

        /// <summary>
        /// Gets all tracked index states grouped by CkType
        /// </summary>
        public Dictionary<CkId<CkTypeId>, List<CkIndexState>> GetIndexStatesByType()
        {
            return _indexStatesByType.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Values.ToList()
            );
        }
    }
}
