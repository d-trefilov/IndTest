namespace Numbering.Core;

public static class NumberingPlanner
{
    public static IReadOnlyList<long> GetScope(
        IEnumerable<ElementData> categoryRows, IEnumerable<ElementData> visibleRows, IEnumerable<long>? selection)
    {
        if (selection is null)
            return visibleRows.Select(row => row.Id).ToArray();

        var selectedIds = new HashSet<long>(selection);
        return categoryRows.Where(row => selectedIds.Contains(row.Id)).Select(row => row.Id).ToArray();
    }

    public static IReadOnlyDictionary<long, NumberProposal> Create(
        IReadOnlyList<ElementData> categoryRows, IReadOnlyList<ElementData> spatialOrder,
        IEnumerable<long> scope, NumberTemplate template, string prefix)
    {
        var proposals = new Dictionary<long, NumberProposal>();
        var includedIds = new HashSet<long>(scope);
        foreach (var row in categoryRows)
        {
            if (!includedIds.Contains(row.Id))
                continue;

            string? issue = row.Issue;
            if (issue == null && !row.LevelId.HasValue)
                issue = "У элемента нет уровня.";
            if (issue == null && (!row.X.HasValue || !row.Y.HasValue))
                issue = "У элемента нет положения в модели.";
            if (issue != null)
                proposals.Add(row.Id, new NumberProposal(null, ProposalStatus.Skipped, issue));
        }

        var counters = new Dictionary<long, int>();
        foreach (var row in spatialOrder)
        {
            if (!includedIds.Contains(row.Id) || proposals.ContainsKey(row.Id))
                continue;

            long levelId = row.LevelId!.Value;
            counters.TryGetValue(levelId, out int number);
            counters[levelId] = ++number;
            string value = template.Render(prefix, row.Level, number);
            var status = StringComparer.Ordinal.Equals(row.CurrentValue, value)
                ? ProposalStatus.Unchanged : ProposalStatus.Ready;
            proposals.Add(row.Id, new NumberProposal(value, status));
        }

        return ConflictChecker.Check(categoryRows, proposals);
    }
}
