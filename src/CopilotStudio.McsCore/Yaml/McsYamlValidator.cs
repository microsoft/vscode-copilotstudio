// Copyright (C) Microsoft Corporation. All rights reserved.

using System;
using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>Reads YAML through the object model after rejecting the malformed shapes its lenient parser silently drops.</summary>
internal static class McsYamlValidator
{
    /// <summary>Throws <see cref="McsYamlFormatException"/> for unresolved conflict markers, a tab in indentation, an unterminated quote, or a duplicate mapping key.</summary>
    internal static void ThrowIfMalformed(string yaml)
    {
        if (McsConflictMarkers.Contains(yaml))
        {
            throw new McsYamlFormatException(McsConflictMarkers.Message, McsConflictMarkers.FindFirstMarkerLine(yaml), 1, McsYamlError.MergeConflict);
        }

        try
        {
            McsYamlReader.ParseDocument(yaml);
        }
        catch (McsYamlFormatException failure) when (failure.Error == McsYamlError.Syntax)
        {
        }
    }

    /// <summary>Deserializes into <typeparamref name="T"/>, rejecting malformed YAML first.</summary>
    internal static T? Deserialize<T>(string yaml) where T : BotElement
    {
        ThrowIfMalformed(yaml);
        return CodeSerializer.Deserialize<T>(yaml);
    }

    /// <summary>Deserializes into the given element type, rejecting malformed YAML first.</summary>
    internal static BotElement? Deserialize(string yaml, Type type, Uri? uri)
    {
        ThrowIfMalformed(yaml);
        return CodeSerializer.Deserialize(yaml, type, uri);
    }
}
