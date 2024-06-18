using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Implementation of <see cref="IOctoSession"/>.
/// </summary>
/// <param name="logger"></param>
/// <param name="clientSessionHandle"></param>
/// <param name="applicationName"></param>
internal class OctoUserSession(
    ILogger<OctoUserSession> logger,
    IClientSessionHandle clientSessionHandle,
    string applicationName)
    : OctoSession(logger, clientSessionHandle, applicationName), IOctoSession;