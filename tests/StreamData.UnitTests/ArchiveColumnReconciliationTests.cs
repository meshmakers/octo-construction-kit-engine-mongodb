using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// Archive tables are provisioned with CREATE TABLE IF NOT EXISTS, so on an already-activated
// archive a newly declared column never reaches CrateDB: everything written to it is dropped as an
// unknown column, or — once SQL references it — every write to the archive fails. Before this, the
// only way to adopt such a change was to drop the table, which for a populated archive means losing
// its history. That is what made the opt-in ConflictVersionColumn unusable on exactly the archives
// that have the ordering defect.
//
// The ALTER itself was verified against CrateDB 5.10.10 on a populated table (it succeeds, and
// existing rows carry NULL for the new column). What is pinned here is the decision: which columns
// are added, which are deliberately left alone, and what happens to a required one.
public class ArchiveColumnReconciliationTests
{
    private static ArchiveColumnDdl Ingested(string path, bool required = false) =>
        new(path, new CrateColumnType.Primitive("DOUBLE PRECISION"), required, Indexed: false);

    private static ArchiveColumnDdl Computed(string name) =>
        new(string.Empty, new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: false, Indexed: false, ColumnName: name);

    private static IReadOnlySet<string> Existing(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    [Fact]
    public void AColumnThePhysicalTableAlreadyHasIsNotTouched()
    {
        var plan = ArchiveColumnReconciliation.Plan(
            [Ingested("Amount.Value")], Existing("amountvalue"));

        Assert.Empty(plan);
    }

    [Fact]
    public void ADeclaredColumnTheTableLacksIsAdded()
    {
        var plan = ArchiveColumnReconciliation.Plan(
            [Ingested("Amount.Value"), Ingested("SourceDocumentDate")], Existing("amountvalue"));

        var addition = Assert.Single(plan);
        Assert.Equal("sourcedocumentdate", addition.Name);
    }

    [Fact]
    public void AColumnTheArchiveNoLongerDeclaresIsNeverDropped()
    {
        // Only additions are planned. Dropping would destroy data on a definition edit, which must
        // stay an operator decision rather than a side effect of re-activating an archive.
        var plan = ArchiveColumnReconciliation.Plan(
            [Ingested("Amount.Value")], Existing("amountvalue", "retiredcolumn"));

        Assert.Empty(plan);
    }

    [Fact]
    public void ARequiredColumnIsAddedNullableAndFlaggedForTheWarning()
    {
        // CrateDB cannot add a NOT NULL column to a table that may already hold rows, and every
        // existing row necessarily has no value for it. Skipping the column instead would preserve
        // exactly the definition-versus-table disagreement this exists to close, so it is added
        // nullable and the caller says so.
        var plan = ArchiveColumnReconciliation.Plan(
            [Ingested("ObisCode", required: true)], Existing("amountvalue"));

        var addition = Assert.Single(plan);
        Assert.True(addition.DeclaredRequired);
        Assert.False(addition.Column.Required);
    }

    [Fact]
    public void AComputedColumnIsLeftToItsOwnLifecycle()
    {
        // A computed column is created as a versioned physical column and populated by a backfill
        // (AB#4189 Phase 7). Creating it empty here would bypass the backfill and leave a column of
        // NULLs that reads like data.
        var plan = ArchiveColumnReconciliation.Plan(
            [Computed("derived")], Existing("amountvalue"));

        Assert.Empty(plan);
    }

    [Fact]
    public void ARollupAggregateColumnIsNotReconciled()
    {
        // Same reason: an aggregate column is derived rather than written, and an empty one is
        // indistinguishable from a bucket that legitimately aggregated to nothing.
        var plan = ArchiveColumnReconciliation.Plan(
            [Computed("amountvalue_sum")], Existing("dataquality_max"));

        Assert.Empty(plan);
    }

    [Fact]
    public void SeveralMissingColumnsAreAllPlanned()
    {
        var plan = ArchiveColumnReconciliation.Plan(
            [Ingested("Amount.Value"), Ingested("Amount.Unit"), Ingested("SourceDocumentDate")],
            Existing("amountvalue"));

        Assert.Equal(["amountunit", "sourcedocumentdate"], plan.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void ComparisonUsesThePhysicalNameNotTheAttributePath()
    {
        // Physical names are the dot-stripped, lower-cased form (ColumnNameMapper). Comparing the
        // path would report every nested column as missing and ALTER on every single activation.
        var plan = ArchiveColumnReconciliation.Plan(
            [Ingested("Amount.Value")], Existing("Amount.Value"));

        Assert.Single(plan);
        Assert.Equal("amountvalue", plan[0].Name);
    }
}
