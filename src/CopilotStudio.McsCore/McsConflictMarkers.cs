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

    /// <summary>Recovers the two sides of a value previously wrapped by <see cref="Build"/>.</summary>
    internal static bool TrySplit(string? text, out string ours, out string theirs)
    {
        ours = string.Empty;
        theirs = string.Empty;

        if (!Contains(text))
        {
            return false;
        }

        var lines = text!.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith(OursMarker, StringComparison.Ordinal));
        var splitter = Array.FindIndex(lines, line => string.Equals(line, SplitterMarker, StringComparison.Ordinal));
        var end = Array.FindIndex(lines, line => line.StartsWith(TheirsMarker, StringComparison.Ordinal));

        if (start < 0 || splitter <= start || end <= splitter)
        {
            return false;
        }

        ours = string.Join("\n", lines.Skip(start + 1).Take(splitter - start - 1));
        theirs = string.Join("\n", lines.Skip(splitter + 1).Take(end - splitter - 1));
        return true;
    }

    /// <summary>True when the line is a conflict boundary or the splitter, which never carry leading indentation.</summary>
    internal static bool IsMarkerLine(string? line) => line != null && (line.StartsWith(OursMarker, StringComparison.Ordinal) || string.Equals(line, SplitterMarker, StringComparison.Ordinal) || line.StartsWith(TheirsMarker, StringComparison.Ordinal));

    internal static int FindFirstMarkerLine(string? text)
    {
        if (text == null)
        {
            return 0;
        }

        var line = 1;
        var lineStart = 0;

        for (var index = 0; index <= text.Length; index++)
        {
            if (index != text.Length && text[index] != '\n')
            {
                continue;
            }

            if (IsConflictBoundary(text, lineStart, index))
            {
                return line;
            }

            line++;
            lineStart = index + 1;
        }

        return 0;
    }

    private static bool IsConflictBoundary(string text, int start, int end)
    {
        while (start < end && (text[start] == ' ' || text[start] == '\t'))
        {
            start++;
        }

        return (end - start) >= OursMarker.Length && (string.CompareOrdinal(text, start, OursMarker, 0, OursMarker.Length) == 0 || string.CompareOrdinal(text, start, TheirsMarker, 0, TheirsMarker.Length) == 0);
    }
}
