// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.CopilotStudio.McsCore.Yaml;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>A problem found in a workspace file that prevents it from being synchronized.</summary>
public sealed class WorkspaceDiagnostic
{
    /// <summary>Creates a diagnostic for a file that could not be synchronized.</summary>
    public WorkspaceDiagnostic(string filePath, string message, int line, int column, WorkspaceDiagnosticKind kind = WorkspaceDiagnosticKind.InvalidFile)
    {
        FilePath = filePath;
        Message = message;
        Line = line;
        Column = column;
        Kind = kind;
    }

    /// <summary>Workspace-relative path of the file that could not be read.</summary>
    public string FilePath { get; }

    /// <summary>Description of the problem.</summary>
    public string Message { get; }

    /// <summary>One-based line number, or zero when the position is unknown.</summary>
    public int Line { get; }

    /// <summary>One-based column number, or zero when the position is unknown.</summary>
    public int Column { get; }

    /// <summary>Why the file could not be synchronized.</summary>
    public WorkspaceDiagnosticKind Kind { get; }

    /// <summary>Formats the diagnostic as a single line including the file and position.</summary>
    public override string ToString() => ToString(static value => value);

    /// <summary>Formats the diagnostic, passing the file path and message through <paramref name="protectValue"/> so a transport can mark them as user content.</summary>
    public string ToString(Func<string, string> protectValue) => Line > 0
        ? $"{protectValue(FilePath)}({Line},{Column}): {protectValue(Message)}"
        : $"{protectValue(FilePath)}: {protectValue(Message)}";

    public static WorkspaceDiagnostic FromException(string filePath, Exception failure)
    {
        for (var current = failure; current != null; current = current.InnerException)
        {
            if (current is McsYamlFormatException formatFailure)
            {
                return new WorkspaceDiagnostic(filePath, formatFailure.Message, formatFailure.Line, formatFailure.Column,
                    formatFailure.Error == McsYamlError.MergeConflict ? WorkspaceDiagnosticKind.MergeConflict : WorkspaceDiagnosticKind.InvalidFile);
            }
        }

        return new WorkspaceDiagnostic(filePath, failure.Message, 0, 0);
    }

    /// <summary>Builds a multi-line summary of <paramref name="diagnostics"/> under <paramref name="header"/>, formatting each entry with <paramref name="format"/>.</summary>
    public static string Summarize(string header, IEnumerable<WorkspaceDiagnostic> diagnostics, Func<WorkspaceDiagnostic, string> format)
        => Summarize(_ => header, diagnostics, format);

    public static string Summarize(Func<int, string> header, IEnumerable<WorkspaceDiagnostic> diagnostics, Func<WorkspaceDiagnostic, string> format)
    {
        var uniqueDiagnostics = GetDistinct(diagnostics);
        var fileCount = uniqueDiagnostics.Select(GetFileKey).Distinct(StringComparer.Ordinal).Count();
        return $"{header(fileCount)}{Environment.NewLine}{string.Join(Environment.NewLine, uniqueDiagnostics.Select(diagnostic => "  " + format(diagnostic)))}";
    }

    internal static ImmutableArray<WorkspaceDiagnostic> GetDistinct(IEnumerable<WorkspaceDiagnostic> diagnostics)
        => diagnostics
            .GroupBy(diagnostic => (GetFileKey(diagnostic), diagnostic.Line, diagnostic.Column, diagnostic.Kind, diagnostic.Message))
            .Select(group => group.First())
            .ToImmutableArray();

    private static string GetFileKey(WorkspaceDiagnostic diagnostic) => diagnostic.FilePath.Replace('\\', '/');
}
