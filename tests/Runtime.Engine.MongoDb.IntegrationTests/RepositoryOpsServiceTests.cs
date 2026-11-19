using FluentAssertions;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(SystemCollection.Name)]
public class RepositoryOpsServiceTests(SystemFixture systemFixture)
{
    private readonly IRepositoryOpsService _service =
        systemFixture.Provider!.GetRequiredService<IRepositoryOpsService>();

    #region MongoDB Shell Script Tests

    [Fact]
    public async Task ExecuteMongoShellScriptAsync_WithValidScript_ShouldReturnSuccess()
    {
        // Arrange
        var tempScript = Path.GetTempFileName();
        tempScript = Path.ChangeExtension(tempScript, ".js");

        try
        {
            await File.WriteAllTextAsync(tempScript, "print('Hello MongoDB');", TestContext.Current.CancellationToken);

            // Act
            var result = await _service.ExecuteMongoShellScriptAsync("testdb", tempScript);

            // Assert
            result.Should().NotBeNull();
            result.Command.Should().Contain("mongosh");
            result.Command.Should().Contain(tempScript);
        }
        finally
        {
            if (File.Exists(tempScript))
                File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task ExecuteMongoShellScriptAsync_WithNonExistentScript_ShouldReturnFailure()
    {
        // Arrange
        var nonExistentScript = "non_existent_script.js";
    
        // Act
        var result = await _service.ExecuteMongoShellScriptAsync("testdb", nonExistentScript);
    
        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Script file not found");
        result.Error.Should().Contain(nonExistentScript);
    }

    [Fact]
    public async Task ExecuteMongoShellCommandAsync_WithSimpleCommand_ShouldBuildCorrectCommand()
    {
        // Arrange
        var command = "db.stats()";

        // Act
        var result = await _service.ExecuteMongoShellCommandAsync("testdb", command);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongosh");
        result.Command.Should().Contain("--eval");
        result.Command.Should().Contain("db.stats()");
    }

    [Fact]
    public async Task ExecuteMongoShellCommandAsync_WithQuotesInCommand_ShouldPassCommandVerbatim()
    {
        // Arrange — ArgumentList passes the eval body verbatim, no manual quote escaping (AB#4367)
        var command = "print(\"Hello World\")";

        // Act
        var result = await _service.ExecuteMongoShellCommandAsync("testdb", command);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongosh");
        result.Command.Should().Contain("print(\"Hello World\")");
    }

    #endregion

    #region Core Command Execution Tests

    [Fact]
    public async Task ExecuteCommandAsync_WithEchoCommand_ShouldReturnSuccess()
    {
        // Arrange - using dotnet --version as a cross-platform executable

        // Act
        var result = await _service.ExecuteCommandAsync("dotnet", "--version");

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.Output.Should().NotBeNullOrWhiteSpace();
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.Command.Should().Be("dotnet --version");
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithInvalidCommand_ShouldReturnFailure()
    {
        // Arrange
        var invalidCommand = "this_command_does_not_exist_12345";

        // Act
        var result = await _service.ExecuteCommandAsync(invalidCommand, "");

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(-1);
        result.Error.Should().Contain("Failed to start process");
        result.Command.Should().Contain(invalidCommand);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithWorkingDirectory_ShouldUseSpecifiedDirectory()
    {
        // Arrange - a directory with a unique name: pwd prints the path in the shell's own form (/c/Users/... under
        // Git Bash on Windows, a resolved symlink on macOS), so the test asserts on the last segment only (AB#9382).
        var dirName = $"repo-ops-pwd-{Guid.NewGuid():N}";
        var workingDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), dirName)).FullName;

        try
        {
            // Act
            var result = await _service.ExecuteCommandAsync("pwd", "", workingDirectory);

            // Assert
            result.Should().NotBeNull();
            if (result.Success) // pwd might not be available on all systems
            {
                result.Output.Trim().TrimEnd('/', '\\').Should().EndWith(dirName);
            }
        }
        finally
        {
            Directory.Delete(workingDirectory);
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithCommandThatWritesToStderr_ShouldCaptureError()
    {
        // Arrange & Act
        var result = await _service.ExecuteCommandAsync("bash", "-c \"echo 'error message' >&2\"");

        // Assert
        result.Should().NotBeNull();
        if (result.Success) // bash might not be available on all systems
        {
            result.Error.Should().Contain("error message");
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_WhenTheProgramCannotStart_ReportsThatTheProcessNeverStarted()
    {
        var missingProgram = $"octo-missing-program-{Guid.NewGuid():N}";

        var result = await _service.ExecuteCommandAsync(missingProgram, ["--version"]);

        result.Success.Should().BeFalse();
        result.ProcessNeverStarted.Should().BeTrue();
        result.ProcessMayStillRun.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteCommandAsync_WhenTheProgramRan_DoesNotReportThatItNeverStarted()
    {
        var result = await _service.ExecuteCommandAsync("bash", ["-c", "exit 3"]);

        result.Success.Should().BeFalse();
        result.ProcessNeverStarted.Should().BeFalse();
    }

    #endregion

    #region Stopping a running process

    [Fact]
    public async Task ExecuteCommandAsync_WhenCancelled_StopsTheWholeProcessTree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Reads the process state from /proc");
        var childPidFile = Path.Combine(Path.GetTempPath(), $"ops_cancel_{Guid.NewGuid():N}.pid");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var commandTask = _service.ExecuteCommandAsync("bash",
                ["-c", $"sleep 120 & echo $! > '{childPidFile}'; wait"], timeout: TimeSpan.FromMinutes(5),
                cancellationToken: cancellation.Token);
            var childPid = await WaitForChildPidAsync(childPidFile);

            await cancellation.CancelAsync();
            var result = await commandTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            result.Success.Should().BeFalse();
            result.Cancelled.Should().BeTrue();
            result.ProcessMayStillRun.Should().BeFalse();
            (await IsStoppedAsync(childPid)).Should().BeTrue("the child of the cancelled command must not keep running");
        }
        finally
        {
            File.Delete(childPidFile);
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_WhenTimedOut_StopsTheWholeProcessTree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Reads the process state from /proc");
        var childPidFile = Path.Combine(Path.GetTempPath(), $"ops_timeout_{Guid.NewGuid():N}.pid");
        try
        {
            var result = await _service.ExecuteCommandAsync("bash",
                ["-c", $"sleep 120 & echo $! > '{childPidFile}'; wait"], timeout: TimeSpan.FromSeconds(2));

            var childPid = await WaitForChildPidAsync(childPidFile);
            result.Success.Should().BeFalse();
            result.Cancelled.Should().BeFalse();
            result.ProcessMayStillRun.Should().BeFalse();
            result.Error.Should().Contain("timed out");
            (await IsStoppedAsync(childPid)).Should().BeTrue("the child of the timed-out command must not keep running");
        }
        finally
        {
            File.Delete(childPidFile);
        }
    }

    private static async Task<int> WaitForChildPidAsync(string childPidFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(childPidFile) &&
                int.TryParse((await File.ReadAllTextAsync(childPidFile)).Trim(), out var childPid))
            {
                return childPid;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The command did not write its child's pid to {childPidFile}");
    }

    private static async Task<bool> IsStoppedAsync(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var statPath = $"/proc/{processId}/stat";
            if (!File.Exists(statPath))
            {
                return true;
            }

            var statLine = await File.ReadAllTextAsync(statPath);
            var processState = statLine[(statLine.LastIndexOf(')') + 2)..].Split(' ')[0];
            if (processState == "Z" || processState == "X")
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    #endregion
}
