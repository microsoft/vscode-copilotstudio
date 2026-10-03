// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Classifies why a workspace file could not be synchronized.</summary>
public enum WorkspaceDiagnosticKind
{
    /// <summary>The file is malformed or could not be read.</summary>
    InvalidFile,

    /// <summary>The file still contains unresolved merge conflict markers.</summary>
    MergeConflict,
}
