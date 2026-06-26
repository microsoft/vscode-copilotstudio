namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.Collections.Immutable;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using DirectoryPath = Microsoft.CopilotStudio.McsCore.DirectoryPath;

    public class SyncPushHandlerTests
    {
        private const string TestDataPath = "TestData";
        private const string EnvironmentId = "TestEnvironment";
        private const string AccountId = "testAccount";
        private const string AccountEmail = "testEmail";
        private const string DataverseUrl = "https://test.crm.dynamics.com";
        private const string AgentManagementUrl = "https://test.agentmanagement.com";
        private const string SolutionName = "TestSolution";
        private const string CopilotStudioToken = "CopilotStudioToken";
        private const string DataverseToken = "DataverseToken";
        private const string TopicsPath = "topics/Goodbye.mcs.yml";

        [Fact]
        public async Task SyncPush_NonCliTemplate_ProceedsAsClassic()
        {
            // Issue #292: a classic agent created from a non-default gallery template (the
            // fixture's template: sdkagent-1.0.0) has no native CLI evidence, so it is
            // Classic/Supported. The push gate allows it and the synchronizer push runs - the
            // template is a template, not an authoring shape, and must not fail the agent closed.
            var (requestContext, request) = CreateSetup("Workspace/UnrecognizedTemplateWorkspace");
            var synchronizer = new PushTrackingSynchronizer();
            var handler = CreateHandler(new MockDataverseClient(), synchronizer);

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.True(synchronizer.PushAttempted);
        }

        [Fact]
        public async Task SyncPush_Retarget_DraftsConnectionReferenceWorkflows()
        {
            var (requestContext, request) = CreateSetup("Workspace/UnrecognizedTemplateWorkspace");
            request.DraftConnectionReferenceWorkflows = true;
            var synchronizer = new PushTrackingSynchronizer();
            var handler = CreateHandler(new MockDataverseClient(), synchronizer);

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal(WorkflowActivationMode.DraftWhenConnectionReferencesExist, synchronizer.CapturedActivationMode);
        }

        [Fact]
        public async Task SyncPush_DefaultPush_UsesDraftWhenConnectionsUnbound()
        {
            var (requestContext, request) = CreateSetup("Workspace/UnrecognizedTemplateWorkspace");
            var synchronizer = new PushTrackingSynchronizer();
            var handler = CreateHandler(new MockDataverseClient(), synchronizer);

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal(WorkflowActivationMode.DraftWhenConnectionsUnbound, synchronizer.CapturedActivationMode);
        }

        private (RequestContext, SyncAgentRequest) CreateSetup(string workspaceRelativePath)
        {
            var workspacePath = Path.GetFullPath(Path.Combine(TestDataPath, workspaceRelativePath));
            var world = new World(workspacePath);
            var doc = world.GetDocument(Path.Combine(workspacePath, TopicsPath));
            var requestContext = world.GetRequestContext(doc!, 0);

            var request = new SyncAgentRequest
            {
                WorkspaceUri = new Uri(workspacePath),
                AccountInfo = new AccountInfo
                {
                    AccountId = AccountId,
                    TenantId = Guid.NewGuid(),
                    AccountEmail = AccountEmail
                },
                EnvironmentInfo = new EnvironmentInfo
                {
                    DataverseUrl = DataverseUrl,
                    AgentManagementUrl = AgentManagementUrl,
                    EnvironmentId = EnvironmentId,
                    DisplayName = "Test Environment"
                },
                SolutionVersions = new SolutionInfo
                {
                    CopilotStudioSolutionVersion = new Version(1, 0, 0, 0)
                },
                CopilotStudioAccessToken = CopilotStudioToken,
                DataverseAccessToken = DataverseToken
            };

            return (requestContext, request);
        }

        private static SyncPushHandler CreateHandler(ISyncDataverseClient dataverseClient, IWorkspaceSynchronizer synchronizer)
        {
            var mockAuthProvider = new Mock<ISyncAuthProvider>();
            mockAuthProvider.Setup(a => a.AcquireTokenAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync("mock-token");
            var accessor = new LspDataverseHttpClientAccessor(mockAuthProvider.Object);

            return new SyncPushHandler(
                new Mock<IIslandControlPlaneService>().Object,
                synchronizer,
                new TestTokenManager(),
                dataverseClient,
                accessor,
                CreateOperationProvider(),
                new Mock<Microsoft.CommonLanguageServerProtocol.Framework.ILspLogger>().Object);
        }

        private static IOperationContextProvider CreateOperationProvider()
        {
            var orgInfo = new CdsOrganizationInfo(
                tenantId: Guid.NewGuid(),
                cdsEndpoint: new Uri(DataverseUrl),
                pvaSolutionVersion: new Version(1, 0, 0, 0),
                dvTableSearchGlossaryAndSynonymsSolutionVersion: new Version(1, 0, 0, 0),
                dvTableSearchSolutionVersion: new Version(1, 0, 0, 0));

            var reference = new BotComponentCollectionReference(
                environmentId: EnvironmentId,
                cdsId: Guid.NewGuid());

            return new TestOperationContextProvider(
                new BotComponentCollectionAuthoringOperationContext(
                    impersonatedUser: null,
                    organizationInfo: orgInfo,
                    reference: reference,
                    solutionUniqueName: SolutionName));
        }
    }

    internal sealed class PushTrackingSynchronizer : TestWorkspaceSynchronizer
    {
        public bool PushAttempted { get; private set; }

        public WorkflowActivationMode? CapturedActivationMode { get; private set; }

        public override Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)> UpsertWorkflowForAgentAsync(
            DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken, WorkflowActivationMode activationMode = WorkflowActivationMode.PreserveSavedState)
        {
            PushAttempted = true;
            CapturedActivationMode = activationMode;
            return base.UpsertWorkflowForAgentAsync(workspaceFolder, dataverseClient, agentId, cancellationToken, activationMode);
        }
    }
}
