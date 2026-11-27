using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// Which physical columns of an archive table a dump leaves out as leftovers of computed columns, and which it keeps:
/// only positive evidence makes a column a leftover.
/// </summary>
public class ArchiveColumnOwnershipTests
{
    private static readonly string[] Standard = ["timestamp", "rtid", "cktypeid", "row_origin"];

    private static ArchiveCopyColumn Double(string name) => new(name, "double precision");

    private static ArchiveColumnOwnership Ownership(
        IEnumerable<string>? owned = null, IReadOnlyList<(string Base, IReadOnlySet<string> Current)>? computed = null,
        IReadOnlySet<string>? attributes = null, bool attributesKnown = true)
        => new(Standard.Concat(owned ?? ["value"]), computed ?? [],
            attributesKnown ? attributes ?? new HashSet<string> { "value", "spare" } : null);

    private static IReadOnlySet<string> Set(params string[] names) => new HashSet<string>(names);

    private static List<ArchiveCopyColumn> Table(params ArchiveCopyColumn[] extra) =>
    [
        new("timestamp", "timestamp with time zone"), new("rtid", "text"), new("cktypeid", "text"),
        new("row_origin", "text"), Double("value"), .. extra
    ];

    [Fact]
    public void OwnedColumns_AreNeverLeftovers()
    {
        var ownership = Ownership(owned: ["value", "doubled__v1", "doubled__v2"],
            computed: [("doubled", Set("doubled__v1", "doubled__v2"))]);

        var leftovers = ownership.FindLeftovers(Table(Double("doubled__v1"), Double("doubled__v2")));

        Assert.Empty(leftovers);
    }

    [Fact]
    public void TheColumnOfARemovedComputedColumn_IsALeftover_WhenTheTypeHasNoSuchAttribute()
    {
        Assert.Equal(["tripled"], Ownership().FindLeftovers(Table(Double("tripled"))));
    }

    [Fact]
    public void APlainUnknownColumn_IsKept_WhenTheAttributesOfTheTypeCannotBeRead()
    {
        Assert.Empty(Ownership(attributesKnown: false).FindLeftovers(Table(Double("tripled"))));
    }

    [Fact]
    public void TheVersionsOfARemovedComputedColumn_AreLeftovers_EvenWhenTheAttributesCannotBeRead()
    {
        var leftovers = Ownership(attributesKnown: false).FindLeftovers(Table(Double("doubled"), Double("doubled__v1"),
            Double("doubled__v2")));

        Assert.Equal(["doubled", "doubled__v1", "doubled__v2"], leftovers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheEarlierVersionOfAComputedColumnThatStays_IsALeftover_ItsActiveAndPendingOnesAreNot()
    {
        var ownership = Ownership(owned: ["value", "doubled__v2", "doubled__v3"],
            computed: [("doubled", Set("doubled__v2", "doubled__v3"))], attributesKnown: false);

        var leftovers = ownership.FindLeftovers(Table(Double("doubled"), Double("doubled__v1"), Double("doubled__v2"),
            Double("doubled__v3")));

        Assert.Equal(["doubled", "doubled__v1"], leftovers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AColumnNamedLikeAnAttributeOfTheType_IsIngestedDataAndKept()
    {
        Assert.Empty(Ownership().FindLeftovers(Table(Double("spare"))));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("object")]
    [InlineData("double precision_array")]
    [InlineData("real")]
    public void AColumnOfATypeNoComputedColumnHas_IsKept(string dataType)
    {
        Assert.Empty(Ownership().FindLeftovers(Table(new ArchiveCopyColumn("note", dataType),
            new ArchiveCopyColumn("note__v1", dataType))));
    }

    [Theory]
    [InlineData("boolean")]
    [InlineData("integer")]
    [InlineData("bigint")]
    [InlineData("timestamp with time zone")]
    public void EveryResultTypeOfAComputedColumn_CanBeALeftover(string dataType)
    {
        Assert.Equal(["flag"], Ownership().FindLeftovers(Table(new ArchiveCopyColumn("flag", dataType))));
    }

    [Fact]
    public void TheMembersOfAnObjectColumn_AreNoColumnsOfTheirOwn()
    {
        var leftovers = Ownership(owned: ["value", "address"], attributes: Set("value", "address"))
            .FindLeftovers(Table(new ArchiveCopyColumn("address", "object"), new ArchiveCopyColumn("address['zip']", "integer")));

        Assert.Empty(leftovers);
    }

    [Fact]
    public void ACaseVariantOfAnOwnedColumn_IsOwned()
    {
        Assert.Empty(Ownership().FindLeftovers(Table(Double("VALUE"))));
    }

    [Theory]
    [InlineData("doubled__v1", "doubled")]
    [InlineData("a__b__v12", "a__b")]
    [InlineData("doubled__v", null)]
    [InlineData("doubled__vx", null)]
    [InlineData("__v1", null)]
    [InlineData("doubled", null)]
    public void VersionedBase_ReadsOnlyTheVersionSuffixOfAFormulaChange(string name, string? expected)
    {
        Assert.Equal(expected, ArchiveColumnOwnership.VersionedBase(name));
    }
}
