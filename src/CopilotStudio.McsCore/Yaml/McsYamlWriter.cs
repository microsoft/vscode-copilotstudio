// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections;
using System.Globalization;
using System.Text;

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>Writes the block-YAML shape produced by the previous serializer, using the host platform's line endings, so existing sidecars rewrite byte-identically.</summary>
internal static class McsYamlWriter
{
    private static readonly string LineBreak = Environment.NewLine;

    private const int MaximumImplicitKeyLength = 1024;

    public static string Write(IDictionary<string, object?> map)
    {
        if (map == null)
        {
            throw new ArgumentNullException(nameof(map));
        }

        var builder = new StringBuilder();
        WriteMapping(builder, map, 0);
        return builder.ToString();
    }

    private static void WriteMapping(StringBuilder builder, IDictionary<string, object?> map, int indent)
    {
        foreach (var entry in map)
        {
            WriteEntry(builder, entry.Key, entry.Value, indent);
        }
    }

    private static void WriteEntry(StringBuilder builder, string key, object? value, int indent)
        => WriteFormattedEntry(builder, FormatScalar(key), value, indent);

    private static void WriteFormattedEntry(StringBuilder builder, string formattedKey, object? value, int indent)
    {
        if (formattedKey.Length > MaximumImplicitKeyLength)
        {
            builder.Append(' ', indent).Append("? ").Append(formattedKey).Append(LineBreak).Append(' ', indent).Append(':');
        }
        else
        {
            builder.Append(' ', indent).Append(formattedKey).Append(':');
        }

        switch (value)
        {
            case IDictionary<string, object?> child when child.Count > 0:
                builder.Append(LineBreak);
                WriteMapping(builder, child, indent + 2);
                return;

            case IEnumerable sequence when value is not string && TryGetNonEmptySequence(sequence, out var items):
                builder.Append(LineBreak);
                WriteSequenceItems(builder, items, indent);
                return;

            default:
                builder.Append(' ').Append(FormatInlineValue(value)).Append(LineBreak);
                return;
        }
    }

    private static void WriteSequenceItems(StringBuilder builder, IEnumerable sequence, int indent)
    {
        foreach (var item in sequence)
        {
            if (item is IDictionary<string, object?> map && map.Count > 0)
            {
                WriteSequenceMapping(builder, map, indent);
                continue;
            }

            if (item is IEnumerable nested && item is not string && TryGetNonEmptySequence(nested, out var nestedItems))
            {
                builder.Append(' ', indent).Append('-').Append(LineBreak);
                WriteSequenceItems(builder, nestedItems, indent + 2);
                continue;
            }

            builder.Append(' ', indent).Append("- ").Append(FormatInlineValue(item)).Append(LineBreak);
        }
    }

    private static void WriteSequenceMapping(StringBuilder builder, IDictionary<string, object?> map, int indent)
    {
        var first = true;
        foreach (var entry in map)
        {
            if (first)
            {
                var formattedKey = FormatScalar(entry.Key);
                if (formattedKey.Length > MaximumImplicitKeyLength)
                {
                    builder.Append(' ', indent).Append('-').Append(LineBreak);
                    WriteMapping(builder, map, indent + 2);
                    return;
                }

                var position = builder.Length;
                WriteFormattedEntry(builder, formattedKey, entry.Value, indent + 2);
                builder[position + indent] = '-';
                first = false;
                continue;
            }

            WriteEntry(builder, entry.Key, entry.Value, indent + 2);
        }
    }

    private static bool TryGetNonEmptySequence(IEnumerable sequence, out IEnumerable items)
    {
        if (sequence is ICollection collection)
        {
            items = collection;
            return collection.Count > 0;
        }

        var buffered = new List<object?>();
        foreach (var item in sequence)
        {
            buffered.Add(item);
        }

        items = buffered;
        return buffered.Count > 0;
    }

    private static string FormatInlineValue(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string scalar:
                return FormatScalar(scalar);
            case McsYamlTaggedScalar tagged:
                return tagged.Tag + " " + FormatScalar(tagged.Text);
            case bool flag:
                return flag ? "true" : "false";
            case IDictionary<string, object?>:
                return "{}";
            case IEnumerable when value is not string:
                return "[]";
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static string FormatScalar(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        if (RequiresDoubleQuoting(value))
        {
            return DoubleQuote(value);
        }

        return RequiresSingleQuoting(value) ? "'" + value.Replace("'", "''") + "'" : value;
    }

    private static bool RequiresDoubleQuoting(string value)
    {
        foreach (var character in value)
        {
            if (character == '\n' || character == '\r' || character == '\t' || character == '\u0085' || character == '\u2028' || character == '\u2029')
            {
                return true;
            }

            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RequiresSingleQuoting(string value)
    {
        if (value != value.Trim())
        {
            return true;
        }

        if (McsYamlScalars.IsNullLiteral(value))
        {
            return true;
        }

        if (IsBlockIndicator(value[0]) && (value.Length == 1 || IsSeparationCharacter(value[1])))
        {
            return true;
        }

        switch (value[0])
        {
            case '#':
            case ',':
            case '[':
            case ']':
            case '{':
            case '}':
            case '&':
            case '*':
            case '!':
            case '|':
            case '>':
            case '\'':
            case '"':
            case '%':
            case '@':
            case '`':
                return true;
        }

        return value.IndexOf(": ", StringComparison.Ordinal) >= 0
            || value.IndexOf(" #", StringComparison.Ordinal) >= 0
            || value.EndsWith(":", StringComparison.Ordinal)
            || StartsWithDocumentMarker(value);
    }

    private static bool IsBlockIndicator(char value) => value == '-' || value == '?' || value == ':';

    private static bool IsSeparationCharacter(char value) => value == ' ' || value == '\t'
        || value == '\n' || value == '\r' || value == '\u0085' || value == '\u2028' || value == '\u2029';

    private static bool StartsWithDocumentMarker(string value) => value.Length >= 3
        && (value.StartsWith("...", StringComparison.Ordinal) || value.StartsWith("---", StringComparison.Ordinal))
        && (value.Length == 3 || value[3] == ' ' || value[3] == '\t');

    private static string DoubleQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\0': builder.Append("\\0"); break;
                case '\a': builder.Append("\\a"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\v': builder.Append("\\v"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                case '\u001b': builder.Append("\\e"); break;
                case '\u0085': builder.Append("\\N"); break;
                case '\u00a0': builder.Append("\\_"); break;
                case '\u2028': builder.Append("\\L"); break;
                case '\u2029': builder.Append("\\P"); break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\x").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
