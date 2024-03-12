using System.Diagnostics;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

[DebuggerDisplay("{" + nameof(ApplicationName) + "}")]
internal class OctoSession : IOctoSessionInternal
{
    private bool _isSessionActive;
    private bool _isSessionStarted;

    internal OctoSession(IClientSessionHandle clientSessionHandle, string applicationName)
    {
        _isSessionActive = false;
        _isSessionStarted = false;
        SessionHandle = clientSessionHandle;
        ApplicationName = applicationName;
    }

    public string ApplicationName { get; set; }

    public void Dispose()
    {
        if (_isSessionActive)
        {
            SessionHandle.AbortTransaction();
        }

        SessionHandle.Dispose();
    }

    public void StartTransaction()
    {
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
            throw SessionOperationException.SessionNotActive();
        }

        await SessionHandle.AbortTransactionAsync();
        _isSessionActive = false;
    }

    public IClientSessionHandle SessionHandle { get; }
}