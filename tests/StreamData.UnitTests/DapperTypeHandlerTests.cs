using System.Data;
using Dapper;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dapper;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Smoke tests for the two Dapper type handlers that survive the typed-column transition
/// (concept §8 T16). The legacy <c>JsonTypeHandler</c> stays for now because it backs the
/// pre-archive single-table store; once full per-archive routing lands and the legacy table is
/// dropped (T11), it can be removed too.
/// </summary>
public class DapperTypeHandlerTests
{
    [Fact]
    public void CkIdTypeHandler_SetValue_AssignsStringRepresentationToParameter()
    {
        var handler = new CkIdTypeHandler();
        var parameter = A.Fake<IDbDataParameter>();
        var ckId = new CkId<CkTypeId>("Test", new CkTypeId("TempSensor"));

        handler.SetValue(parameter, ckId);

        A.CallToSet(() => parameter.Value).To(ckId.ToString()).MustHaveHappened();
    }

    [Fact]
    public void CkIdTypeHandler_SetValue_NullValueWritesNull()
    {
        var handler = new CkIdTypeHandler();
        var parameter = A.Fake<IDbDataParameter>();

        handler.SetValue(parameter, null);

        A.CallToSet(() => parameter.Value).To((object?)null).MustHaveHappened();
    }

    [Fact]
    public void CkIdTypeHandler_Parse_RoundTripsThroughString()
    {
        var handler = new CkIdTypeHandler();
        var ckId = new CkId<CkTypeId>("Test", new CkTypeId("TempSensor"));

        var parsed = handler.Parse(ckId.ToString());

        Assert.NotNull(parsed);
        Assert.Equal(ckId, parsed);
    }

    [Fact]
    public void CkIdTypeHandler_Parse_RejectsNonString()
    {
        var handler = new CkIdTypeHandler();

        Assert.Throws<InvalidOperationException>(() => handler.Parse(42));
    }

    [Fact]
    public void OctoIdTypeHandler_SetValue_AssignsStringRepresentationToParameter()
    {
        var handler = new OctoIdTypeHandler();
        var parameter = A.Fake<IDbDataParameter>();
        var rtId = OctoObjectId.GenerateNewId();

        handler.SetValue(parameter, rtId);

        A.CallToSet(() => parameter.Value).To(rtId.ToString()).MustHaveHappened();
    }

    [Fact]
    public void OctoIdTypeHandler_Parse_RoundTripsThroughString()
    {
        var handler = new OctoIdTypeHandler();
        var rtId = OctoObjectId.GenerateNewId();

        var parsed = handler.Parse(rtId.ToString());

        Assert.Equal(rtId, parsed);
    }

    [Fact]
    public void OctoIdTypeHandler_Parse_RejectsNonString()
    {
        var handler = new OctoIdTypeHandler();

        Assert.Throws<InvalidOperationException>(() => handler.Parse(42));
    }

    [Fact]
    public void OctoIdTypeHandler_Parse_RejectsMalformedString()
    {
        var handler = new OctoIdTypeHandler();

        Assert.Throws<InvalidOperationException>(() => handler.Parse("not-a-valid-objectid"));
    }
}
