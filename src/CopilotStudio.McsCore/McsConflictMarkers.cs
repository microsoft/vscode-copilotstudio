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

    internal static int FindFirstBoundaryLine(string? text)
    {
        if (text == null)
        {
            return 0;
        }

        var lines = SplitLines(text);
        for (var index = 0; index < lines.Length; index++)
        {
            if (TryGetBoundaryIndent(lines[index], out var indent) && indent == 0)
            {
                return index + 1;
            }
        }

        return 0;
    }

    internal static bool ContainsBoundary(string? text) => FindFirstBoundaryLine(text) > 0;

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

        var lines = SplitLines(text!);
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

        var lines = SplitLines(text);
        for (var index = 0; index < lines.Length; index++)
        {
            if (TryGetBoundaryIndent(lines[index], out var indent) && (indent == 0 || CompletesConflictBlock(lines, index, indent)))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

    private static bool TryGetBoundaryIndent(string line, out int indent)
    {
        indent = 0;
        while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
        {
            indent++;
        }

        return StartsWithMarker(line, indent, OursMarker) || StartsWithMarker(line, indent, TheirsMarker);
    }

    private static bool StartsWithMarker(string line, int indent, string marker)
        => line.Length - indent >= marker.Length && string.CompareOrdinal(line, indent, marker, 0, marker.Length) == 0;

    private static bool CompletesConflictBlock(string[] lines, int oursIndex, int indent)
    {
        if (!StartsWithMarker(lines[oursIndex], indent, OursMarker))
        {
            return false;
        }

        var splitter = -1;
        for (var index = oursIndex + 1; index < lines.Length; index++)
        {
            if (splitter < 0)
            {
                if (string.Equals(lines[index].Substring(Math.Min(indent, lines[index].Length)), SplitterMarker, StringComparison.Ordinal))
                {
                    splitter = index;
                }

                continue;
            }

            if (StartsWithMarker(lines[index], indent, TheirsMarker))
            {
                return true;
            }
        }

        return false;
    }
}
