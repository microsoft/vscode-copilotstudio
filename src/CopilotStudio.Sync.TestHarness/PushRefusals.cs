// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.TestHarness;

/// <summary>Stops a push the environment partly refused before the workspace is reconciled with the cloud copies.</summary>
public static class PushRefusals
{
    /// <summary>Throws when any custom connector was not saved, naming each one and why.</summary>
    /// <param name="result">What the custom connector push saved and refused.</param>
    public static void ThrowIfAny(CustomConnectorPushResult result) => ThrowIfAnyReason(result.FailureReasons);

    /// <summary>Throws when any AI prompt was not published, naming each one and why.</summary>
    /// <param name="responses">The publish response of each changed AI prompt.</param>
    public static void ThrowIfAny(IEnumerable<SyncDataverseClient.AIPromptResponse> responses) => ThrowIfAnyReason(responses.Where(response => !string.IsNullOrEmpty(response.ErrorMessage)).Select(response => $"Failed to push AI prompt '{response.PromptName}': {response.ErrorMessage}").ToList());

    private static void ThrowIfAnyReason(IReadOnlyCollection<string> reasons)
    {
        if (reasons.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", reasons));
        }
    }
}
