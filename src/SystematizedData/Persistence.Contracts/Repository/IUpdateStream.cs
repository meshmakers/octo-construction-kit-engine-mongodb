using System;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

/// <summary>
///     Access to event update stream
/// </summary>
/// <typeparam name="TDocument"></typeparam>
public interface IUpdateStream<out TDocument> : IDisposable
{
    IObservable<IUpdateInfo<TDocument>> GetUpdates();
}
