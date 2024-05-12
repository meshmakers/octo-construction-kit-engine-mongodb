using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Implementation of <see cref="IOctoSystemSession"/>.
/// </summary>
/// <param name="logger"></param>
/// <param name="clientSessionHandle"></param>
/// <param name="applicationName"></param>
internal class OctoSystemSession(
    ILogger<OctoSystemSession> logger,
    IClientSessionHandle clientSessionHandle,
    string applicationName)
    : OctoSession(logger, clientSessionHandle, applicationName), IOctoSystemSession;