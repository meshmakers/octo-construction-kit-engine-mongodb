using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>Passes every command to the real service except mongorestore, which runs the given replacement.</summary>
internal sealed class InterceptedMongoRestore(IRepositoryOpsService inner,
    Func<IRepositoryOpsService, MongoRestoreOptions, Task<CommandResult>> replacement) : IRepositoryOpsService
{
    public Task<CommandResult> ExecuteMongoShellScriptAsync(string databaseName, string scriptPath,
        CancellationToken? cancellationToken = null) =>
        inner.ExecuteMongoShellScriptAsync(databaseName, scriptPath, cancellationToken);

    public Task<CommandResult> ExecuteMongoShellCommandAsync(string databaseName, string command,
        CancellationToken? cancellationToken = null) =>
        inner.ExecuteMongoShellCommandAsync(databaseName, command, cancellationToken);

    public Task<CommandResult> ExecuteMongoDumpAsync(MongoDumpOptions options, TimeSpan? timeout = null,
        CancellationToken? cancellationToken = null) =>
        inner.ExecuteMongoDumpAsync(options, timeout, cancellationToken);

    public Task<CommandResult> ExecuteMongoRestoreAsync(MongoRestoreOptions options, TimeSpan? timeout = null,
        CancellationToken? cancellationToken = null) =>
        replacement(inner, options);

    public Task<CommandResult> ExecuteCommandAsync(string fileName, string arguments,
        string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null) =>
        inner.ExecuteCommandAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);

    public Task<CommandResult> ExecuteCommandAsync(string fileName, IReadOnlyList<string> arguments,
        string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null) =>
        inner.ExecuteCommandAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);
}
