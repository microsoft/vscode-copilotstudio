// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Thrown when a workspace cannot be synchronized because one or more files are invalid or unresolved.</summary>
public sealed class WorkspaceValidationException : Exception
{
    /// <summary>Creates a validation failure describing every problem found in the workspace.</summary>
    public WorkspaceValidationException(ImmutableArray<WorkspaceDiagnostic> diagnostics) : base(BuildMessage(diagnostics))
    {
        Diagnostics = WorkspaceDiagnostic.GetDistinct(diagnostics);
    }

    /// <summary>The problems that prevented the operation.</summary>
    public ImmutableArray<WorkspaceDiagnostic> Diagnostics { get; }

    /// <summary>Builds the summary used for <see cref="Exception.Message"/>, formatting each diagnostic with <paramref name="format"/>.</summary>
    public static string Describe(ImmutableArray<WorkspaceDiagnostic> diagnostics, Func<WorkspaceDiagnostic, string> format)
        => WorkspaceDiagnostic.Summarize(HaveOnlyKind(diagnostics, WorkspaceDiagnosticKind.MissingDependency) ? DescribeMissingDependencyHeader : HaveOnlyKind(diagnostics, WorkspaceDiagnosticKind.ManagedDependency) ? DescribeManagedDependencyHeader : HaveOnlyKind(diagnostics, WorkspaceDiagnosticKind.UnapprovedOverwrite) ? DescribeUnapprovedOverwriteHeader : DescribeUnreadableFileHeader, diagnostics, format);

    private static bool HaveOnlyKind(ImmutableArray<WorkspaceDiagnostic> diagnostics, WorkspaceDiagnosticKind kind) => !diagnostics.IsDefaultOrEmpty && diagnostics.All(diagnostic => diagnostic.Kind == kind);

    private static string DescribeUnreadableFileHeader(int count) => $"{count} workspace {(count == 1 ? "file" : "files")} could not be read:";

    private static string DescribeMissingDependencyHeader(int count) => $"{count} workspace {(count == 1 ? "file uses" : "files use")} environment variables that are missing in the target environment:";

    private static string DescribeManagedDependencyHeader(int count) => $"{count} workspace {(count == 1 ? "file changes an environment variable that is" : "files change environment variables that are")} managed in the target environment:";

    private static string DescribeUnapprovedOverwriteHeader(int count) => $"{count} workspace {(count == 1 ? "file changes an environment variable that is" : "files change environment variables that are")} not approved for overwrite in the target environment:";

    private static string BuildMessage(ImmutableArray<WorkspaceDiagnostic> diagnostics) => Describe(diagnostics, static diagnostic => diagnostic.ToString());
}
