using Numbering.Core;

namespace Numbering.Core.Tests;

public sealed class NumberingPlannerTests
{
    [Theory]
    [InlineData("ере", true)]
    [InlineData(" ЭТАЖ ", true)]
    [InlineData("д-05", true)]
    [InlineData("234", true)]
    [InlineData("перегородкаx", false)]
    [InlineData("", true)]
    public void SearchMatchesPartOfAnyFieldIgnoringCase(string query, bool matches)
    {
        var row = new ElementData(1234, "Перегородка", 5, "Этаж 01", 0, 0, "Д-05");
        Assert.Equal(matches, ElementSearch.Matches(row, query));
    }

    [Fact]
    public void SelectionKeepsMatchingCategoryIdsEvenIfSearchHidesThem()
    {
        var rows = new[] { Row(1), Row(2), Row(3) };

        Assert.Equal(new long[] { 2, 3 },
            NumberingPlanner.GetScope(rows, new[] { rows[0] }, new long[] { 99, 2, 3 }));
        Assert.Equal(new long[] { 1 },
            NumberingPlanner.GetScope(rows, new[] { rows[0] }, null));
    }

    [Fact]
    public void RowsUseTopmostPointAsAnchorAndBreakTiesById()
    {
        var rows = new[]
        {
            Row(1, x: 80, y: 500),
            Row(2, x: 20, y: 201),
            Row(3, x: 0, y: 199),
            Row(5, x: 20, y: 200),
            Row(4, x: 20, y: 200)
        };

        Assert.Equal(new long[] { 2, 4, 5, 1, 3 }, SpatialOrder.Sort(rows).Select(x => x.Id));
    }

    [Fact]
    public void CounterStartsAtOnePerLevelAndSkipsRowsWithoutGeometryOrWritableParameter()
    {
        var rows = new[]
        {
            Row(1, x: 0),
            Row(2, x: 5, issue: "Только для чтения"),
            Row(3, x: 50),
            Row(4, levelId: 20, level: "Этаж 02"),
            new ElementData(5, "Неразмещённое", 20, "Этаж 02", null, null, "P"),
            new ElementData(6, "Без уровня", null, "", 0, 0, "")
        };

        var preview = Plan(rows, rows.Select(x => x.Id), "Д-{Уровень}-{N:000}");

        Assert.Equal("Д-Этаж 01-001", preview[1].Value);
        Assert.Equal("Д-Этаж 01-002", preview[3].Value);
        Assert.Equal("Д-Этаж 02-001", preview[4].Value);
        Assert.All(new long[] { 2, 5, 6 }, id => Assert.Equal(ProposalStatus.Skipped, preview[id].Status));
    }

    [Fact]
    public void GroupsByLevelIdEvenWhenNamesAreEqual()
    {
        var rows = new[] { Row(1, levelId: 10), Row(2, levelId: 20) };
        var preview = Plan(rows, new long[] { 1, 2 });
        Assert.Equal("001", preview[1].Value);
        Assert.Equal("001", preview[2].Value);
        Assert.All(preview.Values, item => Assert.Equal(ProposalStatus.Conflict, item.Status));
    }

    [Fact]
    public void AFilteredOutHolderStillBlocksTheProposedNumber()
    {
        var preview = Plan(new[] { Row(1), Row(2, current: "  д-001 ") }, new long[] { 1 }, "Д-{N:000}");
        Assert.Equal(ProposalStatus.Conflict, preview[1].Status);
        Assert.Contains("2", preview[1].Reason);
    }

    [Fact]
    public void TheExistingHolderIsKeptWhenAnotherLevelProposesTheSameNumber()
    {
        var rows = new[] { Row(1, current: "001"), Row(2, levelId: 20) };
        var preview = Plan(rows, new long[] { 1, 2 });
        Assert.Equal(ProposalStatus.Unchanged, preview[1].Status);
        Assert.Equal(ProposalStatus.Conflict, preview[2].Status);
    }

    [Fact]
    public void CanExchangeNumbersWhenBothHoldersMove()
    {
        var preview = Plan(new[] { Row(1, current: "002"), Row(2, current: "001") }, new long[] { 1, 2 });
        Assert.All(preview.Values, x => Assert.Equal(ProposalStatus.Ready, x.Status));
        Assert.Equal("001", preview[1].Value);
        Assert.Equal("002", preview[2].Value);
    }

    [Fact]
    public void ARejectedWriteBlocksDependentRowsWithoutRenumberingTheOthers()
    {
        var rows = new[] { Row(1, current: "002"), Row(2, current: "003"), Row(3, current: "A"), Row(4) };
        var preview = Plan(rows, new long[] { 1, 2, 3, 4 });
        var revised = ConflictChecker.Check(rows, preview, new Dictionary<long, string> { [1] = "Занято" });
        Assert.Equal(ProposalStatus.Skipped, revised[1].Status);
        Assert.Equal(ProposalStatus.Conflict, revised[2].Status);
        Assert.Equal(ProposalStatus.Conflict, revised[3].Status);
        Assert.Equal(ProposalStatus.Ready, revised[4].Status);
        Assert.Equal("004", revised[4].Value);
    }

    [Fact]
    public void AllCurrentHoldersMustReleaseAnExistingDuplicate()
    {
        var rows = new[] { Row(1), Row(2, current: "001"), Row(3, current: "001") };
        var preview = Plan(rows, new long[] { 1, 2 });
        Assert.Equal(ProposalStatus.Conflict, preview[1].Status);
        Assert.Equal(ProposalStatus.Ready, preview[2].Status);
    }

    private static IReadOnlyDictionary<long, NumberProposal> Plan(
        ElementData[] rows, IEnumerable<long> ids, string pattern = "{N:000}")
    {
        Assert.True(NumberTemplate.TryParse(pattern, out var template, out var error), error);
        return NumberingPlanner.Create(rows, SpatialOrder.Sort(rows), ids, template!, "Д");
    }

    private static ElementData Row(
        long id, double x = 0, double y = 0, string current = "", long levelId = 10,
        string level = "Этаж 01", string? issue = null)
    {
        return new ElementData(id, "Элемент " + id, levelId, level, x, y, current, issue);
    }
}
