namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
///     Access to update stream
/// </summary>
/// <typeparam name="TDocument">The type of the document.</typeparam>
public interface IUpdateStream<out TDocument> : IDisposable
{
    /// <summary>
    /// Returns the updates.
    /// </summary>
    /// <returns></returns>
    IObservable<IUpdateInfo<TDocument>> GetUpdates();
}