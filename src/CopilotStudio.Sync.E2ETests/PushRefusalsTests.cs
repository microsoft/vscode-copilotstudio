// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.TestHarness;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// Tests offline, without a tenant, that the harness stops a push the environment partly refused
/// before it reconciles the workspace with the cloud copies.
/// </summary>
public sealed class PushRefusalsTests
{
    [Fact]
    public void ThrowIfAny_AnAIPromptWasNotPublished_StopsNamingOnlyTheRefusedPrompts()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PushRefusals.ThrowIfAny(new[]
        {
            new SyncDataverseClient.AIPromptResponse { PromptName = "Summarize", ErrorMessage = string.Empty },
            new SyncDataverseClient.AIPromptResponse { PromptName = "Translate", ErrorMessage = "It changed in the cloud since it was last synced." },
            new SyncDataverseClient.AIPromptResponse { PromptName = "Classify", ErrorMessage = "invalid model" },
        }));

        Assert.Equal("Failed to push AI prompt 'Translate': It changed in the cloud since it was last synced. Failed to push AI prompt 'Classify': invalid model", exception.Message);
    }

    [Fact]
    public void ThrowIfAny_ACustomConnectorWasNotSaved_StopsWithEachReason()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PushRefusals.ThrowIfAny(new CustomConnectorPushResult
        {
            Failed = new[] { new Change { Name = "Contoso" } },
            FailureReasons = new[] { "Failed to push custom connector 'Contoso': Bad request." },
        }));

        Assert.Equal("Failed to push custom connector 'Contoso': Bad request.", exception.Message);
    }

    [Fact]
    public void ThrowIfAny_EverythingWasSaved_LetsThePushContinue()
    {
        PushRefusals.ThrowIfAny(new CustomConnectorPushResult());
        PushRefusals.ThrowIfAny(new[] { new SyncDataverseClient.AIPromptResponse { PromptName = "Summarize", ErrorMessage = string.Empty } });
    }
}
