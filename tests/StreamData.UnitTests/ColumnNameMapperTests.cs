using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

// Pinned conversion rules for the path-to-column mapping. The mapper is the single source of
// truth for how attribute paths become CrateDB identifiers; regressions here silently break
// upserts and queries because the column referenced in SQL drifts away from the physical schema.
//
// Storage names are fully lower-cased — CrateDB has known case-preservation quirks for quoted
// mixed-case identifiers (notably the EXCLUDED."Col" reference inside ON CONFLICT DO UPDATE).
// The API surface still carries the original PascalCase / dotted Path; only the physical column
// is canonicalised.
public class ColumnNameMapperTests
{
    [Fact]
    public void SingleSegment_AlreadyLowerCase_PassesThrough()
    {
        Assert.Equal("voltage", ColumnNameMapper.PathToColumnName("voltage"));
    }

    [Fact]
    public void SingleSegment_PascalCase_LowersAll()
    {
        Assert.Equal("voltage", ColumnNameMapper.PathToColumnName("Voltage"));
    }

    [Fact]
    public void SingleSegment_WithInnerCapitals_LowersAll()
    {
        // The motivating regression: paths like `CO2Level` used to map to `cO2Level` (mixed case),
        // which CrateDB's ON CONFLICT EXCLUDED clause did not always resolve. Lower-cased storage
        // sidesteps the issue.
        Assert.Equal("co2level", ColumnNameMapper.PathToColumnName("CO2Level"));
    }

    [Fact]
    public void DottedPath_ConcatenatesAndLowers()
    {
        Assert.Equal("sensorreadingvalue", ColumnNameMapper.PathToColumnName("sensor.reading.value"));
    }

    [Fact]
    public void DottedPath_WithMixedCasing_LowersEverything()
    {
        Assert.Equal("sensorurl", ColumnNameMapper.PathToColumnName("sensor.URL"));
        Assert.Equal("sensorreadingvalue", ColumnNameMapper.PathToColumnName("Sensor.reading.value"));
    }

    [Fact]
    public void EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => ColumnNameMapper.PathToColumnName(""));
    }

    [Fact]
    public void EmptySegment_Throws()
    {
        // `a..b` would silently produce `ab` if we didn't reject it; that's a sharp edge for path
        // producers — fail loudly so the bug surfaces at activation, not at query time.
        Assert.Throws<ArgumentException>(() => ColumnNameMapper.PathToColumnName("a..b"));
    }
}
