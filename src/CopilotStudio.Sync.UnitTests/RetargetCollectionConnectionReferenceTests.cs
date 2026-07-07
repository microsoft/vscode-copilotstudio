// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class RetargetCollectionConnectionReferenceTests
{
    private const string UnusedLogicalName = "cr_test.shared_office365users.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static ConnectionReference MakeConnectionRef(string logicalName, string connectorId) => new ConnectionReference.Builder
    {
        ConnectionReferenceLogicalName = logicalName,
        ConnectorId = connectorId,
    }.Build();

    private static Mock<ISyncDataverseClient> CreateEmptyRemoteDataverse()
    {
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        mockDataverse
            .Setup(client => client.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionReferenceInfo>());
        return mockDataverse;
    }

    [Fact]
    public async Task Pull_Collection_DropsConnectionReferenceNotUsedByAnyComponent()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/cc-connref-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        var collection = CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_cctest\ndisplayName: CCTest")!;
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotComponentCollectionDefinition()
            .WithComponentCollection(collection)
            .WithConnectionReferences(new[] { MakeConnectionRef(UnusedLogicalName, "/providers/Microsoft.PowerApps/apis/shared_office365users") }));

        var previousDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        Assert.IsType<BotComponentCollectionDefinition>(previousDefinition);
        Assert.Contains(previousDefinition.ConnectionReferences, reference => reference.ConnectionReferenceLogicalName.Value == UnusedLogicalName);

        mockIsland
            .Setup(island => island.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, null, "token-1"));

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previousDefinition, CreateEmptyRemoteDataverse().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.False(fileAccessor.Exists(new AgentFilePath("connectionreferences.mcs.yml")));
    }

    [Fact]
    public async Task Pull_Agent_KeepsDeclaredConnectionReferenceEvenWhenUnused()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/agent-connref-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr_test")!;
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition()
            .WithEntity(entity)
            .WithConnectionReferences(new[] { MakeConnectionRef(UnusedLogicalName, "/providers/Microsoft.PowerApps/apis/shared_office365users") }));

        var previousDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        Assert.IsType<BotDefinition>(previousDefinition);

        mockIsland
            .Setup(island => island.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, entity, "token-1"));

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previousDefinition, CreateEmptyRemoteDataverse().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.True(fileAccessor.Exists(new AgentFilePath("connectionreferences.mcs.yml")));
    }
}
