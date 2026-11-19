using System.Text;

namespace RigTune.Core.Services.GameMode;

/// <summary>A node of a Valve KeyValues (VDF/ACF) document: either a string value or a set of children.</summary>
public sealed class VdfNode
{
    public VdfNode(string? value = null) => Value = value;

    public string? Value { get; }

    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.GetValueOrDefault(key);

    public string? Get(string key) => Children.GetValueOrDefault(key)?.Value;
}

/// <summary>
/// Minimal, tolerant parser for Valve's text KeyValues format used by <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c>: quoted (or bare) keys/values, nested braces, <c>//</c> comments and
/// backslash escapes (<c>\\</c>, <c>\"</c>, <c>\n</c>, <c>\t</c>).
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        string? pendingKey = null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '{')
            {
                var child = new VdfNode();
                if (pendingKey is not null)
                {
                    stack.Peek().Children[pendingKey] = child;
                    pendingKey = null;
                }

                stack.Push(child);
                i++;
                continue;
            }

            if (c == '}')
            {
                if (stack.Count > 1)
                {
                    stack.Pop();
                }

                pendingKey = null;
                i++;
                continue;
            }

            string token = ReadToken(text, ref i);
            if (token.Length > 0 && token[0] == '[' && pendingKey is null)
            {
                continue; // conditional like [$WIN32] after a value
            }

            if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Children[pendingKey] = new VdfNode(token);
                pendingKey = null;
            }
        }

        return root;
    }

    private static string ReadToken(string text, ref int i)
    {
        var sb = new StringBuilder();
        if (text[i] == '"')
        {
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                    i += 2;
                    continue;
                }

                sb.Append(text[i++]);
            }

            i++; // closing quote
            return sb.ToString();
        }

        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
        {
            sb.Append(text[i++]);
        }

        return sb.ToString();
    }
}
