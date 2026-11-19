using System.Text;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

public class CommandResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string Command { get; set; } = string.Empty;

    /// <summary>
    ///     A restore with attach succeeded, but the tenant-created notification could not be published: the tenant is
    ///     registered and holds its data, the services have not set it up yet. Recover with a ClearCache of the tenant.
    ///     <see cref="Output" /> carries the same warning as text.
    /// </summary>
    public bool TenantCreatedNotificationFailed { get; set; }

    /// <summary>
    ///     A restore: the platform-only values it left out of the restored tenant, as paths
    ///     (<c>&lt;CK type&gt;@&lt;rtId&gt;.&lt;attribute path&gt;</c> for an attribute, <c>&lt;CK type&gt;@&lt;rtId&gt;</c>
    ///     for a deleted entity), never the values. A queued pipeline execution
    ///     that lost its sealed caller fails when it is leased. Empty for every other command.
    /// </summary>
    public IReadOnlyList<string> RemovedPlatformOnlyValues { get; set; } = [];

    public static CommandResult Failure(string error)
    {
        return new CommandResult
        {
            Success = false,
            ExitCode = -1,
            Error = error
        };
    }

    public CommandResult WithCommand(string command)
    {
        Command = command;
        return this;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Command: {Command}");
        sb.AppendLine($"Success: {Success}");
        sb.AppendLine($"Exit Code: {ExitCode}");
        sb.AppendLine($"Duration: {Duration.TotalMilliseconds:F0}ms");

        if (!string.IsNullOrEmpty(Output))
        {
            sb.AppendLine("Output:");
            sb.AppendLine(Output);
        }

        if (!string.IsNullOrEmpty(Error))
        {
            sb.AppendLine("Error:");
            sb.AppendLine(Error);
        }

        return sb.ToString();
    }
}
