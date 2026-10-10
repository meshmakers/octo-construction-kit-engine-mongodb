using System.Diagnostics;

using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

[DebuggerDisplay("{" + nameof(ApplicationName) + "}")]
internal abstract class OctoSession : IOctoSessionInternal
{
    private readonly ILogger<OctoSession> _logger;
    private bool _isSessionActive;
    private bool _isSessionStarted;
    private bool _isDisposed;
    private List<Func<Task>> _afterCommit = new();
    private List<Func<Task>> _afterRollback = new();

    internal OctoSession(ILogger<OctoSession> logger, IClientSessionHandle clientSessionHandle, string applicationName,
        RtSecurityContext? securityContext = null)
    {
        _logger = logger;
        _logger.LogDebug("[{ApplicationName}] Create session", applicationName);
        _isSessionActive = false;
        _isSessionStarted = false;
        _isDisposed = false;
        SessionHandle = clientSessionHandle;
        ApplicationName = applicationName;
        SecurityContext = securityContext ?? RtSecurityContext.System;
    }

    public string ApplicationName { get; set; }

    public RtSecurityContext SecurityContext { get; }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            if (_isSessionActive)
            {
                try
                {
                    SessionHandle.AbortTransaction();
                    _isSessionActive = false;
                    RunCallbacksAsync(TakeCallbacks(_afterRollback), "rollback").GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{ApplicationName}] Failed to abort transaction during session disposal", ApplicationName);
                }
            }

            SessionHandle.Dispose();
            _isDisposed = true;
        }
    }

    public void StartTransaction()
    {
        _logger.LogDebug("[{ApplicationName}] Starting transaction", ApplicationName);

        if (_isDisposed)
        {
            throw SessionOperationException.SessionDisposed();
        }

        if (_isSessionStarted)
        {
            throw SessionOperationException.SessionAlreadyStarted();
        }

        SessionHandle.StartTransaction();
        _logger.LogDebug("[{ApplicationName}, txnNumber {Id}] Transaction started", ApplicationName,
            SessionHandle.WrappedCoreSession.CurrentTransaction.TransactionNumber);
        _isSessionStarted = true;
        _isSessionActive = true;
    }

    public bool IsTransactionActive => _isSessionActive;

    public void RegisterTransactionCallbacks(Func<Task>? afterCommit, Func<Task>? afterRollback = null)
    {
        if (!_isSessionActive)
        {
            throw SessionOperationException.SessionNotActive();
        }

        if (afterCommit != null)
        {
            _afterCommit.Add(afterCommit);
        }

        if (afterRollback != null)
        {
            _afterRollback.Add(afterRollback);
        }
    }

    private static List<Func<Task>> TakeCallbacks(List<Func<Task>> source)
    {
        return source.ToList();
    }

    private async Task RunCallbacksAsync(IReadOnlyList<Func<Task>> callbacks, string phase)
    {
        _afterCommit = new List<Func<Task>>();
        _afterRollback = new List<Func<Task>>();
        foreach (var callback in callbacks)
        {
            try
            {
                await callback().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The database state is already final. A failing side effect (e.g. GridFS cleanup) must not
                // turn a committed transaction into an error; leftover bytes are orphans, not data loss.
                _logger.LogWarning(ex, "[{ApplicationName}] After-{Phase} action failed", ApplicationName, phase);
            }
        }
    }

    public async Task CommitTransactionAsync()
    {
        _logger.LogDebug("[{ApplicationName}, txnNumber {Id}] Commit transaction", ApplicationName,
            SessionHandle.WrappedCoreSession.CurrentTransaction.TransactionNumber);

        if (!_isSessionActive)
        {
            throw SessionOperationException.SessionNotActive();
        }

        await SessionHandle.CommitTransactionAsync();
        _isSessionActive = false;
        await RunCallbacksAsync(TakeCallbacks(_afterCommit), "commit").ConfigureAwait(false);
    }

    public async Task AbortTransactionAsync()
    {
        if (!_isSessionActive)
        {
            _logger.LogWarning("[{ApplicationName}] Abort requested but session is not active, skipping", ApplicationName);
            return;
        }

        _logger.LogDebug("[{ApplicationName}, txnNumber {Id}] Abort transaction", ApplicationName,
            SessionHandle.WrappedCoreSession.CurrentTransaction.TransactionNumber);

        _isSessionActive = false;
        await SessionHandle.AbortTransactionAsync();
        await RunCallbacksAsync(TakeCallbacks(_afterRollback), "rollback").ConfigureAwait(false);
    }

    public IClientSessionHandle SessionHandle { get; }
}
