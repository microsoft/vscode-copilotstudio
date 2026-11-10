// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Names the states Dataverse reports for cloud flows.</summary>
public static class CopilotStudioStates
{
    /// <summary>The state name of a cloud flow that is not running, and the portal label of every flow that is neither running nor unknown.</summary>
    public const string Draft = "draft";

    /// <summary>The state name of a cloud flow that is running.</summary>
    public const string Activated = "activated";

    /// <summary>The state name of a cloud flow the service has suspended.</summary>
    public const string Suspended = "suspended";

    /// <summary>The state name and portal label of a cloud flow whose state code is missing or not one Dataverse defines.</summary>
    public const string Unknown = "unknown";

    /// <summary>The portal label of a cloud flow that is running.</summary>
    public const string Published = "published";

    /// <summary>Names a cloud flow's state from its workflow's state code.</summary>
    /// <param name="stateCode">The workflow's state code, or <see langword="null"/> when the service reported none.</param>
    public static string WorkflowStateName(int? stateCode) => WorkflowStateOf(stateCode) switch
    {
        WorkflowState.Draft => Draft,
        WorkflowState.Activated => Activated,
        WorkflowState.Suspended => Suspended,
        _ => Unknown,
    };

    /// <summary>Maps a cloud flow's state name onto the draft, published or unknown label the Copilot Studio portal shows.</summary>
    /// <param name="stateName">The flow's state name.</param>
    public static string WorkflowPortalLabel(string stateName) => stateName switch
    {
        Activated => Published,
        Unknown => Unknown,
        _ => Draft,
    };

    internal static WorkflowState WorkflowStateOf(int? stateCode) => stateCode switch
    {
        0 => WorkflowState.Draft,
        1 => WorkflowState.Activated,
        2 => WorkflowState.Suspended,
        _ => WorkflowState.Unknown,
    };
}
