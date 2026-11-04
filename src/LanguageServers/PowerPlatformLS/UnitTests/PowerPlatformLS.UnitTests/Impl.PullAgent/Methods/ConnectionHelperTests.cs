namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class ConnectionHelperTests
    {
        private const string EnvironmentId = "TestEnvironment";
        private const string DataverseUrl = "https://test.crm.dynamics.com";
        private const string AgentManagementUrl = "https://test.agentmanagement.com";

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ResolveDeploymentSyncInfo_ResolvesIdentityInSelectedEnvironmentOrFails(bool exists)
        {
            var agentId = exists ? Guid.NewGuid() : Guid.Empty;
            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse.Setup(client => client.GetAgentIdBySchemaNameAsync("agent_schema", It.IsAny<CancellationToken>()))
                .ReturnsAsync(agentId);
            var request = new ListAgentConnectionsRequest
            {
                WorkspaceUri = new Uri("file:///c:/workspace"),
                AccountInfo = new AccountInfo { TenantId = Guid.NewGuid() },
                EnvironmentInfo = new EnvironmentInfo
                {
                    EnvironmentId = "selected-environment",
                    DisplayName = "Selected Environment",
                    DataverseUrl = DataverseUrl,
                    AgentManagementUrl = AgentManagementUrl,
                },
                SolutionVersions = new SolutionInfo(),
                CopilotStudioAccessToken = "cs-token",
                DataverseAccessToken = "dv-token",
            };

            if (!exists)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ConnectionHelper.ResolveDeploymentSyncInfoAsync(request, "agent_schema", dataverse.Object, CancellationToken.None));
                return;
            }

            var target = await ConnectionHelper.ResolveDeploymentSyncInfoAsync(request, "agent_schema", dataverse.Object, CancellationToken.None);
            Assert.Equal(agentId, target.AgentId);
            Assert.Equal("selected-environment", target.EnvironmentId);
            Assert.Equal("Selected Environment", target.EnvironmentDisplayName);
            Assert.Equal(new Uri(DataverseUrl), target.DataverseEndpoint);
        }

        [Fact]
        public void BuildDefaultSyncInfoMapsRequestFieldsTest()
        {
            var accountInfo = new AccountInfo
            {
                AccountId = "account",
                TenantId = Guid.NewGuid(),
                AccountEmail = "email"
            };
            var solutionVersions = new SolutionInfo
            {
                CopilotStudioSolutionVersion = new Version(1, 0, 0, 0)
            };
            var request = new SyncAgentRequest
            {
                WorkspaceUri = new Uri("file:///c:/agent"),
                AccountInfo = accountInfo,
                EnvironmentInfo = new EnvironmentInfo
                {
                    DataverseUrl = DataverseUrl,
                    AgentManagementUrl = AgentManagementUrl,
                    EnvironmentId = EnvironmentId,
                    DisplayName = "Test Environment"
                },
                SolutionVersions = solutionVersions,
                CopilotStudioAccessToken = "cs-token",
                DataverseAccessToken = "dv-token"
            };

            var syncInfo = ConnectionHelper.BuildDefaultSyncInfo(request);

            Assert.Equal(Guid.Empty, syncInfo.AgentId);
            Assert.Equal(EnvironmentId, syncInfo.EnvironmentId);
            Assert.Equal(new Uri(DataverseUrl), syncInfo.DataverseEndpoint);
            Assert.Equal(new Uri(AgentManagementUrl), syncInfo.AgentManagementEndpoint);
            Assert.Same(accountInfo, syncInfo.AccountInfo);
            Assert.Same(solutionVersions, syncInfo.SolutionVersions);
        }

        [Fact]
        public async Task ProvisionConnectionsAsyncProvisionsConnectionReferencesWithTheConnectorsItPushedAndReturnsThePushResultTest()
        {
            var pushedRowIds = new Dictionary<string, Guid> { ["shared_contoso"] = Guid.NewGuid() };
            var pushResult = new CustomConnectorPushResult { PushedRowIds = pushedRowIds };
            var synchronizer = new Mock<IWorkspaceSynchronizer>();
            synchronizer
                .Setup(service => service.PushCustomConnectorsAsync(It.IsAny<DirectoryPath>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .ReturnsAsync(pushResult);
            var workspaceFolder = new DirectoryPath("c:/agent/");
            var definition = new BotDefinition();
            var dataverse = Mock.Of<ISyncDataverseClient>();

            var result = await ConnectionHelper.ProvisionConnectionsAsync(synchronizer.Object, workspaceFolder, definition, dataverse, CancellationToken.None);

            Assert.Same(pushResult, result);
            synchronizer.Verify(service => service.PushCustomConnectorsAsync(workspaceFolder, dataverse, It.IsAny<CancellationToken>(), false), Times.Once);
            synchronizer.Verify(service => service.ProvisionConnectionReferencesAsync(workspaceFolder, definition, dataverse, It.IsAny<CancellationToken>(), pushedRowIds), Times.Once);
        }
    }
}
