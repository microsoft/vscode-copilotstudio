// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Derives the folder names workspaces and workflows occupy, identically on every platform.</summary>
public static class WorkspaceFolderNames
{
    private const string ProjectFallbackName = "agent";

    private const int MaximumSegmentLength = 255;

    private static readonly char[] InvalidCharacters = Enumerable.Range(0, 32).Select(code => (char)code).Concat("\"<>|:*?\\/").ToArray();

    private static readonly HashSet<string> ReservedDeviceNames = new(new[] { "CON", "PRN", "AUX", "NUL" }.Concat(Enumerable.Range(1, 9).SelectMany(number => new[] { $"COM{number}", $"LPT{number}" })), StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder name a workflow of this name occupies, holding no whitespace, no character a folder name cannot hold on any platform, no reserved device name, and at most 255 characters.</summary>
    /// <param name="name">The workflow name the folder is derived from.</param>
    /// <returns>The folder name, empty when the name carries no character a folder may hold.</returns>
    public static string ForWorkflow(string? name) => Fitted(PortableWorkflowName(name), string.Empty);

    /// <summary>The folder name a project of this display name occupies, with every character a folder name cannot hold replaced by a hyphen, no reserved device name, and at most 255 characters.</summary>
    /// <param name="displayName">The project's display name.</param>
    /// <returns>The folder name, or <c>agent</c> when the display name keeps no letter or digit.</returns>
    public static string ForProject(string displayName)
    {
        if (displayName is null)
        {
            throw new ArgumentNullException(nameof(displayName));
        }

        var folderName = Fitted(new string(displayName.Select(character => InvalidCharacters.Contains(character) ? '-' : character).ToArray()).Trim().TrimEnd('.', ' '), string.Empty);

        return folderName.Any(char.IsLetterOrDigit) ? folderName : ProjectFallbackName;
    }

    internal static string ForWorkflow(string? name, Guid workflowId) => Fitted(PortableWorkflowName(name), $"-{workflowId}");

    private static string PortableWorkflowName(string? name) => new string((name ?? string.Empty).Where(character => !char.IsControl(character) && !char.IsWhiteSpace(character) && !InvalidCharacters.Contains(character)).ToArray()).TrimEnd('.', ' ');

    private static string Fitted(string name, string suffix)
    {
        var nameLength = MaximumSegmentLength - suffix.Length;
        var folderName = Guard(Truncated(name, nameLength) + suffix);

        return folderName.Length <= MaximumSegmentLength ? folderName : Guard(Truncated(name, nameLength - 1) + suffix);
    }

    private static string Truncated(string name, int length) => name.Length <= length ? name : name.Substring(0, char.IsHighSurrogate(name[length - 1]) ? length - 1 : length).TrimEnd('.', ' ');

    private static string Guard(string folderName)
    {
        var firstDot = folderName.IndexOf('.');
        var stem = (firstDot < 0 ? folderName : folderName.Substring(0, firstDot)).TrimEnd(' ');

        return ReservedDeviceNames.Contains(stem) ? folderName.Insert(stem.Length, "_") : folderName;
    }
}
