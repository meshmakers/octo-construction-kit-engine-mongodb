namespace Meshmakers.Octo.Common.DistributedCache.Channels;

/// <summary>
/// Interface of an operation
/// </summary>
/// <typeparam name="TInvoke">Type of operation arguments</typeparam>
/// <typeparam name="TResult">Type of operation results</typeparam>
public interface IOperationInvokeChannel<out TInvoke, TResult> : IDisposable
{
    /// <summary>
    ///     Adds a callback when operation is invoked
    /// </summary>
    /// <param name="action">The action that is performed when operation is invoked.</param>
    void OnInvoked(Func<TInvoke, Task<TResult>> action);
    
    /// <summary>
    ///     Unsubscribe the channel
    /// </summary>
    /// <returns>Task</returns>
    Task UnsubscribeAsync();
}