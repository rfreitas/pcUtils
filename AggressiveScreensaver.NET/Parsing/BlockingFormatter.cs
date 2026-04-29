using System.Collections.Generic;
using System.Text;

namespace AggressiveScreensaver.Parsing;

/// <summary>
/// Formats a list of app display texts into a compact string capped at 2 visible items.
/// Ported from BuildList() in AggressiveScreensaver/index.ahk.
/// </summary>
public static class BlockingFormatter
{
    /// <summary>
    /// Returns empty string if the list is empty; otherwise returns up to 2 items
    /// comma-separated, with " +N more" appended if there are more.
    /// </summary>
    public static string Format(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return "";

        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            if (i >= 2)
            {
                sb.Append($" +{items.Count - 2} more");
                break;
            }
            if (i > 0)
                sb.Append(", ");
            sb.Append(items[i]);
        }
        return sb.ToString();
    }
}
