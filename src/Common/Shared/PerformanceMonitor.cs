using System.Runtime.CompilerServices;

namespace Meshmakers.Octo.Common.Shared;

/// <summary>
///     Represents the performance monitor
/// </summary>
public class PerformanceMonitor : IDisposable
{
    private readonly PerfItem _item;
    private bool _isDisposed;

    /// <summary>
    ///     c'tor
    /// </summary>
    public PerformanceMonitor([CallerMemberName] string memberName = "")
    {
        _item = PerfManager.Instance.CreateMeasurement(memberName);
        _item.Start();
    }

    /// <summary>
    ///     Disposes the performance monitor
    /// </summary>
    public void Dispose()
    {
        Dispose(true);

        // Use SupressFinalize in case a subclass
        // of this type implements a finalizer.
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Destructor
    /// </summary>
    ~PerformanceMonitor()
    {
        Dispose(false);
    }

    protected void Dispose(bool disposing)
    {
        // If you need thread safety, use a lock around these 
        // operations, as well as in your methods that use the resource.
        if (!_isDisposed)
        {
            if (disposing) _item.Stop();

            // Indicate that the instance has been disposed.
            _isDisposed = true;
        }
    }

    /// <summary>
    ///     Sets a checkpoint
    /// </summary>
    /// <param name="description">Description of the checkpoint</param>
    public void SetCheckPoint(string description)
    {
        _item.SetCheckPoint(description);
    }
}