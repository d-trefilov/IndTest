using System.Diagnostics;
using Numbering.Core;
using Xunit.Abstractions;

namespace Numbering.Core.Tests;

public sealed class LargeCategoryTests
{
    private readonly ITestOutputHelper _output;

    public LargeCategoryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void GeneratesUniqueValuesFor10200ElementsAcrossThreeLevels()
    {
        const int levelCount = 3;
        const int elementsPerLevel = 3400;
        var rows = Enumerable.Range(0, levelCount * elementsPerLevel).Select(index =>
        {
            int levelId = index / elementsPerLevel + 1;
            int rank = index % elementsPerLevel;
            return new ElementData(index + 1, "Стена", levelId, $"Этаж {levelId}",
                rank % 100 * 1000, -(rank / 100) * 1000, "");
        }).ToArray();
        Assert.True(NumberTemplate.TryParse("{Префикс}-{Уровень}-{N:000}", out var template, out var error), error);

        for (int iteration = 1; iteration <= 3; iteration++)
        {
            var clock = Stopwatch.StartNew();
            var spatialOrder = SpatialOrder.Sort(rows);
            double sortMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var visible = rows.Where(row => ElementSearch.Matches(row, "СТЕН")).ToArray();
            double searchMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var scope = NumberingPlanner.GetScope(rows, visible, null);
            var proposals = NumberingPlanner.Create(rows, spatialOrder, scope, template!, "С");
            double previewMs = clock.Elapsed.TotalMilliseconds;

            _output.WriteLine($"Run {iteration}: sort {sortMs:F2} ms, search {searchMs:F2} ms, preview {previewMs:F2} ms.");
            Assert.Equal(10200, proposals.Count);
            Assert.Equal(10200, proposals.Values.Select(proposal => proposal.Value).Distinct().Count());
            Assert.All(proposals.Values, proposal => Assert.Equal(ProposalStatus.Ready, proposal.Status));
            Assert.Equal("С-Этаж 1-001", proposals[1].Value);
            Assert.Equal("С-Этаж 2-001", proposals[3401].Value);
            Assert.Equal("С-Этаж 3-3400", proposals[10200].Value);
        }
    }
}
