using System.Text;

namespace SteamDxvk;

/// <summary>Minimal Valve KeyValues (VDF/ACF) parser: quoted keys and values, nested braces. Keys are case-insensitive.</summary>
internal static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        var root = NewObject();
        var stack = new Stack<Dictionary<string, object>>();
        stack.Push(root);
        string? key = null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{')
            {
                i++;
                var child = NewObject();
                if (key is not null) stack.Peek()[key] = child;
                stack.Push(child);
                key = null;
            }
            else if (c == '}')
            {
                i++;
                if (stack.Count > 1) stack.Pop();
                key = null;
            }
            else if (c == '"')
            {
                string value = ReadQuoted(text, ref i);
                if (key is null) key = value;
                else { stack.Peek()[key] = value; key = null; }
            }
            else i++;
        }
        return root;
    }

    public static Dictionary<string, object>? Object(object? node) => node as Dictionary<string, object>;

    public static string? Str(Dictionary<string, object> node, string key) =>
        node.TryGetValue(key, out var v) ? v as string : null;

    private static Dictionary<string, object> NewObject() => new(StringComparer.OrdinalIgnoreCase);

    private static string ReadQuoted(string text, ref int i)
    {
        i++;   // opening quote
        var sb = new StringBuilder();
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                i += 2;
            }
            else sb.Append(text[i++]);
        }
        i++;   // closing quote
        return sb.ToString();
    }
}
