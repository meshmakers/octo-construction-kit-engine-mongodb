using FluentAssertions;

using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

// AB#4367 — mongo tool arguments must be built as one argv entry per argument (passed via
// ProcessStartInfo.ArgumentList), so passwords with leading/embedded spaces survive verbatim,
// and the display string used for logs and CommandResult.Command must never leak the password.
public class MongoToolArgumentsTests
{
    private const string ConnectionString = "mongodb://localhost/wwc26?authSource=admin&retryWrites=true";

    [Fact]
    public void BuildMongoDumpArguments_PasswordWithLeadingAndEmbeddedSpaces_IsSingleVerbatimArgument()
    {
        var options = MongoDumpOptions.ForArchive("wwc26", "/tmp/dump dir/archive.tar.gz");

        var args = MongoToolArguments.BuildMongoDumpArguments(options, ConnectionString,
            "octo-system-admin", " p@ss word");

        args.Should().Contain("--password= p@ss word");
        args.Should().Contain("--username=octo-system-admin");
        args.Should().Contain($"--uri={ConnectionString}");
        args.Should().Contain("--archive=/tmp/dump dir/archive.tar.gz");
        args.Should().OnlyContain(a => !a.Contains('"'), "ArgumentList passes values verbatim — embedded quotes would become literal");
    }

    [Fact]
    public void BuildMongoRestoreArguments_WithNamespaceMapping_EmitsIncludeFromAndTo()
    {
        var options = new MongoRestoreOptions
        {
            Database = "test",
            Archive = "/tmp/archive.tar.gz",
            Gzip = true,
            Drop = true,
            NsFrom = "wwc26.*",
            NsTo = "test.*"
        };

        var args = MongoToolArguments.BuildMongoRestoreArguments(options, ConnectionString, null, null);

        args.Should().Contain("--nsInclude=wwc26.*");
        args.Should().Contain("--nsFrom=wwc26.*");
        args.Should().Contain("--nsTo=test.*");
        args.Should().Contain("--drop");
        args.Should().Contain("--gzip");
        args.Should().OnlyContain(a => !a.Contains('"'));
    }

    [Fact]
    public void BuildMongoRestoreArguments_WithoutNamespaceMapping_DefaultsToTargetNamespace()
    {
        var options = new MongoRestoreOptions
        {
            Database = "test",
            Archive = "/tmp/archive.tar.gz",
            Gzip = true
        };

        var args = MongoToolArguments.BuildMongoRestoreArguments(options, ConnectionString, null, null);

        args.Should().Contain("--nsInclude=test.*");
        args.Should().NotContain(a => a.StartsWith("--nsFrom="));
        args.Should().NotContain(a => a.StartsWith("--nsTo="));
    }

    [Fact]
    public void BuildMongoRestoreArguments_AlwaysEmitsAnExplicitBatchSize_DefaultingTo100()
    {
        var options = new MongoRestoreOptions { Database = "test", Archive = "/tmp/archive.tar.gz", Gzip = true };

        var args = MongoToolArguments.BuildMongoRestoreArguments(options, ConnectionString, null, null);

        args.Should().ContainSingle(a => a.StartsWith("--batchSize="));
        args.Should().Contain("--batchSize=100");
    }

    [Fact]
    public void BuildMongoRestoreArguments_UsesTheConfiguredDefault_AndTheOptionOverridesIt()
    {
        var options = new MongoRestoreOptions { Database = "test", Archive = "/tmp/a.gz" };

        MongoToolArguments.BuildMongoRestoreArguments(options, ConnectionString, null, null, 25)
            .Should().Contain("--batchSize=25");

        options.BatchSize = 10;
        MongoToolArguments.BuildMongoRestoreArguments(options, ConnectionString, null, null, 25)
            .Should().Contain("--batchSize=10").And.NotContain("--batchSize=25");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1001)]
    public void BuildMongoRestoreArguments_WithBatchSizeOutOfRange_Throws(int batchSize)
    {
        var options = new MongoRestoreOptions { Database = "test", Archive = "/tmp/a.gz", BatchSize = batchSize };

        var act = () => MongoToolArguments.BuildMongoRestoreArguments(options, ConnectionString, null, null);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*between 1 and 1000*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void SystemConfiguration_WithInvalidMongoRestoreBatchSize_FailsNamingTheOption(int value)
    {
        var act = () => new OctoSystemConfiguration { MongoRestoreBatchSize = value };

        act.Should().Throw<ConfigurationErrorException>().WithMessage("*MongoRestoreBatchSize*between 1 and 1000*");
    }

    [Fact]
    public void SystemConfiguration_BoundFromConfiguration_FailsAtBindWithTheOptionName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["System:MongoRestoreBatchSize"] = "5000" })
            .Build();

        var act = () => configuration.GetSection("System").Get<OctoSystemConfiguration>();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("MongoRestoreBatchSize");
        new OctoSystemConfiguration().MongoRestoreBatchSize.Should().Be(100);
    }

    [Fact]
    public void BuildMongoShellArguments_WithCredentials_EmitsUserAndPassword()
    {
        var args = MongoToolArguments.BuildMongoShellArguments("octo-system-admin", " p@ss word");

        args.Should().Equal("--username=octo-system-admin", "--password= p@ss word");
    }

    [Fact]
    public void ToDisplayString_MasksPasswordAndQuotesArgsWithWhitespace()
    {
        var options = MongoDumpOptions.ForArchive("wwc26", "/tmp/dump dir/archive.tar.gz");
        var args = MongoToolArguments.BuildMongoDumpArguments(options, ConnectionString,
            "octo-system-admin", " p@ss word");

        var display = MongoToolArguments.ToDisplayString("mongodump", args);

        display.Should().StartWith("mongodump ");
        display.Should().Contain("--password=***");
        display.Should().NotContain("p@ss word");
        display.Should().Contain("\"--archive=/tmp/dump dir/archive.tar.gz\"");
    }

    [Fact]
    public void RedactCommandLine_MasksPasswordTokensAndUriCredentials()
    {
        var commandLine =
            "mongodump --uri=mongodb://admin:secretvalue@localhost/db --password=topsecret --db=db";

        var redacted = MongoToolArguments.RedactCommandLine(commandLine);

        redacted.Should().NotContain("topsecret");
        redacted.Should().NotContain("secretvalue");
        redacted.Should().Contain("--password=***");
        redacted.Should().Contain("mongodb://admin:***@localhost/db");
    }

    [Fact]
    public void ToDisplayString_MasksCredentialsEmbeddedInUri()
    {
        var args = new List<string> { "--uri=mongodb://octo-system-admin:secretvalue@localhost/wwc26" };

        var display = MongoToolArguments.ToDisplayString("mongodump", args);

        display.Should().NotContain("secretvalue");
        display.Should().Contain("mongodb://octo-system-admin:***@localhost/wwc26");
    }
}
