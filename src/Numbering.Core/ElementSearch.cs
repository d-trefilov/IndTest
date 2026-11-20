using System.Globalization;

namespace Numbering.Core;

public static class ElementSearch
{
    public static bool Matches(ElementData row, string query)
    {
        string term = query.Trim();
        return term.Length == 0
            || Contains(row.Name, term)
            || Contains(row.Level, term)
            || Contains(row.CurrentValue, term)
            || Contains(row.Id.ToString(CultureInfo.InvariantCulture), term);
    }

    private static bool Contains(string text, string term) =>
        text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
}
