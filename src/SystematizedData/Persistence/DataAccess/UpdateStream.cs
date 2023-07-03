using System;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

/// <inheritdoc />
internal class UpdateStream<TDocument> : IUpdateStream<TDocument>
    where TDocument : class, new()
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ReplaySubject<IUpdateInfo<TDocument>> _messageStream = new(1);

    public IObservable<IUpdateInfo<TDocument>> GetUpdates()
    {
        return _messageStream;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
    }

    public void Watch(IMongoCollection<TDocument> documentCollection,
        PipelineDefinition<ChangeStreamDocument<TDocument>, ChangeStreamDocument<TDocument>> pipelineDefinition,
        CancellationToken requestCancellationToken = default)
    {
        Task.Run(async () =>
        {
            using (var cursor = await documentCollection.WatchAsync(pipelineDefinition,
                       new ChangeStreamOptions
                       {
                           FullDocument = ChangeStreamFullDocumentOption.UpdateLookup, 
                           FullDocumentBeforeChange = ChangeStreamFullDocumentBeforeChangeOption.WhenAvailable
                       },
                       requestCancellationToken))
            {
                await cursor.ForEachAsync(change =>
                {
                    _messageStream.OnNext(new UpdateInfo<TDocument>(change));
                    if (!_messageStream.HasObservers)
                    {
                        _cancellationTokenSource.Cancel();
                    }
                }, _cancellationTokenSource.Token);
            }
        }, _cancellationTokenSource.Token);
    }
}
