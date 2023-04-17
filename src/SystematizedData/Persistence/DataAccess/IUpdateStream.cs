using System;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

/// <summary>
///     Access to event update stream
/// </summary>
/// <typeparam name="TDocument"></typeparam>
public interface IUpdateStream<TDocument> : IDisposable where TDocument : class, new()
{
    IObservable<UpdateInfo<TDocument>> GetUpdates();
}
