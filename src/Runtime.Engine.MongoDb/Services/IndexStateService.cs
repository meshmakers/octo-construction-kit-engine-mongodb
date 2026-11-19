using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

/// <summary>
/// Service responsible for tracking index creation operations and managing index states on CkTypes
/// </summary>
internal class IndexStateService(IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> ckTypes, ILogger logger)
{
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

        if (state == IndexState.Failed)
        {
            logger.LogWarning(
                "Index '{IndexName}' failed for type '{TypeId}' on collection '{CollectionName}': {ErrorMessage}",
                indexName, typeId, collectionName, errorMessage);
        }

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
            logger.LogWarning("BulkUpdateIndexStatesAsync: No active tracking context, skipping index state update");
            return;
        }

        var indexStatesByType = _currentTracker.GetIndexStatesByType();
        var removedByType = _currentTracker.GetRemovedStatesByType();

        if (!indexStatesByType.Any() && !removedByType.Any())
        {
            return;
        }

        var typesToUpdate = new List<CkType>();

        foreach (var typeId in indexStatesByType.Keys.Union(removedByType.Keys))
        {
            var newStates = indexStatesByType.GetValueOrDefault(typeId) ?? [];
            var removedStateKeys = removedByType.GetValueOrDefault(typeId) ?? [];
            try
            {
                var ckType = await ckTypes.DocumentAsync(session, typeId);
                if (ckType != null)
                {
                    // Get existing states
                    var existingStates = ckType.IndexStates?.ToList() ?? new List<CkIndexState>();
                    var hadStates = ckType.IndexStates != null;

                    // Remove the states that will be replaced by new states and the ones removed in this run (an index
                    // replaced, dropped or no longer serving the type, AB#9342) - match by name + collection
                    var newStateKeys = newStates.Select(s => (s.Name, s.CollectionName)).ToHashSet();
                    var removedCount = existingStates.RemoveAll(existing =>
                        newStateKeys.Contains((existing.Name, existing.CollectionName)) ||
                        removedStateKeys.Contains((existing.Name, existing.CollectionName)));
                    if (newStates.Count == 0 && removedCount == 0)
                    {
                        // Nothing removed in this run is stored on the type: no write.
                        continue;
                    }

                    // Add new states
                    existingStates.AddRange(newStates);

                    // Set states, preserving null if it was null and the list is now empty
                    ckType.IndexStates = existingStates.Any() || hadStates ? existingStates : null;

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

        // AB#9342: the states removed in this run, so that a stored state (e.g. Failed from an earlier run) is removed
        // from the type too, not only one tracked in this run.
        private readonly Dictionary<CkId<CkTypeId>, HashSet<(string Name, string CollectionName)>> _removedByType = new();

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

            if (_removedByType.TryGetValue(typeId, out var removed))
            {
                removed.Remove((state.Name, state.CollectionName));
            }
        }

        /// <summary>
        /// Removes index states for a specific index name and collection: the state tracked in this run and the one
        /// stored on the type (removed by <see cref="IndexStateService.BulkUpdateIndexStatesAsync" />, AB#9342).
        /// An index this run recorded as <see cref="IndexState.Applied" /> for the type is kept: it serves one of the
        /// type's index definitions, whatever another definition processed later expected under that name (e.g. two
        /// definitions of one type whose existing indexes hold each other's keys).
        /// </summary>
        public void RemoveIndexState(CkId<CkTypeId> typeId, string indexName, string collectionName)
        {
            if (_indexStatesByType.TryGetValue(typeId, out var states))
            {
                var key = $"{indexName}_{collectionName}";
                if (states.TryGetValue(key, out var tracked) && tracked.State == IndexState.Applied)
                {
                    return;
                }

                states.Remove(key);
            }

            if (!_removedByType.TryGetValue(typeId, out var removed))
            {
                removed = [];
                _removedByType[typeId] = removed;
            }

            removed.Add((indexName, collectionName));
        }

        /// <summary>
        /// Gets the states removed in this run, grouped by CkType
        /// </summary>
        public Dictionary<CkId<CkTypeId>, HashSet<(string Name, string CollectionName)>> GetRemovedStatesByType() =>
            _removedByType.Where(kvp => kvp.Value.Count > 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

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
