// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.CopilotStudio.McsCore.Yaml;

namespace Microsoft.CopilotStudio.McsCore;

internal static class McsComponentBodyWriter
{
    private const string MetadataBlockHeader = McsMetadata.PropertyName + ":";

    internal static string SerializeComponent(BotComponentBase component, DefinitionBase definition, AgentFilePath path) => Serialize(SkillBodyProjection.PrepareForWrite(component, path), SkillBodyProjection.GetBodyMetadata(component, definition, path));

    internal static string ReplaceBodyPreservingMetadata(BotComponentBase component, DefinitionBase definition, AgentFilePath path, string replacementBody, McsMetadataConflict? displayNameConflict = null, McsMetadataConflict? descriptionConflict = null)
    {
        var extraValues = BuildExtraValues(SkillBodyProjection.GetBodyMetadata(component, definition, path));
        _ = ExtractMetadataBlocks(SerializeComponent(component, definition, path), out var authoredLines);

        if (authoredLines.Count == 0 && extraValues.Count == 0 && string.IsNullOrEmpty(component.DisplayName) && string.IsNullOrEmpty(component.Description)
            && displayNameConflict == null && descriptionConflict == null)
        {
            return replacementBody;
        }

        return ComposeBlock(component, authoredLines, extraValues, displayNameConflict, descriptionConflict) + replacementBody;
    }

    internal static string Serialize(BotComponentBase component, McsMetadata extraMetadata)
    {
        using var writer = new StringWriter();
        CodeSerializer.SerializeAsMcsYml(writer, component);
        var body = writer.ToString();

        var extraValues = BuildExtraValues(extraMetadata);
        if (extraValues.Count == 0)
        {
            return body;
        }

        var remainder = ExtractMetadataBlocks(body, out var authoredLines);
        return ComposeBlock(component, authoredLines, extraValues) + remainder;
    }

    private static Dictionary<string, string> BuildExtraValues(McsMetadata extraMetadata)
    {
        var extraValues = new Dictionary<string, string>(StringComparer.Ordinal);
        AddValue(extraValues, McsMetadata.SchemaNameKey, extraMetadata.SchemaName);
        AddValue(extraValues, McsMetadata.BundleKey, extraMetadata.Bundle);
        AddValue(extraValues, McsMetadata.ManifestSchemaNameKey, extraMetadata.ManifestSchemaName);
        return extraValues;
    }

    private static void AddValue(IDictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values[key] = value!;
        }
    }

    private static string ExtractMetadataBlocks(string body, out Dictionary<string, List<string>> authoredLines)
    {
        authoredLines = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var remainder = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            if (!string.Equals(lines[index], MetadataBlockHeader, StringComparison.Ordinal))
            {
                remainder.Add(lines[index]);
                continue;
            }

            string? currentKey = null;
            var keyIndent = -1;
            var blankLines = new List<string>();

            while (index + 1 < lines.Length && (lines[index + 1].Length == 0 || IsIndented(lines[index + 1])))
            {
                index++;
                if (lines[index].Length == 0)
                {
                    blankLines.Add(lines[index]);
                    continue;
                }

                var indent = IndentWidth(lines[index]);
                if (keyIndent < 0)
                {
                    keyIndent = indent;
                }

                var key = indent == keyIndent ? TryGetKey(lines[index]) : null;
                if (key != null)
                {
                    blankLines.Clear();
                    currentKey = authoredLines.ContainsKey(key) ? null : key;
                    if (currentKey != null)
                    {
                        authoredLines[currentKey] = new List<string> { lines[index] };
                    }

                    continue;
                }

                if (currentKey == null)
                {
                    blankLines.Clear();
                    continue;
                }

                authoredLines[currentKey].AddRange(blankLines);
                authoredLines[currentKey].Add(lines[index]);
                blankLines.Clear();
            }

            remainder.AddRange(blankLines);
        }

        return string.Join("\n", remainder);
    }

    private static bool IsIndented(string line) => IndentWidth(line) > 0;

    private static int IndentWidth(string line)
    {
        var width = 0;
        while (width < line.Length && (line[width] == ' ' || line[width] == '\t'))
        {
            width++;
        }

        return width;
    }

    private static string? TryGetKey(string line)
    {
        var separator = line.IndexOf(':');
        if (separator < 0)
        {
            return null;
        }

        var key = line.Substring(0, separator).Trim();
        return key.Length > 0 && key[0] != '-' ? key : null;
    }

    private static string ComposeBlock(BotComponentBase component, Dictionary<string, List<string>> authoredLines, IDictionary<string, string> extraValues, McsMetadataConflict? displayNameConflict = null, McsMetadataConflict? descriptionConflict = null)
    {
        var lines = new List<string>();
        if (displayNameConflict is { } nameConflict)
        {
            lines.AddRange(ConflictedEntry(McsMetadata.ComponentNameKey, nameConflict.Ours, nameConflict.Theirs));
        }
        else
        {
            AppendMetadataEntry(lines, McsMetadata.ComponentNameKey, component.DisplayName, authoredLines);
        }

        if (descriptionConflict is { } textConflict)
        {
            lines.AddRange(ConflictedEntry(McsMetadata.DescriptionKey, textConflict.Ours, textConflict.Theirs));
        }
        else
        {
            AppendMetadataEntry(lines, McsMetadata.DescriptionKey, component.Description, authoredLines);
        }

        lines.AddRange(IndentValues(extraValues));
        return MetadataBlockHeader + "\n" + string.Join("\n", lines) + "\n";
    }

    private static void AppendMetadataEntry(List<string> lines, string key, string? value, Dictionary<string, List<string>> authoredLines)
    {
        if (authoredLines.TryGetValue(key, out var authored))
        {
            lines.AddRange(authored);
            return;
        }

        var fallback = new Dictionary<string, string>(StringComparer.Ordinal);
        AddValue(fallback, key, value);
        lines.AddRange(IndentValues(fallback));
    }

    private static IEnumerable<string> ConflictedEntry(string key, string? ours, string? theirs)
    {
        var lines = new List<string> { McsConflictMarkers.OursLine };
        lines.AddRange(IndentValues(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = ours ?? string.Empty }));
        lines.Add(McsConflictMarkers.SplitterLine);
        lines.AddRange(IndentValues(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = theirs ?? string.Empty }));
        lines.Add(McsConflictMarkers.TheirsLine);
        return lines;
    }

    private static IEnumerable<string> IndentValues(IDictionary<string, string> values) => values.Count == 0
        ? Array.Empty<string>()
        : McsYamlWriter.Write(values.ToDictionary(entry => entry.Key, entry => (object?)entry.Value, StringComparer.Ordinal)).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(line => "  " + line);
}
