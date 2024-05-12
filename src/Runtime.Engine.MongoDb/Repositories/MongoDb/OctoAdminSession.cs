using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Implementation of <see cref="IOctoAdminSession"/>.
/// </summary>
/// <param name="logger"></param>
/// <param name="clientSessionHandle"></param>
/// <param name="applicationName"></param>
internal class OctoAdminSession(
    ILogger<OctoAdminSession> logger,
    IClientSessionHandle clientSessionHandle,
    string applicationName)
    : OctoSession(logger, clientSessionHandle, applicationName), IOctoAdminSession;