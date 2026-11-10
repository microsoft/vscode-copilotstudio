// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Puts files in their compared form where only a push round trip tells them apart.</summary>
public static class PushRoundTrip
{
    private const string FlowsFolder = "workflows";

    private const string FlowMetadataFileName = "metadata.yml";

    private const string SkillsFolder = "behaviors";

    private const string SkillFileName = "skill.mcs.yml";

    private static readonly string[] FlowStateKeys = { "stateCode", "statusCode" };

    private static readonly string[] SkillKeys = { "authoringSource" };

    /// <summary>Gives each file its compared file's bytes when only a push round trip tells them apart.</summary>
    /// <param name="files">The files to put in the compared form.</param>
    /// <param name="comparedFiles">The files they are compared with.</param>
    /// <param name="isAgent">Whether the files hold an agent.</param>
    /// <returns>The files, or a copy holding the compared bytes.</returns>
    public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> ToComparedForm(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> comparedFiles, bool isAgent)
    {
        Dictionary<string, ReadOnlyMemory<byte>>? inComparedForm = null;
        foreach (var file in files)
        {
            if (comparedFiles.TryGetValue(file.Key, out var compared) && !compared.Span.SequenceEqual(file.Value.Span) && HoldTheSameContent(file.Key, file.Value, compared, isAgent))
            {
                inComparedForm ??= files.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
                inComparedForm[file.Key] = compared;
            }
        }

        return inComparedForm ?? files;
    }

    private static bool HoldTheSameContent(string path, ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> compared, bool isAgent)
    {
        if (TextEquivalence.Checkout.Decode(content) is not { } text || TextEquivalence.Checkout.Decode(compared) is not { } comparedText)
        {
            return false;
        }

        if (TextEquivalence.Checkout.Same(new StringReader(text), new StringReader(comparedText)))
        {
            return true;
        }

        var keys = isAgent ? KeysThePushDoesNotCarry(path) : Array.Empty<string>();
        return keys.Length > 0 && string.Equals(WithoutKeys(text, keys), WithoutKeys(comparedText, keys), StringComparison.Ordinal);
    }

    private static string[] KeysThePushDoesNotCarry(string path)
    {
        var segments = path.Split('/');
        return segments.Length < 3 ? Array.Empty<string>() : (segments[segments.Length - 3], segments[segments.Length - 1]) switch
        {
            (FlowsFolder, FlowMetadataFileName) => FlowStateKeys,
            (SkillsFolder, SkillFileName) => SkillKeys,
            _ => Array.Empty<string>(),
        };
    }

    private static string WithoutKeys(string text, string[] keys) => string.Join("\n", text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !keys.Any(key => line.StartsWith($"{key}:", StringComparison.Ordinal))));
}
