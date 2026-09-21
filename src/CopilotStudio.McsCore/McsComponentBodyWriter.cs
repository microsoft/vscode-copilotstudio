// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.CopilotStudio.McsCore.Yaml;

namespace Microsoft.CopilotStudio.McsCore;

internal static class McsComponentBodyWriter
{
    private const string MetadataBlockHeader = McsMetadata.PropertyName + ":";

    internal static string SerializeComponent(BotComponentBase component, DefinitionBase definition, AgentFilePath path) => Serialize(SkillBodyProjection.PrepareForWrite(component, path), SkillBodyProjection.GetBodyMetadata(component, definition, path));

    internal static string Serialize(BotComponentBase component, McsMetadata extraMetadata)
    {
        using var writer = new StringWriter();
        CodeSerializer.SerializeAsMcsYml(writer, component);
        var body = writer.ToString();

        var extraValues = new Dictionary<string, string>(StringComparer.Ordinal);
        AddValue(extraValues, McsMetadata.SchemaNameKey, extraMetadata.SchemaName);
        AddValue(extraValues, McsMetadata.BundleKey, extraMetadata.Bundle);
        AddValue(extraValues, McsMetadata.ManifestSchemaNameKey, extraMetadata.ManifestSchemaName);
        if (extraValues.Count == 0)
        {
            return body;
        }

        var remainder = ExtractMetadataBlocks(body, out var authoredLines);
        return ComposeBlock(component, authoredLines, extraValues) + remainder;
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
            while (index + 1 < lines.Length && IsIndented(lines[index + 1]))
            {
                index++;
                var key = TryGetKey(lines[index]);
                if (key != null)
                {
                    currentKey = key;
                    if (!authoredLines.ContainsKey(key))
                    {
                        authoredLines[key] = new List<string> { lines[index] };
                    }
                    else
                    {
                        currentKey = null;
                    }
                }
                else if (currentKey != null)
                {
                    authoredLines[currentKey].Add(lines[index]);
                }
            }
        }

        return string.Join("\n", remainder);
    }

    private static bool IsIndented(string line) => line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal);

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

    private static string ComposeBlock(BotComponentBase component, Dictionary<string, List<string>> authoredLines, IDictionary<string, string> extraValues)
    {
        var lines = new List<string>();
        foreach (var key in new[] { McsMetadata.ComponentNameKey, McsMetadata.DescriptionKey })
        {
            if (authoredLines.TryGetValue(key, out var authored))
            {
                lines.AddRange(authored);
            }
        }

        if (lines.Count == 0)
        {
            var fallback = new Dictionary<string, string>(StringComparer.Ordinal);
            AddValue(fallback, McsMetadata.ComponentNameKey, component.DisplayName);
            AddValue(fallback, McsMetadata.DescriptionKey, component.Description);
            lines.AddRange(IndentValues(fallback));
        }

        lines.AddRange(IndentValues(extraValues));
        return MetadataBlockHeader + "\n" + string.Join("\n", lines) + "\n";
    }

    private static IEnumerable<string> IndentValues(IDictionary<string, string> values) => McsYamlWriter.Write(values.ToDictionary(entry => entry.Key, entry => (object?)entry.Value, StringComparer.Ordinal)).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(line => "  " + line);
}
