using Autodesk.Revit.DB;

namespace Numbering.Revit;

internal static class ElementIds
{
    public static long Read(ElementId id)
    {
#if ELEMENT_ID_64_BIT
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }

    public static ElementId Create(long value)
    {
#if ELEMENT_ID_64_BIT
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }
}
