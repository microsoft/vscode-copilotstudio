// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>Detection of git-style merge conflict markers left in a workspace file.</summary>
internal static class McsConflictMarkers
{
    /// <summary>Message shown when a file still contains unresolved conflict markers.</summary>
    internal const string Message = "Unresolved merge conflict. Choose one side, remove the conflict markers, then sync again.";

    private const string OursMarker = "<<<<<<<";

    private const string SplitterMarker = "=======";

    private const string TheirsMarker = ">>>>>>>";

    internal const string OursLine = OursMarker + " ";

    internal const string SplitterLine = SplitterMarker;

    internal const string TheirsLine = TheirsMarker + " ";

    internal static bool Contains(string? text) => FindFirstMarkerLine(text) > 0;

    /// <summary>Wraps both sides of an unresolved conflict in git-style markers anchored at column zero.</summary>
    internal static string Build(string? ours, string? theirs) => string.Join("\n", OursLine, ours ?? string.Empty, SplitterLine, theirs ?? string.Empty, TheirsLine);

    /// <summary>True when the line is a conflict boundary or the splitter, which never carry leading indentation.</summary>
    internal static bool IsMarkerLine(string? line) => line != null && (IsBoundaryLine(line) || string.Equals(line, SplitterMarker, StringComparison.Ordinal));

    /// <summary>Finds the one-based line of the first boundary anchored at column zero, or zero when the text carries no unresolved conflict.</summary>
    internal static int FindFirstMarkerLine(string? text)
    {
        if (text == null)
        {
            return 0;
        }

        var lines = SplitLines(text);
        for (var index = 0; index < lines.Length; index++)
        {
            if (IsBoundaryLine(lines[index]))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

    private static bool IsBoundaryLine(string line)
        => line.StartsWith(OursMarker, StringComparison.Ordinal) || line.StartsWith(TheirsMarker, StringComparison.Ordinal);
}
