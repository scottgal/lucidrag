using System.ComponentModel.DataAnnotations;

namespace Mostlylucid.Shared.Helpers;

public static class EnumHelper
{
    public static string EnumDisplayName<T>(this T value) where T : Enum
    {
        var type = typeof(T);
        if (!type.IsEnum)
        {
            throw new ArgumentException("T must be an enumerated type");
        }

        var name = Enum.GetName(type, value);
        if (name == null)
        {
            throw new ArgumentException("Value is not a valid enum value");
        }

        var field = type.GetField(name);
        var attr = field!.GetCustomAttributes(typeof(DisplayAttribute), false).FirstOrDefault() as DisplayAttribute;
        return attr?.Name ?? name;
    }
}