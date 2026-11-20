using Autodesk.Revit.DB;

namespace Numbering.Revit;

internal sealed class CategoryDefinition
{
    private CategoryDefinition(string key, string label, string prefix, BuiltInCategory category, BuiltInParameter parameter)
    {
        Key = key;
        Label = label;
        DefaultPrefix = prefix;
        Category = category;
        Parameter = parameter;
    }

    public string Key { get; }
    public string Label { get; }
    public string DefaultPrefix { get; }
    public BuiltInCategory Category { get; }
    public BuiltInParameter Parameter { get; }

    public static IReadOnlyList<CategoryDefinition> All { get; } = new[]
    {
        new CategoryDefinition("walls", "Стены", "С", BuiltInCategory.OST_Walls, BuiltInParameter.ALL_MODEL_MARK),
        new CategoryDefinition("doors", "Двери", "Д", BuiltInCategory.OST_Doors, BuiltInParameter.ALL_MODEL_MARK),
        new CategoryDefinition("windows", "Окна", "О", BuiltInCategory.OST_Windows, BuiltInParameter.ALL_MODEL_MARK),
        new CategoryDefinition("rooms", "Помещения", "П", BuiltInCategory.OST_Rooms, BuiltInParameter.ROOM_NUMBER)
    };
}
