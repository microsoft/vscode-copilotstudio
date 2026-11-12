// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Classifies why a workspace file could not be synchronized.</summary>
public enum WorkspaceDiagnosticKind
{
    /// <summary>The file is malformed or could not be read.</summary>
    InvalidFile,

    /// <summary>The file still contains unresolved merge conflict markers.</summary>
    MergeConflict,

    /// <summary>The file references an environment variable that does not exist in the target environment.</summary>
    MissingDependency,

    /// <summary>The file changes an environment variable that is managed in the target environment.</summary>
    ManagedDependency,

    /// <summary>The file changes an environment variable that already exists in the target environment without an approved overwrite.</summary>
    UnapprovedOverwrite,
}
