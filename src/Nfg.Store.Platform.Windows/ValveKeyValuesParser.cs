using System.Text;

namespace Nfg.Store.Platform.Windows;

internal static class ValveKeyValuesParser
{
    public static IReadOnlyDictionary<string, object> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Tokenize(text);
        var index = 0;
        var result = ParseObject(tokens, ref index, nested: false);
        if (index != tokens.Count)
        {
            throw new FormatException("Valve KeyValues document has trailing tokens.");
        }

        return result;
    }

    private static Dictionary<string, object> ParseObject(
        IReadOnlyList<string> tokens,
        ref int index,
        bool nested)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (index < tokens.Count)
        {
            var token = tokens[index++];
            if (token == "}")
            {
                if (!nested)
                {
                    throw new FormatException("Unexpected closing brace in Valve KeyValues document.");
                }

                return values;
            }

            if (token == "{")
            {
                throw new FormatException("Unexpected opening brace in Valve KeyValues document.");
            }

            if (index >= tokens.Count)
            {
                throw new FormatException($"Missing value for Valve KeyValues key '{token}'.");
            }

            object value;
            if (tokens[index] == "{")
            {
                index++;
                value = ParseObject(tokens, ref index, nested: true);
            }
            else if (tokens[index] == "}")
            {
                throw new FormatException($"Missing value for Valve KeyValues key '{token}'.");
            }
            else
            {
                value = tokens[index++];
            }

            values[token] = value;
        }

        if (nested)
        {
            throw new FormatException("Unterminated Valve KeyValues object.");
        }

        return values;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length)
            {
                break;
            }

            if (text[index] is '{' or '}')
            {
                tokens.Add(text[index].ToString());
                index++;
                continue;
            }

            if (text[index] != '"')
            {
                throw new FormatException($"Unexpected character at position {index} in Valve KeyValues document.");
            }

            index++;
            var value = new StringBuilder();
            var closed = false;
            while (index < text.Length)
            {
                var character = text[index++];
                if (character == '"')
                {
                    closed = true;
                    break;
                }

                if (character == '\\' && index < text.Length && text[index] is '\\' or '"')
                {
                    value.Append(text[index++]);
                }
                else
                {
                    value.Append(character);
                }
            }

            if (!closed)
            {
                throw new FormatException("Unterminated string in Valve KeyValues document.");
            }

            tokens.Add(value.ToString());
        }

        return tokens;
    }

    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] == '\uFEFF')
            {
                index++;
                continue;
            }

            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                index += 2;
                while (index < text.Length && text[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            break;
        }
    }
}
