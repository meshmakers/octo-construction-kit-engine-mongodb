using System.Diagnostics;
using System.Text;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

public class RepositoryOpsService(
    IOptions<OctoSystemConfiguration> systemConfigurationOptions,
    ILogger<RepositoryOpsService> logger)
    : IRepositoryOpsService
{
    private readonly Guid _instanceId = Guid.NewGuid();

    #region MongoDB Shell Operations

    public async Task<CommandResult> ExecuteMongoShellScriptAsync(string databaseName, string scriptPath,
        CancellationToken? cancellationToken = null)
    {
        if (!File.Exists(scriptPath))
        {
            return CommandResult.Failure($"Script file not found: {scriptPath}");
        }

        var args = new List<string> { GetConnectionString(databaseName).ToString() };
        args.AddRange(MongoToolArguments.BuildMongoShellArguments(
            systemConfigurationOptions.Value.AdminUser, systemConfigurationOptions.Value.AdminUserPassword));
        args.Add(scriptPath);

        logger.LogInformation("Executing MongoDB shell script: {ScriptPath}", scriptPath);
        return await ExecuteCommandAsync("mongosh", args, null, null, cancellationToken);
    }

    public async Task<CommandResult> ExecuteMongoShellCommandAsync(string databaseName, string command,
        CancellationToken? cancellationToken = null)
    {
        var args = new List<string> { GetConnectionString(databaseName).ToString() };
        args.AddRange(MongoToolArguments.BuildMongoShellArguments(
            systemConfigurationOptions.Value.AdminUser, systemConfigurationOptions.Value.AdminUserPassword));
        // ArgumentList passes the eval body verbatim — no manual quote escaping
        args.Add("--eval");
        args.Add(command);

        logger.LogInformation("Executing MongoDB shell command: {Command}", command);

        return await ExecuteCommandAsync("mongosh", args, null, null, cancellationToken);
    }

    #endregion

    #region MongoDB Dump/Restore Operations

    public async Task<CommandResult> ExecuteMongoDumpAsync(MongoDumpOptions options,
        TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var args = MongoToolArguments.BuildMongoDumpArguments(options,
            GetConnectionString(options.Database).ToString(),
            systemConfigurationOptions.Value.AdminUser, systemConfigurationOptions.Value.AdminUserPassword);

        logger.LogInformation("Executing mongodump with options: {Options}",
            MongoToolArguments.ToDisplayString("mongodump", args));

        return await ExecuteCommandAsync("mongodump", args, timeout: timeout, cancellationToken: cancellationToken);
    }


    public async Task<CommandResult> ExecuteMongoRestoreAsync(MongoRestoreOptions options, TimeSpan? timeout = null,
        CancellationToken? cancellationToken = null)
    {
        var args = MongoToolArguments.BuildMongoRestoreArguments(options,
            GetConnectionString(options.Database).ToString(),
            systemConfigurationOptions.Value.AdminUser, systemConfigurationOptions.Value.AdminUserPassword);

        logger.LogInformation("Executing mongorestore with options: {Options}",
            MongoToolArguments.ToDisplayString("mongorestore", args));

        // Mongorestore can hang - Standard timeout 2 minutes
        var restoreTimeout = timeout ?? TimeSpan.FromMinutes(2);
        return await ExecuteCommandAsync("mongorestore", args, timeout: restoreTimeout,
            cancellationToken: cancellationToken);
    }

    #endregion

    #region Core Command Execution

    public async Task<CommandResult> ExecuteCommandAsync(string fileName, string arguments,
        string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments
        };

        return await ExecuteProcessAsync(startInfo, $"{fileName} {arguments}", workingDirectory, timeout,
            cancellationToken);
    }

    public async Task<CommandResult> ExecuteCommandAsync(string fileName, IReadOnlyList<string> arguments,
        string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        // ArgumentList passes each entry as one verbatim argv element — no re-tokenization, so
        // values containing whitespace (e.g. passwords with a leading space) survive intact.
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await ExecuteProcessAsync(startInfo, MongoToolArguments.ToDisplayString(fileName, arguments),
            workingDirectory, timeout, cancellationToken);
    }

    private async Task<CommandResult> ExecuteProcessAsync(ProcessStartInfo startInfo, string displayCommand,
        string? workingDirectory, TimeSpan? timeout, CancellationToken? cancellationToken)
    {
        var fileName = startInfo.FileName;
        var timeoutMs = timeout?.TotalMilliseconds ?? 300000; // Default 5 minutes
        var workingDirectoryPath = workingDirectory ?? Directory.GetCurrentDirectory();
        logger.LogInformation("Executing command: {Command}", fileName);
        logger.LogInformation("Using working-directory: {WorkingDirectory}", workingDirectoryPath);
        logger.LogDebug("Using args: {Args}", displayCommand);

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = workingDirectoryPath;

        var output = new StringBuilder();
        var error = new StringBuilder();
        var startTime = DateTime.UtcNow;

        using var process = new Process();
        process.StartInfo = startInfo;

        // Event handlers for output capturing
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                output.AppendLine(e.Data);
                logger.LogDebug("STDOUT: {Output}", e.Data);

                // Bei MongoDB Tools: Info-Level für wichtige Meldungen
                if (fileName.StartsWith("mongo") && (e.Data.Contains("connected") ||
                                                     e.Data.Contains("done dumping") || e.Data.Contains("restored") ||
                                                     e.Data.Contains("finished") || e.Data.Contains("writing")))
                {
                    logger.LogInformation("MongoDB Tool Output: {Output}", e.Data);
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                error.AppendLine(e.Data);
                logger.LogDebug("STDERR: {Error}", e.Data);

                if (fileName.StartsWith("mongo") && (e.Data.Contains("warning") ||
                                                     e.Data.Contains("error") || e.Data.Contains("failed")))
                {
                    logger.LogWarning("MongoDB Tool Warning/Error: {Error}", e.Data);
                }
            }
        };

        try
        {
            logger.LogDebug("Starting process: {Command} (Timeout: {TimeoutMs}ms)",
                displayCommand, timeoutMs);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var processTask = process.WaitForExitAsync(cancellationToken ?? CancellationToken.None);
            var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs));

            var completedTask = await Task.WhenAny(processTask, timeoutTask);

            var duration = DateTime.UtcNow - startTime;

            if (completedTask == timeoutTask)
            {
                logger.LogWarning("Process timed out after {TimeoutMs}ms: {Command}",
                    timeoutMs, displayCommand);

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true); // Kill the entire process tree
                        await process.WaitForExitAsync(); // Wait for cleanup
                    }
                }
                catch (Exception killEx)
                {
                    logger.LogError(killEx, "Error killing timed out process");
                }

                return new CommandResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = output.ToString(),
                    Error = error + $"\nProcess timed out after {timeoutMs}ms",
                    Duration = duration,
                    Command = displayCommand
                };
            }

            await processTask; // Ensure the process is fully completed

            logger.LogDebug("Process completed. Exit code: {ExitCode}, Duration: {Duration}ms",
                process.ExitCode, duration.TotalMilliseconds);

            return new CommandResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output.ToString(),
                Error = error.ToString(),
                Duration = duration,
                Command = displayCommand
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute command: {Command}", displayCommand);

            return CommandResult.Failure($"Failed to start process '{fileName}': {ex.Message}")
                .WithCommand(displayCommand);
        }
    }

    #endregion

    #region Private methods

    private MongoUrl GetConnectionString(string databaseName)
    {
        var urlBuilder = new MongoUrlBuilder();

        var systemConfiguration = systemConfigurationOptions.Value;

        if (systemConfiguration.DatabaseHost.Contains(","))
        {
            urlBuilder.Servers =
                systemConfiguration.DatabaseHost.Split(",").Select(x => new MongoServerAddress(x));
        }
        else
        {
            urlBuilder.Server = new MongoServerAddress(systemConfiguration.DatabaseHost);
        }

        if (!string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUser)
            && !string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUserPassword))
        {
            urlBuilder.AuthenticationSource = systemConfiguration.AuthenticationDatabaseName;
        }

        urlBuilder.ApplicationName = $"OctoMeshUpdate-{databaseName}-{_instanceId}-{urlBuilder.Username}";
        urlBuilder.UseTls = systemConfiguration.UseTls;
        urlBuilder.AllowInsecureTls = systemConfiguration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;
        urlBuilder.DirectConnection = systemConfiguration.UseDirectConnection;
        urlBuilder.DatabaseName = databaseName;

        return urlBuilder.ToMongoUrl();
    }

    #endregion private methods
}
