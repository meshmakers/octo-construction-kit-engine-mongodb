using System.Text;
using System.Text.RegularExpressions;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

/// <summary>
/// Builds mongo tool argument lists for <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
/// (one verbatim argv entry per argument, no shell quoting — values with whitespace survive intact)
/// and the redacted display string used for logging and <see cref="Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.CommandResult.Command"/>.
/// </summary>
internal static partial class MongoToolArguments
{
    public static List<string> BuildMongoDumpArguments(MongoDumpOptions options, string connectionString,
        string? adminUser, string? adminUserPassword)
    {
        var args = new List<string>
        {
            $"--uri={connectionString}"
        };

        AddAuthentication(args, adminUser, adminUserPassword);

        if (!string.IsNullOrEmpty(options.Database))
        {
            args.Add($"--db={options.Database}");
        }

        if (!string.IsNullOrEmpty(options.Collection))
        {
            args.Add($"--collection={options.Collection}");
        }

        if (!string.IsNullOrEmpty(options.OutputDirectory))
        {
            args.Add($"--out={options.OutputDirectory}");
        }

        if (!string.IsNullOrEmpty(options.Archive))
        {
            args.Add($"--archive={options.Archive}");
        }

        if (options.Gzip)
        {
            args.Add("--gzip");
        }

        if (options.Pretty)
        {
            args.Add("--pretty");
        }

        if (options.Verbose)
        {
            args.Add("--verbose");
        }

        return args;
    }

    public static List<string> BuildMongoRestoreArguments(MongoRestoreOptions options, string connectionString,
        string? adminUser, string? adminUserPassword)
    {
        var args = new List<string>
        {
            $"--uri={connectionString}"
        };

        AddAuthentication(args, adminUser, adminUserPassword);

        // Namespace mapping (for restoring to different database name)
        if (!string.IsNullOrEmpty(options.NsFrom) && !string.IsNullOrEmpty(options.NsTo))
        {
            // Include collections from archive that match the source pattern
            args.Add($"--nsInclude={options.NsFrom}");
            // Map the source namespace to the target namespace
            args.Add($"--nsFrom={options.NsFrom}");
            args.Add($"--nsTo={options.NsTo}");
        }
        else
        {
            // Database and Collection (default behavior)
            args.Add($"--nsInclude={options.Database}.{options.Collection}");
        }

        if (!string.IsNullOrEmpty(options.InputDirectory))
        {
            args.Add(options.InputDirectory);
        }

        if (!string.IsNullOrEmpty(options.Archive))
        {
            args.Add($"--archive={options.Archive}");
        }

        if (options.Drop)
        {
            args.Add("--drop");
        }

        if (options.Gzip)
        {
            args.Add("--gzip");
        }

        if (options.Verbose)
        {
            args.Add("--verbose");
        }

        if (options.DryRun)
        {
            args.Add("--dryRun");
        }

        if (options.OplogReplay)
        {
            args.Add("--oplogReplay");
        }

        if (options.RestoreDbUsersAndRoles)
        {
            args.Add("--restoreDbUsersAndRoles");
        }

        if (options.NumParallelCollections.HasValue)
        {
            args.Add($"--numParallelCollections={options.NumParallelCollections}");
        }

        return args;
    }

    public static List<string> BuildMongoShellArguments(string? adminUser, string? adminUserPassword)
    {
        var args = new List<string>();
        AddAuthentication(args, adminUser, adminUserPassword);
        return args;
    }

    /// <summary>
    /// Renders the command for logs and error surfaces: the password value is masked, credentials
    /// embedded in a connection-string URI are masked, and arguments containing whitespace are
    /// quoted for readability. Never use the result to start a process.
    /// </summary>
    public static string ToDisplayString(string fileName, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder(fileName);
        foreach (var argument in arguments)
        {
            var display = Redact(argument);
            if (display.Length == 0 || display.Any(char.IsWhiteSpace))
            {
                display = $"\"{display}\"";
            }

            sb.Append(' ').Append(display);
        }

        return sb.ToString();
    }

    private static void AddAuthentication(List<string> args, string? adminUser, string? adminUserPassword)
    {
        if (!string.IsNullOrEmpty(adminUser))
        {
            args.Add($"--username={adminUser}");
        }

        if (!string.IsNullOrEmpty(adminUserPassword))
        {
            args.Add($"--password={adminUserPassword}");
        }
    }

    private static string Redact(string argument)
    {
        if (argument.StartsWith("--password=", StringComparison.Ordinal))
        {
            return "--password=***";
        }

        return UriCredentialsRegex().Replace(argument, "$1:***@");
    }

    [GeneratedRegex(@"(://[^/@\s:]+):[^@\s]+@")]
    private static partial Regex UriCredentialsRegex();
}
