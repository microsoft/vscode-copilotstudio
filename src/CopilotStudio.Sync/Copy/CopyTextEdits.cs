// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Syntax;
using Microsoft.Agents.ObjectModel.Syntax.Tokens;
using Microsoft.CopilotStudio.McsCore.Yaml;

namespace Microsoft.CopilotStudio.Sync.Copy;

internal readonly struct CopyYamlScalar
{
    public CopyYamlScalar(string key, string value, bool atRoot, bool inSequence)
    {
        Key = key;
        Value = value;
        AtRoot = atRoot;
        InSequence = inSequence;
    }

    public string Key { get; }

    public string Value { get; }

    public bool AtRoot { get; }

    public bool InSequence { get; }
}

internal readonly struct CopyJsonString
{
    public CopyJsonString(string? ownerProperty, string value, bool isPropertyName, int depth, IReadOnlyList<string> path)
    {
        OwnerProperty = ownerProperty;
        Value = value;
        IsPropertyName = isPropertyName;
        Depth = depth;
        Path = path;
    }

    public string? OwnerProperty { get; }

    public string Value { get; }

    public bool IsPropertyName { get; }

    public int Depth { get; }

    public IReadOnlyList<string> Path { get; }
}

internal static class CopyTextEdits
{
    internal const string ArrayItemSegment = "[]";

    internal const string McsFileExtension = ".mcs.yml";

    private static readonly byte[] Utf8Preamble = { 0xEF, 0xBB, 0xBF };

    private static readonly JsonReaderOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static void VisitYaml(string relativePath, string text, Action<CopyYamlScalar> visit)
    {
        RewriteYaml(relativePath, text, scalar =>
        {
            visit(scalar);
            return null;
        });
    }

    public static string RewriteYaml(string relativePath, string text, Func<CopyYamlScalar, string?> replace) => IsMcsFile(relativePath) ? RewriteMcsYaml(text, replace) : RewriteYaml(text, replace);

    public static bool IsMcsFile(string relativePath) => relativePath.EndsWith(McsFileExtension, StringComparison.OrdinalIgnoreCase);

    public static ReadOnlyMemory<byte> WithoutPreamble(byte[] content) => content.AsMemory(PreambleLength(content));

    public static int PreambleLength(byte[] content) => HasPreamble(content) ? Utf8Preamble.Length : 0;

    public static string RewriteMcsYaml(string text, Func<CopyYamlScalar, string?> replace)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var syntax = McsYamlValidator.Deserialize(text, typeof(BotElement), null)?.Syntax ?? throw new InvalidDataException("The file has no readable structure.");
        var edits = new List<(int Start, int End, string Replacement)>();

        foreach (var token in syntax.EnumerateTokens())
        {
            if (token.Kind is not (SyntaxTokenKind.UnquotedValue or SyntaxTokenKind.QuotedStringValue) || token.Value is not string value || !TryDescribe(token, out var key, out var atRoot, out var inSequence))
            {
                continue;
            }

            AddEdit(edits, text, token.Position, token.EndPosition, value, replace(new CopyYamlScalar(key, value, atRoot, inSequence)));
        }

        return Apply(text, edits);
    }

    private static bool TryDescribe(SyntaxToken token, out string key, out bool atRoot, out bool inSequence)
    {
        var owner = token.Parent;
        inSequence = owner is SequenceElementSyntax;

        if (inSequence)
        {
            owner = owner?.Parent?.Parent;
        }

        if (owner is IMappingKeyValueSyntax pair && pair.PropertyName?.Value is { Length: > 0 } name)
        {
            key = name;
            atRoot = owner.Parent is MappingObjectSyntax mapping && mapping.Parent is null or FileSyntax;

            return true;
        }

        key = string.Empty;
        atRoot = false;

        return false;
    }

    public static string RewriteYaml(string text, Func<CopyYamlScalar, string?> replace)
    {
        var document = McsYamlReader.ParseDocument(text);

        if (document.IsEmpty)
        {
            return text;
        }

        var edits = new List<(int Start, int End, string Replacement)>();
        VisitNode(document.Root, atRoot: true, text, replace, edits);

        return Apply(text, edits);
    }

    public static string RemoveRootProperty(string text, string key)
    {
        var prefix = key + ":";
        var lineStart = 0;

        while (lineStart < text.Length)
        {
            var lineEnd = LineEnd(text, lineStart);

            if (string.CompareOrdinal(text, lineStart, prefix, 0, prefix.Length) == 0 && text.Substring(lineStart + prefix.Length, lineEnd - lineStart - prefix.Length).Trim() is { Length: > 0 } value && value[0] is not ('|' or '>'))
            {
                return text.Remove(lineStart, lineEnd - lineStart);
            }

            lineStart = lineEnd;
        }

        return text;
    }

    public static byte[] RewriteJson(byte[] content, Func<CopyJsonString, string?> replace)
    {
        var bodyStart = PreambleLength(content);
        var edits = new List<(int Start, int Length, byte[] Replacement)>();
        var reader = new Utf8JsonReader(content.AsSpan(bodyStart), JsonOptions);
        var owners = new Stack<string?>();
        var path = new List<string>();
        string? pendingProperty = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    var name = reader.GetString() ?? string.Empty;
                    AddJsonEdit(edits, ref reader, bodyStart, ReplaceAt(path, name, replace, new CopyJsonString(owners.Count > 0 ? owners.Peek() : null, name, isPropertyName: true, owners.Count, path)), name);
                    pendingProperty = name;
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (owners.Count > 0)
                    {
                        path.Add(pendingProperty ?? ArrayItemSegment);
                    }

                    owners.Push(pendingProperty ?? (owners.Count > 0 ? owners.Peek() : null));
                    pendingProperty = null;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    owners.Pop();

                    if (owners.Count > 0)
                    {
                        path.RemoveAt(path.Count - 1);
                    }

                    pendingProperty = null;
                    break;
                case JsonTokenType.String:
                    var value = reader.GetString() ?? string.Empty;
                    AddJsonEdit(edits, ref reader, bodyStart, ReplaceAt(path, pendingProperty ?? ArrayItemSegment, replace, new CopyJsonString(pendingProperty ?? (owners.Count > 0 ? owners.Peek() : null), value, isPropertyName: false, owners.Count, path)), value);
                    pendingProperty = null;
                    break;
                default:
                    pendingProperty = null;
                    break;
            }
        }

        if (edits.Count == 0)
        {
            return content;
        }

        using var output = new MemoryStream(content.Length);
        var position = 0;

        foreach (var (start, length, replacement) in edits)
        {
            output.Write(content, position, start - position);
            output.Write(replacement, 0, replacement.Length);
            position = start + length;
        }

        output.Write(content, position, content.Length - position);

        return output.ToArray();
    }

    public static void VisitJson(byte[] content, Action<CopyJsonString> visit)
    {
        RewriteJson(content, value =>
        {
            visit(value);
            return null;
        });
    }

    private static string? ReplaceAt(List<string> path, string segment, Func<CopyJsonString, string?> replace, CopyJsonString json)
    {
        path.Add(segment);

        try
        {
            return replace(json);
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    private static void AddJsonEdit(List<(int Start, int Length, byte[] Replacement)> edits, ref Utf8JsonReader reader, int bodyStart, string? replacement, string original)
    {
        if (replacement is null || string.Equals(replacement, original, StringComparison.Ordinal))
        {
            return;
        }

        var encoded = "\"" + JsonEncodedText.Encode(replacement, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString() + "\"";
        edits.Add((bodyStart + (int)reader.TokenStartIndex, reader.ValueSpan.Length + 2, Encoding.UTF8.GetBytes(encoded)));
    }

    private static void VisitNode(McsYamlNode node, bool atRoot, string text, Func<CopyYamlScalar, string?> replace, List<(int Start, int End, string Replacement)> edits)
    {
        if (node.IsAlias || node.Properties is not { } properties)
        {
            if (!node.IsAlias && node.Items is { } items)
            {
                foreach (var item in items)
                {
                    VisitNode(item, atRoot: false, text, replace, edits);
                }
            }

            return;
        }

        foreach (var property in properties)
        {
            var value = property.Value;

            if (value.IsAlias)
            {
                continue;
            }

            if (value.Kind == McsYamlNodeKind.Scalar)
            {
                AddYamlEdit(edits, text, value, replace(new CopyYamlScalar(property.Name, value.Scalar ?? string.Empty, atRoot, inSequence: false)));
                continue;
            }

            if (value.Items is { } sequence)
            {
                foreach (var item in sequence)
                {
                    if (item.Kind == McsYamlNodeKind.Scalar && !item.IsAlias)
                    {
                        AddYamlEdit(edits, text, item, replace(new CopyYamlScalar(property.Name, item.Scalar ?? string.Empty, atRoot, inSequence: true)));
                    }
                    else
                    {
                        VisitNode(item, atRoot: false, text, replace, edits);
                    }
                }

                continue;
            }

            VisitNode(value, atRoot: false, text, replace, edits);
        }
    }

    private static void AddYamlEdit(List<(int Start, int End, string Replacement)> edits, string text, McsYamlNode node, string? replacement)
    {
        if (node.Scalar is not null)
        {
            AddEdit(edits, text, node.Start.Index, node.End.Index, node.Scalar, replacement);
        }
    }

    private static void AddEdit(List<(int Start, int End, string Replacement)> edits, string text, int start, int end, string original, string? replacement)
    {
        if (replacement is null || string.Equals(replacement, original, StringComparison.Ordinal) || start < 0 || end > text.Length)
        {
            return;
        }

        var raw = text.Substring(start, Math.Max(0, end - start)).TrimEnd();

        if (raw.Length == 0 || raw.IndexOf('\n') >= 0 || raw[0] == '|' || raw[0] == '>')
        {
            return;
        }

        edits.Add((start, start + raw.Length, McsYamlWriter.FormatReplacementScalar(replacement, raw)));
    }

    private static string Apply(string text, List<(int Start, int End, string Replacement)> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var position = 0;

        foreach (var (start, end, replacement) in edits.OrderBy(edit => edit.Start))
        {
            builder.Append(text, position, start - position).Append(replacement);
            position = end;
        }

        return builder.Append(text, position, text.Length - position).ToString();
    }

    public static bool HasPreamble(byte[] content) => content.Length >= Utf8Preamble.Length && content[0] == Utf8Preamble[0] && content[1] == Utf8Preamble[1] && content[2] == Utf8Preamble[2];

    private static int LineEnd(string text, int index)
    {
        var nextBreak = text.IndexOf('\n', Math.Min(index, text.Length));

        return nextBreak < 0 ? text.Length : nextBreak + 1;
    }
}
