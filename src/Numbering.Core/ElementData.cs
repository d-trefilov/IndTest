namespace Numbering.Core;

public sealed class ElementData
{
    public ElementData(long id, string name, long? levelId, string level, double? x, double? y,
        string currentValue, string? issue = null)
    {
        Id = id;
        Name = name;
        LevelId = levelId;
        Level = level;
        X = x;
        Y = y;
        CurrentValue = currentValue;
        Issue = issue;
    }

    public long Id { get; }
    public string Name { get; }
    public long? LevelId { get; }
    public string Level { get; }

    // Coordinates in millimeters in the model's XY plane.
    public double? X { get; }
    public double? Y { get; }
    public string CurrentValue { get; }
    public string? Issue { get; }
}
