// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>What asking an environment to start or stop a workflow established.</summary>
/// <param name="Changed">Whether the environment confirmed the state change.</param>
/// <param name="Refused">Whether the environment rejected the request outright.</param>
/// <param name="Failure">Why the state did not change, when it did not.</param>
public sealed record WorkflowStateOutcome(bool Changed, bool Refused, string? Failure)
{
    /// <summary>The environment accepted the change.</summary>
    public static readonly WorkflowStateOutcome Applied = new(true, false, null);

    /// <summary>The environment rejected the request, so the workflow is where it was.</summary>
    /// <param name="failure">Why the environment rejected it.</param>
    /// <returns>A refusal.</returns>
    public static WorkflowStateOutcome Refusal(string failure) => new(false, true, failure);

    /// <summary>The request neither clearly applied nor was clearly rejected.</summary>
    /// <param name="failure">What went wrong.</param>
    /// <returns>An unknown outcome.</returns>
    public static WorkflowStateOutcome Unknown(string failure) => new(false, false, failure);

    /// <summary>Whether the workflow's state is no longer known.</summary>
    public bool IsUnknown => !Changed && !Refused;
}
