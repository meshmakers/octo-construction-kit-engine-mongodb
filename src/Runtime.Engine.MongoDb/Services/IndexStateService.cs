using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

/// <summary>
/// Tracks the index operations of one collection run of the index update and keeps the index states of the CK types
/// stored in that collection (<see cref="CkType.IndexStates" />) in line with the indexes that exist (AB#9391):
/// <list type="bullet">
///     <item>An <see cref="IndexState.Applied" /> state names an index that exists. At the end of the collection run
///     (<see cref="Reconcile" />) every <c>Applied</c> state of a type of the collection - stored or tracked in the run -
///     whose index is not in the final index list is removed, whatever dropped the index (the cleanup, an earlier run
///     that was aborted before it persisted its states).</item>
///     <item>A <see cref="IndexState.Failed" /> state names an index a definition of the type wants and that is not in
///     its target shape. It stays until an index in the target shape serves the definition (a later successful build or
///     upgrade, an existing index with the definition, or an index shared with another type), also through runs that
///     keep a legacy index without rebuilding it. After a completed run a <c>Failed</c> state under a name no definition
///     of the type expected in that run is removed (the definition is gone).</item>
///     <item>A <c>Failed</c> state never replaces an <c>Applied</c> state tracked in the same run for the same index
///     name. AB#9302: a build failure is recorded with <see cref="TrackFailure" /> and gets its name in
///     <see cref="ResolveFailures" />, after every definition of the collection was processed: the expected name, or
///     the alternate name when an <c>Applied</c> state is tracked under the expected name by then.</item>
///     <item>A run that changes nothing writes nothing: the merged states are compared with the stored ones by name,
///     collection, state and error message (not by <see cref="CkIndexState.AppliedAt" />); a state that is unchanged
///     keeps its stored <c>AppliedAt</c>. A change writes only the <c>IndexStates</c> field of the CkType.</item>
/// </list>
/// </summary>
internal partial class IndexStateService(IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> ckTypes, ILogger logger)
{
    private static readonly IndexState[] AllStates = [IndexState.Applied, IndexState.Failed];

    private IndexOperationTracker? _currentTracker;

    /// <summary>
    /// Starts tracking the collection run of <paramref name="collectionName" />; <paramref name="collectionTypeIds" />
    /// are the types stored in the collection (base chain, root, descendants), whose states <see cref="Reconcile" />
    /// brings in line with the indexes of the collection.
    /// </summary>
    public void BeginTracking(string collectionName, IEnumerable<CkId<CkTypeId>> collectionTypeIds)
    {
        _currentTracker = new IndexOperationTracker(collectionName, collectionTypeIds);
    }

    public void EndTracking()
    {
        _currentTracker = null;
    }

    /// <summary>AB#9302: true while a collection run is tracked (between <see cref="BeginTracking" /> and
    /// <see cref="EndTracking" />).</summary>
    public bool IsTracking => _currentTracker != null;

    /// <summary>
    /// AB#9302: records the failed build of the index a definition of the type expects under
    /// <paramref name="indexName" />. The state is named in <see cref="ResolveFailures" />: under
    /// <paramref name="indexName" />, or under <paramref name="alternateName" /> when by then the run tracks an
    /// <see cref="IndexState.Applied" /> state under <paramref name="indexName" /> for the type (the index there serves the
    /// type: a legacy index the failed upgrade keeps, or another definition, also one processed after this one).
    /// </summary>
    public void TrackFailure(CkId<CkTypeId> typeId, string indexName, string alternateName, string collectionName,
        string errorMessage)
    {
        if (_currentTracker == null)
        {
            logger.LogWarning("TrackFailure called without active tracking context");
            return;
        }

        _currentTracker.Failures.Add((typeId, indexName, alternateName, collectionName, errorMessage));
    }

    /// <summary>
    /// AB#9302: names and records the failures of <see cref="TrackFailure" />; called once per collection run, after every
    /// definition was processed and before <see cref="Reconcile" />.
    /// </summary>
    public void ResolveFailures()
    {
        if (_currentTracker == null)
        {
            return;
        }

        var failures = _currentTracker.Failures.ToList();
        _currentTracker.Failures.Clear();
        foreach (var (typeId, indexName, alternateName, collectionName, errorMessage) in failures)
        {
            var name = _currentTracker.IsTrackedApplied(typeId, indexName, collectionName) ? alternateName : indexName;
            TrackIndexOperation(typeId, name, collectionName, IndexState.Failed, errorMessage);
        }
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

        if (!_currentTracker.AddIndexState(typeId, indexState))
        {
            logger.LogWarning(
                "Index '{IndexName}' of type '{TypeId}' on collection '{CollectionName}' exists and serves another " +
                "definition of the type; the failure is not recorded under its name: {ErrorMessage}",
                indexName, typeId, collectionName, errorMessage);
        }
    }

    /// <summary>
    /// True when this run recorded <see cref="IndexState.Applied" /> for <paramref name="indexName" /> on the type.
    /// </summary>
    public bool IsTrackedApplied(CkId<CkTypeId> typeId, string indexName, string collectionName) =>
        _currentTracker?.IsTrackedApplied(typeId, indexName, collectionName) == true;

    /// <summary>
    /// Records that a definition of the type expects <paramref name="indexName" /> in this run: a stored
    /// <see cref="IndexState.Failed" /> state under it survives a completed run (<see cref="Reconcile" />).
    /// </summary>
    public void ExpectIndex(CkId<CkTypeId> typeId, string indexName, string collectionName) =>
        _currentTracker?.ExpectIndex(typeId, indexName, collectionName);

    /// <summary>
    /// Removes the states of <paramref name="indexName" /> on the type (default: both states) - the state tracked in
    /// this run and the one stored on the type. An <see cref="IndexState.Applied" /> state this run recorded for the
    /// type is kept, and with it the index name's other states.
    /// </summary>
    public void RemoveIndexState(CkId<CkTypeId> typeId, string indexName, string collectionName,
        params IndexState[] states)
    {
        if (_currentTracker == null)
        {
            logger.LogWarning("RemoveIndexState called without active tracking context");
            return;
        }

        _currentTracker.RemoveIndexState(typeId, indexName, collectionName, states.Length == 0 ? AllStates : states);
    }

    /// <summary>
    /// AB#9391: the type an index was built for, for the log of an index another type shares - the same in every order
    /// of the types: the type of the collection whose slot name (<c>&lt;collection name of the type&gt;_&lt;n&gt;</c>) the
    /// index carries; else, among the types of the collection with a stored <see cref="IndexState.Applied" /> state
    /// under <paramref name="indexName" />, the only one; null when neither identifies it. The stored states are read
    /// once per collection run, only when the name does not identify the type.
    /// </summary>
    public async Task<CkId<CkTypeId>?> FindStoredHolderAsync(IOctoSession session, string indexName,
        string collectionName)
    {
        if (_currentTracker == null)
        {
            return null;
        }

        // The engine names a type's index after the type's slot: that type was it built for, whichever type of the
        // collection processes the index first (no read).
        var bySlotName = _currentTracker.CollectionTypeIds.Where(t => IsSlotNameOf(indexName, t)).ToList();
        if (bySlotName.Count == 1)
        {
            return bySlotName[0];
        }

        _currentTracker.StoredTypes ??= (await LoadTypesAsync(session, _currentTracker.CollectionTypeIds)).ToList();
        var candidates = _currentTracker.StoredTypes
            .Where(t => t.IndexStates?.Any(s => s.State == IndexState.Applied &&
                                               string.Equals(s.Name, indexName, StringComparison.Ordinal) &&
                                               SameCollection(s.CollectionName, collectionName)) == true)
            .ToList();
        return candidates.Count == 1 ? candidates[0].CkTypeId : null;
    }

    /// <summary>
    /// AB#9391: brings the states of the collection's types in line with <paramref name="existingIndexNames" />, the
    /// final index list of the collection, when <see cref="BulkUpdateIndexStatesAsync" /> persists them: every
    /// <see cref="IndexState.Applied" /> state under a name that is not in it is removed (stored and tracked). With
    /// <paramref name="runCompleted" /> (every definition of the collection was processed) a stored
    /// <see cref="IndexState.Failed" /> state under a name no definition of the type expected in this run is removed
    /// too. After an aborted run no <c>Failed</c> state is removed for that reason (the definitions not processed yet
    /// expected nothing); the changes the run made before the abort - a failure recorded, a failure removed because its
    /// definition is served in its target shape - are persisted.
    /// </summary>
    public void Reconcile(IEnumerable<string> existingIndexNames, bool runCompleted) =>
        _currentTracker?.Reconcile(existingIndexNames, runCompleted);

    /// <summary>
    ///     Persists the tracked index states of the current collection (only the <c>IndexStates</c> field of each type).
    ///     AB#9302: <paramref name="lockLostToken" /> is the lost-lock token of the index update; it is checked after the
    ///     read of the stored states and before each write, so a loss noticed meanwhile persists nothing more. A token
    ///     check cannot fence a write already started; the next index update reconciles the states.
    /// </summary>
    /// <returns>False when the lost lock stopped the update before a write, otherwise true.</returns>
    public async Task<bool> BulkUpdateIndexStatesAsync(IOctoSession session, CancellationToken lockLostToken = default)
    {
        if (_currentTracker == null)
        {
            logger.LogWarning("BulkUpdateIndexStatesAsync: No active tracking context, skipping index state update");
            return true;
        }

        var typeIds = _currentTracker.AffectedTypeIds();
        if (typeIds.Count == 0)
        {
            return true;
        }

        IReadOnlyCollection<CkType> types;
        try
        {
            // Read just before the write: the merge starts from the stored states; only IndexStates is written.
            types = await LoadTypesAsync(session, typeIds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the CkTypes of collection '{CollectionName}' for the index state update",
                _currentTracker.CollectionName);
            return true;
        }

        if (lockLostToken.IsCancellationRequested)
        {
            return false;
        }

        foreach (var missing in typeIds.Where(id => types.All(t => t.CkTypeId != id) &&
                                                    _currentTracker.HasTrackedChanges(id)))
        {
            logger.LogWarning("CkType '{CkTypeId}' not found when updating index states", missing);
        }

        var typesToUpdate = new List<CkType>();
        foreach (var ckType in types)
        {
            var stored = ckType.IndexStates ?? [];
            var merged = _currentTracker.Merge(ckType.CkTypeId, stored);
            if (SameStates(stored, merged))
            {
                continue;
            }

            // Set states, preserving null if it was null and the list is now empty
            ckType.IndexStates = merged.Count > 0 || ckType.IndexStates != null ? merged : null;
            typesToUpdate.Add(ckType);
        }

        if (typesToUpdate.Count > 0)
        {
            try
            {
                // AB#9391: only the IndexStates field is written. Replacing the whole document would revert a CkType
                // change (e.g. of a model import, which does not take the index update lock) committed between the
                // read above and this write.
                foreach (var ckType in typesToUpdate)
                {
                    if (lockLostToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    await ckTypes.UpdateOneAsync(session, ckType.CkTypeId,
                        Builders<CkType>.Update.Set(t => t.IndexStates, ckType.IndexStates));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update index states for CkTypes");
                throw;
            }
        }

        return true;
    }

    public async Task ClearIndexStatesForCollectionAsync(IOctoSession session, string collectionName,
        IEnumerable<CkId<CkTypeId>> affectedTypes)
    {
        var typesToUpdate = new List<CkType>();

        foreach (var typeId in affectedTypes.Distinct())
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
                // AB#9302 (as AB#9391 for BulkUpdateIndexStatesAsync): only the IndexStates field is written. Replacing
                // the whole document would revert a CkType change committed between the read above and this write.
                foreach (var ckType in typesToUpdate)
                {
                    await ckTypes.UpdateOneAsync(session, ckType.CkTypeId,
                        Builders<CkType>.Update.Set(t => t.IndexStates, ckType.IndexStates));
                }

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

    private async Task<IReadOnlyCollection<CkType>> LoadTypesAsync(IOctoSession session,
        IReadOnlyCollection<CkId<CkTypeId>> typeIds)
    {
        if (typeIds.Count == 0)
        {
            return [];
        }

        var ids = typeIds.ToList();
        return (await ckTypes.FindManyAsync(session, t => ids.Contains(t.CkTypeId))).ToList();
    }

    /// <summary>
    /// The engine names a CK index of a type <c>&lt;collection name of the type&gt;_&lt;n&gt;</c>, in the tiebreak shape
    /// with the suffix <c>_id</c> (<see cref="TiebreakIndexShape.NameSuffix" />), and either name with an alternate-name
    /// hash (<see cref="TiebreakIndexShape.AlternateName" />). AB#9302: nothing else - a hand-made name such as
    /// <c>&lt;type&gt;_0_manual</c> is not a slot name.
    /// </summary>
    internal static bool IsSlotNameOf(string indexName, CkId<CkTypeId> typeId)
    {
        var prefix = typeId.ToRtCkId().GetCkTypeCollectionName() + "_";
        return indexName.StartsWith(prefix, StringComparison.Ordinal) &&
               SlotSuffix().IsMatch(indexName[prefix.Length..]);
    }

    [GeneratedRegex("^[0-9]+(?:_id)?(?:_[0-9a-f]{6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlotSuffix();

    private static bool SameCollection(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Same states by name, collection, state and error message, in any order; <c>AppliedAt</c> ignored.</summary>
    private static bool SameStates(IReadOnlyCollection<CkIndexState> stored, IReadOnlyCollection<CkIndexState> merged)
    {
        if (stored.Count != merged.Count)
        {
            return false;
        }

        var remaining = stored.Select(StateKey).ToList();
        foreach (var key in merged.Select(StateKey))
        {
            if (!remaining.Remove(key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Index name and collection; the collection compared without case, as everywhere in this service.</summary>
    private static (string Name, string Collection) Key(string indexName, string collectionName) =>
        (indexName, collectionName.ToLowerInvariant());

    private static (string Name, string Collection, IndexState State, string? Error) StateKey(CkIndexState s) =>
        (s.Name, s.CollectionName.ToLowerInvariant(), s.State, s.ErrorMessage);

    /// <summary>
    /// Internal tracker for the index operations of one collection run
    /// </summary>
    private class IndexOperationTracker(string collectionName, IEnumerable<CkId<CkTypeId>> collectionTypeIds)
    {
        private readonly Dictionary<CkId<CkTypeId>, Dictionary<(string Name, string Collection), CkIndexState>>
            _indexStatesByType = new();

        // AB#9342: the states removed in this run, so that a stored state (e.g. Failed from an earlier run) is removed
        // from the type too, not only one tracked in this run. AB#9391: by state, so that a caller can remove only the
        // Applied state of a name and keep a failure visible.
        private readonly Dictionary<CkId<CkTypeId>, HashSet<(string Name, string Collection, IndexState State)>>
            _removedByType = new();

        // AB#9391: the index names the definitions of each type expected in this run.
        private readonly Dictionary<CkId<CkTypeId>, HashSet<(string Name, string Collection)>> _expectedByType = new();

        private HashSet<string>? _existingIndexNames;
        private bool _runCompleted;

        /// <summary>AB#9302: the failed builds of the run, named in <see cref="IndexStateService.ResolveFailures" />.</summary>
        public List<(CkId<CkTypeId> TypeId, string IndexName, string AlternateName, string CollectionName, string Error)>
            Failures { get; } = [];

        public string CollectionName { get; } = collectionName;

        public IReadOnlyCollection<CkId<CkTypeId>> CollectionTypeIds { get; } = collectionTypeIds.Distinct().ToList();

        /// <summary>The stored types of the collection, read once on demand (logging only).</summary>
        public List<CkType>? StoredTypes { get; set; }

        /// <summary>
        /// Adds or updates an index state for a specific CkType. A <see cref="IndexState.Failed" /> state never replaces
        /// an <see cref="IndexState.Applied" /> state tracked in this run (returns false).
        /// </summary>
        public bool AddIndexState(CkId<CkTypeId> typeId, CkIndexState state)
        {
            if (!_indexStatesByType.TryGetValue(typeId, out var states))
            {
                states = new Dictionary<(string, string), CkIndexState>();
                _indexStatesByType[typeId] = states;
            }

            // Use composite key of index name and collection name for uniqueness
            var key = Key(state.Name, state.CollectionName);
            if (state.State == IndexState.Failed && states.TryGetValue(key, out var tracked) &&
                tracked.State == IndexState.Applied)
            {
                return false;
            }

            states[key] = state;

            if (_removedByType.TryGetValue(typeId, out var removed))
            {
                removed.RemoveWhere(r => (r.Name, r.Collection) == key);
            }

            return true;
        }

        public bool IsTrackedApplied(CkId<CkTypeId> typeId, string indexName, string collection) =>
            _indexStatesByType.TryGetValue(typeId, out var states) &&
            states.TryGetValue(Key(indexName, collection), out var tracked) && tracked.State == IndexState.Applied;

        public void ExpectIndex(CkId<CkTypeId> typeId, string indexName, string collection)
        {
            if (!_expectedByType.TryGetValue(typeId, out var expected))
            {
                expected = [];
                _expectedByType[typeId] = expected;
            }

            expected.Add(Key(indexName, collection));
        }

        /// <summary>
        /// Removes the given states of an index name and collection: the state tracked in this run and the one stored on
        /// the type (removed by <see cref="IndexStateService.BulkUpdateIndexStatesAsync" />, AB#9342). An index this run
        /// recorded as <see cref="IndexState.Applied" /> for the type is kept: it serves one of the type's index
        /// definitions, whatever another definition processed later expected under that name (e.g. two definitions of
        /// one type whose existing indexes hold each other's keys).
        /// </summary>
        public void RemoveIndexState(CkId<CkTypeId> typeId, string indexName, string collection,
            IReadOnlyCollection<IndexState> states)
        {
            var key = Key(indexName, collection);
            if (_indexStatesByType.TryGetValue(typeId, out var tracked) && tracked.TryGetValue(key, out var state))
            {
                if (state.State == IndexState.Applied)
                {
                    return;
                }

                if (states.Contains(state.State))
                {
                    tracked.Remove(key);
                }
            }

            if (!_removedByType.TryGetValue(typeId, out var removed))
            {
                removed = [];
                _removedByType[typeId] = removed;
            }

            foreach (var removedState in states)
            {
                removed.Add((key.Name, key.Collection, removedState));
            }
        }

        public void Reconcile(IEnumerable<string> existingIndexNames, bool runCompleted)
        {
            _existingIndexNames = existingIndexNames.ToHashSet(StringComparer.Ordinal);
            _runCompleted = runCompleted;
        }

        public bool HasTrackedChanges(CkId<CkTypeId> typeId) =>
            _indexStatesByType.ContainsKey(typeId) || _removedByType.ContainsKey(typeId);

        /// <summary>
        /// The types whose states this run may change: those with tracked or removed states and, when the run is
        /// reconciled, every type of the collection.
        /// </summary>
        public IReadOnlyCollection<CkId<CkTypeId>> AffectedTypeIds()
        {
            var ids = _indexStatesByType.Where(kvp => kvp.Value.Count > 0).Select(kvp => kvp.Key)
                .Concat(_removedByType.Where(kvp => kvp.Value.Count > 0).Select(kvp => kvp.Key));
            if (_existingIndexNames != null)
            {
                ids = ids.Concat(CollectionTypeIds);
            }

            return ids.Distinct().ToList();
        }

        /// <summary>The stored states of the type with the changes of this run applied.</summary>
        public List<CkIndexState> Merge(CkId<CkTypeId> typeId, IReadOnlyCollection<CkIndexState> stored)
        {
            var newStates = _indexStatesByType.GetValueOrDefault(typeId)?.Values.ToList() ?? [];
            var removed = _removedByType.GetValueOrDefault(typeId) ?? [];
            var expected = _expectedByType.GetValueOrDefault(typeId) ?? [];
            var reconcile = _existingIndexNames != null && CollectionTypeIds.Contains(typeId);

            bool InThisCollection(CkIndexState s) => SameCollection(s.CollectionName, CollectionName);
            bool Exists(CkIndexState s) => _existingIndexNames!.Contains(s.Name);

            // A state of this run that names an index the collection no longer holds is not stored (reconcile).
            newStates = newStates
                .Where(s => !reconcile || !InThisCollection(s) || s.State != IndexState.Applied || Exists(s))
                .ToList();

            // Replace the stored states of the names tracked in this run, remove the ones removed in this run and, when
            // reconciled, the Applied states of indexes that do not exist and (after a completed run) the Failed states
            // of names no definition expected.
            var newStateKeys = newStates.Select(s => Key(s.Name, s.CollectionName)).ToHashSet();
            var merged = stored.Where(existing =>
                    !newStateKeys.Contains(Key(existing.Name, existing.CollectionName)) &&
                    !removed.Contains((existing.Name, existing.CollectionName.ToLowerInvariant(), existing.State)) &&
                    !(reconcile && InThisCollection(existing) &&
                      (existing.State == IndexState.Applied
                          ? !Exists(existing)
                          : _runCompleted && !expected.Contains(Key(existing.Name, existing.CollectionName)))))
                .ToList();

            // An unchanged state keeps its stored AppliedAt.
            foreach (var state in newStates)
            {
                var same = stored.FirstOrDefault(s => StateKey(s) == StateKey(state));
                merged.Add(same ?? state);
            }

            return merged;
        }
    }
}
