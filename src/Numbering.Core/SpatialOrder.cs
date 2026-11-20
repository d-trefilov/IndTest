namespace Numbering.Core;

public static class SpatialOrder
{
    public const double RowTolerance = 300;

    public static IReadOnlyList<ElementData> Sort(IEnumerable<ElementData> elements)
    {
        var ordered = new List<ElementData>();
        var levels = elements
            .Where(element => element.LevelId.HasValue && element.X.HasValue && element.Y.HasValue)
            .GroupBy(element => element.LevelId)
            .OrderBy(level => level.Key);

        foreach (var level in levels)
        {
            var byY = level.OrderByDescending(element => element.Y).ToArray();
            int start = 0;
            while (start < byY.Length)
            {
                double anchor = byY[start].Y!.Value;
                int end = start + 1;
                while (end < byY.Length && anchor - byY[end].Y!.Value <= RowTolerance)
                    end++;

                ordered.AddRange(byY.Skip(start).Take(end - start)
                    .OrderBy(element => element.X)
                    .ThenBy(element => element.Id));
                start = end;
            }
        }

        return ordered;
    }
}
