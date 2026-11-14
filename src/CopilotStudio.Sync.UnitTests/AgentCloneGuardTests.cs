// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentCloneGuardTests
{
    [Fact]
    public async Task CloneAllAssets_ManagedAgent_RefusesBeforeWritingAnyWorkspace()
    {
        var (synchronizer, factory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var rootFolder = new DirectoryPath($"c:/test/managed-agent-{Guid.NewGuid():N}/");
        var agentInfo = new AgentInfo { AgentId = Guid.NewGuid(), DisplayName = "Payroll Agent", IsManaged = true };

        var failure = await Assert.ThrowsAsync<AgentIsManagedException>(() => synchronizer.CloneAllAssetsAsync(rootFolder, new AgentSyncInfo { AgentId = agentInfo.AgentId }, new AssetsToClone { CloneAgent = true }, agentInfo, new Mock<IOperationContextProvider>(MockBehavior.Strict).Object, new Mock<ISyncDataverseClient>(MockBehavior.Strict).Object, CancellationToken.None));

        Assert.Equal(agentInfo.AgentId, failure.AgentId);
        Assert.Equal("Payroll Agent", failure.AgentName);
        Assert.Equal($"The agent 'Payroll Agent' ({agentInfo.AgentId}) is managed. A managed agent changes only through the solution that installed it, so it cannot be cloned. Clone it from the environment where it is unmanaged.", failure.Message);
        Assert.Empty(factory.Create(rootFolder).ListFiles());
        island.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CloneAllAssets_CollectionOnlyCloneOfAManagedAgent_ClonesTheCollection()
    {
        var (synchronizer, factory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var rootFolder = new DirectoryPath($"c:/test/managed-agent-{Guid.NewGuid():N}/");
        var collectionId = Guid.NewGuid();
        var operationContextProvider = new Mock<IOperationContextProvider>();
        operationContextProvider.Setup(provider => provider.GetAllAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<AssetsToClone>())).ReturnsAsync(ImmutableArray.Create<AuthoringOperationContextBase>(new BotComponentCollectionAuthoringOperationContext(impersonatedUser: null, organizationInfo: new CdsOrganizationInfo(), reference: new BotComponentCollectionReference(environmentId: "environment-id", cdsId: collectionId), solutionUniqueName: null)));
        EnvironmentVariableTestFixtures.SetupRemoteChanges(island, new PvaComponentChangeSet(botComponentChanges: null, connectorDefinitionChanges: null, environmentVariableChanges: null, connectionReferenceChanges: null, aIPluginOperationChanges: null, componentCollectionChanges: [new BotComponentCollectionInsert(new BotComponentCollection(schemaName: "cr123_payrolltopics"))], dataverseTableSearchChanges: null, dataverseTableSearchEntityConfigurationChanges: null, connectedAgentDefinitionChanges: null, bot: null, changeToken: "token-1"));

        var workspace = Assert.Single(await synchronizer.CloneAllAssetsAsync(rootFolder, new AgentSyncInfo { AgentId = Guid.NewGuid() }, new AssetsToClone { ComponentCollectionIds = [collectionId] }, new AgentInfo { AgentId = Guid.NewGuid(), DisplayName = "Payroll Agent", IsManaged = true, ComponentCollections = [new ComponentCollectionInfo { Id = collectionId, DisplayName = "Payroll Topics" }] }, operationContextProvider.Object, EnvironmentVariableTestFixtures.CreateDataverse().Dataverse.Object, CancellationToken.None));

        Assert.Equal(rootFolder.GetChildDirectoryPath("Payroll Topics"), workspace);
        Assert.True(factory.Create(workspace).Exists(new AgentFilePath("collection.mcs.yml")));
    }
}
