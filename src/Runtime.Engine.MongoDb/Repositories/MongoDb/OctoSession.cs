using System.Diagnostics;

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

    internal OctoSession(ILogger<OctoSession> logger, IClientSessionHandle clientSessionHandle, string applicationName)
    {
        _logger = logger;
        _logger.LogDebug("[{ApplicationName}] Create session", applicationName);
        _isSessionActive = false;
        _isSessionStarted = false;
        _isDisposed = false;
        SessionHandle = clientSessionHandle;
        ApplicationName = applicationName;
    }

    public string ApplicationName { get; set; }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            if (_isSessionActive)
            {
                try
                {
                    SessionHandle.AbortTransaction();
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
    }

    public IClientSessionHandle SessionHandle { get; }
}
