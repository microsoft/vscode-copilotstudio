// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SyncWorkspacePromptDownloadTests
{
    private static Mock<ISyncDataverseClient> CreateDataverseMock()
    {
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        return mockDataverse;
    }

    [Fact]
    public async Task SyncWorkspaceAsync_WhenPromptMetadataEmptyAndNonDefault_DownloadsRemotePrompts()
    {
        var (synchronizer, _, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/sync-empty-prompts/");

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, null, "token-1"));

        var mockDataverse = CreateDataverseMock();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.SyncWorkspaceAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            changeToken: null,
            updateWorkspaceDirectory: false,
            mockDataverse.Object,
            syncInfo,
            cloudFlowMetadata: null,
            CancellationToken.None,
            ImmutableArray<AIPromptMetadata>.Empty,
            syncCustomConnectors: false);

        mockDataverse.Verify(
            x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncWorkspaceAsync_WhenPromptMetadataProvided_DoesNotDownloadRemotePrompts()
    {
        var (synchronizer, _, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/sync-provided-prompts/");

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, null, "token-1"));

        var mockDataverse = CreateDataverseMock();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        var provided = ImmutableArray.Create(new AIPromptMetadata { AIModelId = Guid.NewGuid(), Name = "MyPrompt" });

        await synchronizer.SyncWorkspaceAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            changeToken: null,
            updateWorkspaceDirectory: false,
            mockDataverse.Object,
            syncInfo,
            cloudFlowMetadata: null,
            CancellationToken.None,
            provided,
            syncCustomConnectors: false);

        mockDataverse.Verify(
            x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
