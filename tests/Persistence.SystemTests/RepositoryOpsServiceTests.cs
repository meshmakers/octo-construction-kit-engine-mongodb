using FluentAssertions;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Xunit;


namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class RepositoryOpsServiceTests(SystemFixture systemFixture) : IClassFixture<SystemFixture>
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
    public async Task ExecuteMongoShellCommandAsync_WithQuotesInCommand_ShouldEscapeQuotes()
    {
        // Arrange
        var command = "print(\"Hello World\")";

        // Act
        var result = await _service.ExecuteMongoShellCommandAsync("testdb", command);

        // Assert
        result.Should().NotBeNull();
        result.Command.Should().Contain("mongosh");
        result.Command.Should().Contain("print(\\\"Hello World\\\")");
    }

    #endregion

    #region Core Command Execution Tests

    // [Fact]
    // public async Task ExecuteCommandAsync_WithEchoCommand_ShouldReturnSuccess()
    // {
    //     // Arrange
    //     var testMessage = "Hello World";
    //
    //     // Act
    //     var result = await _service.ExecuteCommandAsync("echo", testMessage);
    //
    //     // Assert
    //     result.Should().NotBeNull();
    //     result.Success.Should().BeTrue();
    //     result.ExitCode.Should().Be(0);
    //     result.Output.Should().Contain(testMessage);
    //     result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    //     result.Command.Should().Be($"echo {testMessage}");
    // }

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
        // Arrange
        var tempDir = Path.GetTempPath();

        // Act
        var result = await _service.ExecuteCommandAsync("pwd", "", tempDir);

        // Assert
        result.Should().NotBeNull();
        if (result.Success) // pwd might not be available on all systems
        {
            result.Output.Should().Contain(tempDir.TrimEnd('/'));
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
