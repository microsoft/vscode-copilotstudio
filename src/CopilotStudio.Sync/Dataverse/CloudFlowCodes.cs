// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>The Dataverse codes that mark a workflow row as a cloud flow definition.</summary>
public static class CloudFlowCodes
{
    /// <summary>The <c>workflow.category</c> of a cloud flow.</summary>
    public const int Category = 5;

    /// <summary>The <c>workflow.type</c> of a definition, as opposed to an activation or a template.</summary>
    public const int Type = 1;
}
