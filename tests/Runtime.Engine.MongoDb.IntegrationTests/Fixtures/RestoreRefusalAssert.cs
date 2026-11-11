using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     A refused restore returns the uniform conflict of attach and delete and names the reason (owner, prefix)
///     only in the server log (AB#8672).
/// </summary>
internal static class RestoreRefusalAssert
{
    /// <summary>The caller sees exactly the uniform conflict text, nothing about the owner.</summary>
    public static void IsUniform(CommandResult result, string databaseName)
    {
        Assert.False(result.Success);
        Assert.Equal(TenantException.DatabaseNameNotAvailable(databaseName).Message, result.Error);
    }

    /// <summary>
    ///     The caller sees the uniform conflict, and a warning in the log names the tenant, the database and
    ///     every part of the reason.
    /// </summary>
    public static void IsUniformAndLogged(CommandResult result, CapturingLoggerProvider logs, string tenantId,
        string databaseName, params string[] reasonParts)
    {
        IsUniform(result, databaseName);
        Assert.Contains(logs.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains($"Refused the restore of tenant '{tenantId}' into database '{databaseName}'",
                StringComparison.Ordinal) &&
            reasonParts.All(part => e.Message.Contains(part, StringComparison.Ordinal)));
    }
}
