namespace Meshmakers.Octo.Common.DistributedCache.Channels;

/// <summary>
/// Interface of an operation
/// </summary>
/// <typeparam name="TResult">Type of operation results</typeparam>
/// <typeparam name="TError">Type of operation errors</typeparam>
public interface IOperationResultChannel<out TError, out TResult> : IDisposable
{
    /// <summary>
    ///     Adds a callback when operation is completed
    /// </summary>
    /// <param name="action">The action that is performed when operation is invoked.</param>
    void OnSuccess(Func<TResult, Task> action);

    /// <summary>
    /// Adds a callback when operation is in error
    /// </summary>
    /// <param name="action"></param>
    void OnError(Func<TError, Task> action);
    
    /// <summary>
    ///     Unsubscribe the channel
    /// </summary>
    /// <returns>Task</returns>
    Task UnsubscribeAsync();
}