using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Implementation of mongodb repository client for user (CRUD) operations.
/// </summary>
internal class UserMongoRepositoryClient(
    ILogger<UserMongoRepositoryClient> logger,
    IOptions<OctoSystemConfiguration> systemConfiguration,
    IServiceProvider serviceProvider,
    string databaseName)
    : MongoRepositoryClient(logger, systemConfiguration, serviceProvider)
{
    protected override MongoUrl CreateConnectionUri()
    {
        var urlBuilder = new MongoUrlBuilder();

        var systemConfiguration = SystemConfiguration.Value;

        if (systemConfiguration.DatabaseHost.Contains(","))
            urlBuilder.Servers =
                systemConfiguration.DatabaseHost.Split(",").Select(x => new MongoServerAddress(x));
        else
            urlBuilder.Server = new MongoServerAddress(systemConfiguration.DatabaseHost);

        if (!string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUser)
            && !string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUserPassword))
        {
            urlBuilder.Username = string.Format(systemConfiguration.DatabaseUser, databaseName);
            urlBuilder.Password = systemConfiguration.DatabaseUserPassword;
            urlBuilder.DatabaseName = databaseName;
            urlBuilder.AuthenticationSource = systemConfiguration.AuthenticationDatabaseName;
        }

        urlBuilder.ApplicationName = $"OctoMesh-{databaseName}-{InstanceId}-{urlBuilder.Username}";
        urlBuilder.UseTls = systemConfiguration.UseTls;
        urlBuilder.AllowInsecureTls = systemConfiguration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;
        urlBuilder.DirectConnection = systemConfiguration.UseDirectConnection;

        return urlBuilder.ToMongoUrl();
    }
}