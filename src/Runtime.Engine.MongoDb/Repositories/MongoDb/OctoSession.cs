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
        _logger.LogDebug("[{Id}] Create session", clientSessionHandle.ServerSession.Id);
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
                SessionHandle.AbortTransaction();
            }

            SessionHandle.Dispose();
            _isDisposed = true;
        }
    }

    public void StartTransaction()
    {
        _logger.LogDebug("[{Id}] Start transaction", SessionHandle.ServerSession.Id);

        if (_isDisposed)
        {
            throw SessionOperationException.SessionDisposed();
        }
        if (_isSessionStarted)
        {
            throw SessionOperationException.SessionAlreadyStarted();
        }

        SessionHandle.StartTransaction();
        _isSessionStarted = true;
        _isSessionActive = true;
    }

    public async Task CommitTransactionAsync()
    {
        _logger.LogDebug("[{Id}] Commit transaction", SessionHandle.ServerSession.Id);

        if (!_isSessionActive)
        {
            throw SessionOperationException.SessionNotActive();
        }

        _isSessionActive = false;
        await SessionHandle.CommitTransactionAsync();
    }

    public async Task AbortTransactionAsync()
    {
        _logger.LogDebug("[{Id}] Abort transaction", SessionHandle.ServerSession.Id);

        if (!_isSessionActive)
        {
            throw SessionOperationException.SessionNotActive();
        }

        _isSessionActive = false;
        await SessionHandle.AbortTransactionAsync();
    }

    public IClientSessionHandle SessionHandle { get; }
}