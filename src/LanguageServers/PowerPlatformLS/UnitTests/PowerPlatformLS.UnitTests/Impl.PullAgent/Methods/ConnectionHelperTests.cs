namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Moq;
    using System;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class ConnectionHelperTests
    {
        private const string EnvironmentId = "TestEnvironment";
        private const string DataverseUrl = "https://test.crm.dynamics.com";
        private const string AgentManagementUrl = "https://test.agentmanagement.com";

        [Fact]
        public async Task BindConnectionsAsyncBindsAllValidBindingsTest()
        {
            var dataverseClient = new MockDataverseClient();
            var bindings = ImmutableArray.Create(
                new ConnectionBindingInput
                {
                    ConnectionReferenceLogicalName = "cr_first",
                    ConnectionLogicalName = "connection-one",
                    ConnectionDisplayName = "Connection One"
                },
                new ConnectionBindingInput
                {
                    ConnectionReferenceLogicalName = "cr_second",
                    ConnectionLogicalName = "connection-two",
                    ConnectionDisplayName = "Connection Two"
                });

            await ConnectionHelper.BindConnectionsAsync(dataverseClient, bindings, new Mock<ILspLogger>().Object, CancellationToken.None);

            Assert.Equal(2, dataverseClient.BindConnectionReferenceCalls.Count);
            Assert.Equal("cr_first", dataverseClient.BindConnectionReferenceCalls[0].ConnectionReferenceLogicalName);
            Assert.Equal("connection-one", dataverseClient.BindConnectionReferenceCalls[0].ConnectionLogicalName);
            Assert.Equal("Connection One", dataverseClient.BindConnectionReferenceCalls[0].ConnectionDisplayName);
            Assert.Equal("cr_second", dataverseClient.BindConnectionReferenceCalls[1].ConnectionReferenceLogicalName);
        }

        [Fact]
        public async Task BindConnectionsAsyncSkipsBindingsWithBlankNamesTest()
        {
            var dataverseClient = new MockDataverseClient();
            var bindings = ImmutableArray.Create(
                new ConnectionBindingInput
                {
                    ConnectionReferenceLogicalName = "cr_valid",
                    ConnectionLogicalName = "connection-valid"
                },
                new ConnectionBindingInput
                {
                    ConnectionReferenceLogicalName = "cr_missing_connection",
                    ConnectionLogicalName = "   "
                },
                new ConnectionBindingInput
                {
                    ConnectionReferenceLogicalName = string.Empty,
                    ConnectionLogicalName = "connection-missing-ref"
                });

            await ConnectionHelper.BindConnectionsAsync(dataverseClient, bindings, new Mock<ILspLogger>().Object, CancellationToken.None);

            Assert.Single(dataverseClient.BindConnectionReferenceCalls);
            Assert.Equal("cr_valid", dataverseClient.BindConnectionReferenceCalls[0].ConnectionReferenceLogicalName);
        }

        [Fact]
        public async Task BindConnectionsAsyncWithEmptyArrayIsNoOpTest()
        {
            var dataverseClient = new MockDataverseClient();

            await ConnectionHelper.BindConnectionsAsync(dataverseClient, ImmutableArray<ConnectionBindingInput>.Empty, new Mock<ILspLogger>().Object, CancellationToken.None);
            await ConnectionHelper.BindConnectionsAsync(dataverseClient, default, new Mock<ILspLogger>().Object, CancellationToken.None);

            Assert.Empty(dataverseClient.BindConnectionReferenceCalls);
        }

        [Fact]
        public async Task BindConnectionsAsyncBindFailureThrowsRedactedExceptionAndLogsSensitiveTest()
        {
            var logger = new Mock<ILspLogger>();
            var bindings = ImmutableArray.Create(
                new ConnectionBindingInput
                {
                    ConnectionReferenceLogicalName = "cr_secretref",
                    ConnectionLogicalName = "secret-connection"
                });

            var exception = await Assert.ThrowsAsync<ConnectionBindingException>(() =>
                ConnectionHelper.BindConnectionsAsync(new MockDataverseClientThrowingBind(), bindings, logger.Object, CancellationToken.None));

            Assert.Contains("Failed to bind a connection reference", exception.Message);
            Assert.DoesNotContain("cr_secretref", exception.Message);
            Assert.NotNull(exception.InnerException);
            logger.Verify(
                l => l.LogSensitiveInformation(
                    It.Is<string>(s => s.Contains("cr_secretref")),
                    It.Is<string>(s => !s.Contains("cr_secretref"))),
                Times.Once);
        }

        [Fact]
        public async Task BindConnectionsAsyncCancellationIsNotWrappedTest()
        {
            var dataverseClient = new Mock<ISyncDataverseClient>();
            dataverseClient
                .Setup(c => c.BindConnectionReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ThrowsAsync(new OperationCanceledException());
            var bindings = ImmutableArray.Create(
                new ConnectionBindingInput
                {
                    ConnectionReferenceLogicalName = "cr_ref",
                    ConnectionLogicalName = "connection"
                });

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                ConnectionHelper.BindConnectionsAsync(dataverseClient.Object, bindings, new Mock<ILspLogger>().Object, CancellationToken.None));
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
            var request = new PreparePushRequest
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
