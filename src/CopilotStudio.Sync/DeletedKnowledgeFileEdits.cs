// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Decides what a pull does with a locally edited knowledge file that the environment deleted.</summary>
public enum DeletedKnowledgeFileEdits
{
    /// <summary>Keeps the local files so the next push creates the knowledge file again, and stops the pull when a file can't be kept.</summary>
    Keep,

    /// <summary>Deletes the local files, like any other deletion in the environment.</summary>
    Discard,

    /// <summary>Stops the pull before it changes the workspace, so the user can choose.</summary>
    Refuse,
}
