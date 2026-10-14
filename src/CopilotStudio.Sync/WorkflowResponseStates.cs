// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Reads the activation outcome the service reported for each workflow.</summary>
public static class WorkflowResponseStates
{
    /// <summary>Whether one workflow ended up running.</summary>
    /// <param name="response">What the service returned for it.</param>
    /// <returns><see langword="true"/> when the service stored it as active.</returns>
    public static bool IsActivated(WorkflowResponse response) => string.IsNullOrEmpty(response.ErrorMessage) && !response.IsDisabled;

    /// <summary>Projects every response to its observed state.</summary>
    /// <param name="responses">What the service returned for the run.</param>
    /// <returns>Workflow name to whether it ended up running, omitting the ones it cannot attribute.</returns>
    public static IReadOnlyDictionary<string, bool> Observed(ImmutableArray<WorkflowResponse> responses) =>
        Unambiguous(responses
            .Where(response => !string.IsNullOrWhiteSpace(response.WorkflowName) && string.IsNullOrEmpty(response.ErrorMessage))
            .Select(response => (response.WorkflowName, Running: IsActivated(response))));

    /// <summary>Projects every activation outcome to its observed state.</summary>
    /// <param name="workflows">What the service returned for the run.</param>
    /// <returns>Workflow name to whether it ended up running, omitting the ones it cannot attribute.</returns>
    public static IReadOnlyDictionary<string, bool> Observed(ImmutableArray<WorkflowStatusView> workflows) =>
        Unambiguous(workflows
            .Where(workflow => !string.IsNullOrWhiteSpace(workflow.DisplayName) && Settled(workflow.State))
            .Select(workflow => (workflow.DisplayName, Running: workflow.State == WorkflowState.Activated)));

    /// <summary>Projects every activation outcome to its observed state, keyed by workflow id.</summary>
    /// <param name="workflows">What the service returned for the run.</param>
    /// <returns>Workflow id to whether it ended up running.</returns>
    public static IReadOnlyDictionary<string, bool> ObservedById(ImmutableArray<WorkflowStatusView> workflows)
    {
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var workflow in workflows.Where(workflow => !string.IsNullOrWhiteSpace(workflow.WorkflowId) && Settled(workflow.State)))
        {
            states[workflow.WorkflowId] = workflow.State == WorkflowState.Activated;
        }

        return states;
    }

    private static Dictionary<string, bool> Unambiguous(IEnumerable<(string Name, bool Running)> observations)
    {
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, running) in observations)
        {
            if (states.ContainsKey(name))
            {
                ambiguous.Add(name);
            }
            else
            {
                states[name] = running;
            }
        }

        foreach (var name in ambiguous)
        {
            states.Remove(name);
        }

        return states;
    }

    private static bool Settled(WorkflowState state) => state is WorkflowState.Activated or WorkflowState.Draft;
}
