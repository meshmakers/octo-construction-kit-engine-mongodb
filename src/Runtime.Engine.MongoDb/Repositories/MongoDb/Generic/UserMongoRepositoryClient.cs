using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Implementation of the mongodb repository client for user (CRUD) operations.
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

        var systemConfiguration = _systemConfiguration.Value;

        // Parse, not the string ctor: DatabaseHost may be "host:port", which MongoDB.Driver >= 3.11.1 rejects in the ctor (CSHARP-6171).
        if (systemConfiguration.DatabaseHost.Contains(","))
            urlBuilder.Servers =
                systemConfiguration.DatabaseHost.Split(",").Select(MongoServerAddress.Parse);
        else
            urlBuilder.Server = MongoServerAddress.Parse(systemConfiguration.DatabaseHost);

        var user = string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUser)
            ? null
            : string.Format(systemConfiguration.DatabaseUser, databaseName);
        var password = systemConfiguration.DatabaseUserPassword;

        // 🔴 AB#4924 — a runtime credential for THIS database wins over the configured one. An
        // adapter-pool member is deliberately given neither the installation's datasource password nor
        // its admin password (WorkloadReconciler.AppendClusterSecrets); the credential for the tenant
        // it is currently lent arrives on the lease and is published through this seam for the
        // duration of that lease. Resolved per connection build, not cached in a field: a client is
        // built once per database and lives in a cache the lease drops on release, so reading the
        // source here is what makes the credential's lifetime the lease's rather than the process's.
        //
        // Optional on purpose — GetService, not GetRequiredService. No host registers a source unless
        // it is a pool member, and one that does not is byte-for-byte unchanged by this block.
        var credentialSource = _serviceProvider.GetService<ITenantDatabaseCredentialSource>();
        if (credentialSource is not null
            && credentialSource.TryGetCredential(databaseName, out var leasedUser, out var leasedPassword))
        {
            user = leasedUser;
            password = leasedPassword;
        }

        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
        {
            urlBuilder.Username = user;
            urlBuilder.Password = password;
            urlBuilder.DatabaseName = databaseName;
            urlBuilder.AuthenticationSource = systemConfiguration.AuthenticationDatabaseName;
        }

        urlBuilder.ApplicationName = BuildApplicationName(databaseName, _instanceId, urlBuilder.Username);
        urlBuilder.UseTls = systemConfiguration.UseTls;
        urlBuilder.AllowInsecureTls = systemConfiguration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;
        urlBuilder.DirectConnection = systemConfiguration.UseDirectConnection;

        if (!string.IsNullOrWhiteSpace(systemConfiguration.ReplicaSetName))
        {
            urlBuilder.ReplicaSetName = systemConfiguration.ReplicaSetName;
        }

        return urlBuilder.ToMongoUrl();
    }
}
