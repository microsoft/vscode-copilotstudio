// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
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
}
