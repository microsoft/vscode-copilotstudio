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
        => WorkspaceDiagnostic.Summarize(count => $"{count} workspace {(count == 1 ? "file" : "files")} could not be read:", diagnostics, format);

    private static string BuildMessage(ImmutableArray<WorkspaceDiagnostic> diagnostics) => Describe(diagnostics, static diagnostic => diagnostic.ToString());
}
