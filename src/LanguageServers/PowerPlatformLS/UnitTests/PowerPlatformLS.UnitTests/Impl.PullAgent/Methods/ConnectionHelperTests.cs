namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using System;
    using Xunit;

    public class ConnectionHelperTests
    {
        private const string EnvironmentId = "TestEnvironment";
        private const string DataverseUrl = "https://test.crm.dynamics.com";
        private const string AgentManagementUrl = "https://test.agentmanagement.com";

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
    }
}
