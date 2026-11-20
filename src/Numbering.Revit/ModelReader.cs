using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Numbering.Core;

namespace Numbering.Revit;

internal sealed class ModelReader
{
    private static readonly TimeSpan SliceTime = TimeSpan.FromMilliseconds(12);

    private readonly Document _document;
    private readonly CategoryDefinition _category;
    private readonly ElementId[] _ids;
    private readonly List<ElementData> _rows;
    private readonly Dictionary<long, string> _levels = new();
    private int _next;

    public ModelReader(Document document, CategoryDefinition category)
    {
        _document = document;
        _category = category;
        var clock = Stopwatch.StartNew();
        using var collector = new FilteredElementCollector(document);
        _ids = collector.OfCategory(category.Category).WhereElementIsNotElementType().ToElementIds().ToArray();
        _rows = new List<ElementData>(_ids.Length);
        MaxSliceMs = clock.Elapsed.TotalMilliseconds;
    }

    public double MaxSliceMs { get; private set; }
    public IReadOnlyList<ElementData> Rows => _rows;

    public bool ReadNextSlice()
    {
        var clock = Stopwatch.StartNew();
        while (_next < _ids.Length)
        {
            if (_document.GetElement(_ids[_next++]) is Element element)
                _rows.Add(Read(element));

            if (clock.Elapsed >= SliceTime)
                break;
        }

        MaxSliceMs = Math.Max(MaxSliceMs, clock.Elapsed.TotalMilliseconds);
        return _next == _ids.Length;
    }

    private ElementData Read(Element element)
    {
        long id = ElementIds.Read(element.Id);
        var parameter = element.get_Parameter(_category.Parameter);
        string? issue = ParameterIssue(parameter);
        string value = parameter?.StorageType == StorageType.String ? parameter.AsString() ?? "" : "";

        string name = element is Room
            ? element.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? element.Name
            : element.Name;
        long? levelId = ValidLevelId(element);
        string levelName = levelId.HasValue ? LevelName(levelId.Value) : "";
        if (levelName.Length == 0)
            levelId = null;

        XYZ? point = GetPoint(element.Location);
        double? x = point == null ? null : UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters);
        double? y = point == null ? null : UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters);
        return new ElementData(id, name, levelId, levelName, x, y, value, issue);
    }

    private static XYZ? GetPoint(Location? location)
    {
        if (location is LocationPoint point)
            return point.Point;

        if (location is LocationCurve locationCurve)
        {
            var curve = locationCurve.Curve;
            if (curve != null && curve.IsBound)
                return curve.Evaluate(0.5, true);
        }

        return null;
    }

    private static long? ValidLevelId(Element element)
    {
        if (ValidId(element.LevelId) is long levelId)
            return levelId;

        if (element is Wall)
            return ValidId(element.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId());

        if (element is FamilyInstance)
            return ValidId(element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId())
                ?? ValidId(element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId());

        return null;
    }

    private static long? ValidId(ElementId? id) =>
        id != null && id != ElementId.InvalidElementId ? ElementIds.Read(id) : null;

    private string LevelName(long id)
    {
        if (!_levels.TryGetValue(id, out var name))
        {
            name = (_document.GetElement(ElementIds.Create(id)) as Level)?.Name ?? "";
            _levels.Add(id, name);
        }

        return name;
    }

    public static string? ParameterIssue(Parameter? parameter)
    {
        if (parameter == null || parameter.StorageType != StorageType.String)
            return "Строковый параметр номера недоступен.";

        return parameter.IsReadOnly ? "Параметр доступен только для чтения." : null;
    }
}
