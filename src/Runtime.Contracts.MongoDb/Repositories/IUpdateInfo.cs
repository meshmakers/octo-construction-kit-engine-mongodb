using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

public interface IUpdateInfo<out T>
{
    string[] UpdateFields { get; }
    UpdateTypes UpdateType { get; }
    T? Document { get; }
    T? DocumentBeforeChange { get; }

    /// <summary>
    ///     The runtime id of the changed document, from the change event's document key. Set for every event of a
    ///     runtime-entity stream, and the only identification of a delete event without a pre-image
    ///     (<see cref="WatchStreamFilter.IncludeDeleteEvents" />). <c>null</c> for implementations that do not know it.
    /// </summary>
    OctoObjectId? DocumentRtId => null;
}