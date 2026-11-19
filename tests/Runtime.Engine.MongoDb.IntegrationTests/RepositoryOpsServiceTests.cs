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

    #endregion
}
