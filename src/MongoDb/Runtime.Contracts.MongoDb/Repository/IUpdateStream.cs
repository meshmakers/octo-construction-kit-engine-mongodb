namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

/// <summary>
///     Access to event update stream
/// </summary>
/// <typeparam name="TDocument"></typeparam>
public interface IUpdateStream<out TDocument> : IDisposable
{
    IObservable<IUpdateInfo<TDocument>> GetUpdates();
}