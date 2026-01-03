using System.Globalization;

namespace Mostlylucid.Shared.Helpers;

public static class UpdatedDateFormatter
{
    private static readonly CultureInfo UkCulture = new CultureInfo("en-GB");

    /// <summary>
    /// Formats an updated date using the provided template or default UK format.
    /// Supports placeholders:
    /// - {updateddateTime} - Full date and time (e.g., "11/11/2025 15:30")
    /// - {updateddate} - Date only (e.g., "11/11/2025")
    /// - {updatedtime} - Time only (e.g., "15:30")
    /// </summary>
    /// <param name="updatedDate">The date to format</param>
    /// <param name="template">Optional template string. If null, uses default "Updated {updateddateTime}"</param>
    /// <returns>Formatted string</returns>
    public static string Format(DateTimeOffset? updatedDate, string? template = null)
    {
        if (!updatedDate.HasValue)
        {
            return string.Empty;
        }

        // Convert to UK time zone (GMT/BST)
        var localTime = updatedDate.Value.ToLocalTime();

        // Use default template if none provided
        template ??= "Updated {updateddateTime}";

        // Format date/time components in UK format
        var dateTime = localTime.ToString("dd/MM/yyyy HH:mm", UkCulture);
        var date = localTime.ToString("dd/MM/yyyy", UkCulture);
        var time = localTime.ToString("HH:mm", UkCulture);

        // Replace placeholders
        var result = template
            .Replace("{updateddateTime}", dateTime, StringComparison.OrdinalIgnoreCase)
            .Replace("{updateddate}", date, StringComparison.OrdinalIgnoreCase)
            .Replace("{updatedtime}", time, StringComparison.OrdinalIgnoreCase);

        return result;
    }
}
