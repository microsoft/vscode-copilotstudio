namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.DependencyInjection;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Microsoft.CopilotStudio.McsCore;
    using IFileAccessorFactory = Microsoft.CopilotStudio.McsCore.IFileAccessorFactory;

    public class CloneAgentTests
    {
        private const string TestEnvironment = "testEnvironment";
        private const string TestAccount = "testAccount";
        private const string AccountEmail = "testAccount@contoso.com";
        private const string AgentManagementHost = "https://test.agentmanagement.com";
        private const string DataverseEndpoint = "https://test.crm.dynamics.com";
        private const int MinorVersion = 1001234;
        private const string CopilotStudioAccessToken = "testCopilotStudioAccessToken";
        private const string DataverseAccessToken = "testDataverseAccessToken";

        [Fact]
        public async Task Success_OnCloneAgent_Async()
        {
            var pullMockModule = new PullAgentMockModule();
            await using var context = new TestHost([new McsLspModule(), new PullAgentLspModule(new BuildVersionInfo { VsixVersion = "MCSVSCode-1.0.0", Hash = "pullAgentLsp" }), pullMockModule]);
            await context.InitializeLanguageServerAsync();

            var tenantId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var cloneParams = CreateRequest(agentId, tenantId);
            var cloneRequest = JsonRpc.CreateRequestMessage(CloneAgentRequest.MessageName, cloneParams);
            context.TestStream.WriteMessage(cloneRequest);

            var response = await context.GetResponseAsync();
            var cloneResponse = JsonRpc.GetValidResult<CloneAgentResponse>(response as JsonRpcResponse);

            // assert API response
            Assert.True(200 == cloneResponse.Code, $"cloneResponse should have success status Code (200). Got '{cloneResponse.Code}' with message: '{cloneResponse.Message}'");

            // assert files
            TestAssert.StringArrayEqual([".mcs/.gitignore", ".mcs/botdefinition.json", ".mcs/changetoken.txt", ".mcs/conn.json", "agent.mcs.yml"], pullMockModule.DiskMock.Filenames);
            await AssertFileContentAsync(".mcs/.gitignore", "*", pullMockModule.DiskMock);
            await AssertFileContentAsync(".mcs/botdefinition.json", "{\"$kind\":\"BotDefinition\"}", pullMockModule.DiskMock);
            await AssertFileContentAsync(".mcs/changetoken.txt", "TestHttpMethodHandler change token", pullMockModule.DiskMock);
            await AssertFileContentAsync(".mcs/conn.json", $"{{\"DataverseEndpoint\":\"{DataverseEndpoint}\",\"EnvironmentId\":\"{TestEnvironment}\",\"AccountInfo\":{{\"AccountId\":\"{TestAccount}\",\"TenantId\":\"{tenantId}\",\"AccountEmail\":\"{AccountEmail}\",\"clusterCategory\":null}},\"AgentId\":\"{agentId}\",\"ComponentCollectionId\":null,\"SolutionVersions\":{{\"SolutionVersions\":{{\"msdyn_RelevanceSearch\":\"0.{MinorVersion}\",\"msft_AIPlatformExtensionsComponents\":\"0.{MinorVersion}\"}},\"CopilotStudioSolutionVersion\":\"0.{MinorVersion}\"}},\"AgentManagementEndpoint\":\"{AgentManagementHost}\"}}", pullMockModule.DiskMock);

            // assert network calls sequence
            Assert.NotEmpty(pullMockModule.HttpClientMock.Requests);
            var expectedUrl = $"{AgentManagementHost}/api/botmanagement/v1/environments/{TestEnvironment}/bots/{agentId}/content/botcomponents";
            var httpRequest = pullMockModule.HttpClientMock.Requests.Single(r => r.RequestUri?.AbsoluteUri == expectedUrl);
            Assert.Equal(expectedUrl, httpRequest.RequestUri?.AbsoluteUri);

            // assert external dependency
            Assert.NotEmpty(pullMockModule.ContentAuthoringMock.GetComponentsRequests);
            var componentsRequest = pullMockModule.ContentAuthoringMock.GetComponentsRequests.First();
            var reqContext = componentsRequest.operationContext;
            Assert.Equal(TestEnvironment, reqContext.EnvironmentId);
            Assert.Equal(tenantId, reqContext.OrganizationInfo.TenantId);
            Assert.Equal(DataverseEndpoint, reqContext.OrganizationInfo.CdsEndpoint.OriginalString);
            Assert.Equal(new Version(0, MinorVersion), reqContext.OrganizationInfo.PvaSolutionVersion);

            // assert on access tokens: mock on http client cause mocked request to miss those
            Assert.Equal(DataverseAccessToken, pullMockModule.TokenProvider?.GetDataverseToken());
            Assert.Equal(CopilotStudioAccessToken, pullMockModule.TokenProvider?.GetCopilotStudioToken());
        }

        [Fact]
        public void CloneAgentRequestSerializationTest()
        {
            var tenantId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var cloneAgentRequest = CreateRequest(agentId, tenantId);

            var json = JsonSerializer.Serialize(cloneAgentRequest, new JsonSerializerOptions { WriteIndented = true });
            var deserialized = JsonSerializer.Deserialize<CloneAgentRequest>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(cloneAgentRequest.AgentInfo.AgentId, deserialized!.AgentInfo.AgentId);
            Assert.Equal(cloneAgentRequest.Assets.CloneAgent, deserialized.Assets.CloneAgent);
            Assert.Equal(cloneAgentRequest.RootFolder, deserialized.RootFolder);
            Assert.Equal(cloneAgentRequest.EnvironmentInfo.DataverseUrl, deserialized.EnvironmentInfo.DataverseUrl);
        }

        private async Task AssertFileContentAsync(string filepath, string expectedContent, InMemoryFileWriter diskMock)
        {
            var actualContent = await diskMock.ReadStringAsync(new AgentFilePath(filepath), CancellationToken.None);
            Assert.True(expectedContent == actualContent, $"{filepath} has unexpected content.\nexpected=\n{expectedContent}\nactual=\n{actualContent}");
        }

        private static CloneAgentRequest CreateRequest(Guid agentId, Guid tenantId)
        {
            return new CloneAgentRequest
            {
                // $$$ make this Z:/ to catch bugs where we actually do write to the drive and skip our file abstraction.
                RootFolder = new Uri("file:///c:/test"), 
                AccountInfo = new AccountInfo
                {
                    AccountId = TestAccount,
                    TenantId = tenantId,
                    AccountEmail = AccountEmail
                },
                EnvironmentInfo = new EnvironmentInfo
                {
                    DataverseUrl = DataverseEndpoint,
                    AgentManagementUrl = AgentManagementHost,
                    EnvironmentId = TestEnvironment,
                    DisplayName = "Test Environment",
                },
                SolutionVersions = new SolutionInfo
                {
                    SolutionVersions = new Dictionary<string, Version>
                    {
                        { "msdyn_RelevanceSearch", new Version(0, MinorVersion) },
                        { "msft_AIPlatformExtensionsComponents", new Version(0, MinorVersion) },
                    },
                    CopilotStudioSolutionVersion = new Version(0, MinorVersion),
                },
                AgentInfo = new AgentInfo
                {
                    AgentId = agentId,
                    DisplayName = "Test Agent",
                },
                Assets = new AssetsToClone
                {
                    CloneAgent = true,
                },
                CopilotStudioAccessToken = CopilotStudioAccessToken,
                DataverseAccessToken = DataverseAccessToken,
            };
        }

        // Mock for setting the IIslandControlPlaneService used for cloning. 
        private class MockModuleForClone : ILspModule
        {
            public InMemoryFileAccessorFactory DiskMock { get; } = new();
            public required IIslandControlPlaneService MockIsland { get; init; }

            public void ConfigureServices(IServiceCollection services)
            {
                // prevent disk access during tests (both PullAgent and CopilotStudio.Sync interfaces)
                services.RemoveAll<IFileAccessorFactory>();
                services.AddSingleton<IFileAccessorFactory>(DiskMock);
                services.RemoveAll<Microsoft.CopilotStudio.McsCore.IFileAccessorFactory>();
                services.AddSingleton<Microsoft.CopilotStudio.McsCore.IFileAccessorFactory>(DiskMock);

                // Point island for getting changesets.
                // By injecting the island, we can skip injecting http services or other auth.
                services.RemoveAll<IIslandControlPlaneService>();
                services.AddSingleton<IIslandControlPlaneService>(MockIsland);
            }
        }

        // Clone a Component Collection 
        [Fact]
        public async Task Success_OnCloneAgent_With_CC_Async()
        {
            // Use an existing workspace directory from our tests to produce the changesets
            // hande back by clone. 
            var dir = Path.GetFullPath(Path.Combine("TestData", "WorkspaceWithCC"));
            World worldToClone = new World(dir);

            var pullMockModule = new MockModuleForClone
            {
                MockIsland = worldToClone.GetControlIsland("Agent 111", "MyCC333")
            };
            var agentId = worldToClone.GetCdsId("Agent 111");
            var componentCollectionId = worldToClone.GetCdsId("MyCC333");

            await using var context = new TestHost([new McsLspModule(), new PullAgentLspModule(new BuildVersionInfo { VsixVersion = "1.0.0-test", Hash = "pullAgentLsp" }), pullMockModule]);
            await context.InitializeLanguageServerAsync();

            var tenantId = Guid.NewGuid();
            
            // Create clone request. Specify to include a component collection. 
            var cloneParams = CreateRequest(agentId, tenantId);
                        
            cloneParams.AgentInfo.ComponentCollections = new List<ComponentCollectionInfo>
            {
                new ComponentCollectionInfo()
                {
                     Id = componentCollectionId,
                     DisplayName = "MyCC_333",
                     SchemaName = "bot_componentcollection_my_cc_333"
                }
            };
            cloneParams.Assets = new AssetsToClone
            {
                CloneAgent = true,
                ComponentCollectionIds = [componentCollectionId]
            };

            var cloneRequest = JsonRpc.CreateRequestMessage(CloneAgentRequest.MessageName, cloneParams);
            context.TestStream.WriteMessage(cloneRequest);

            var response = await context.GetResponseAsync();
            var cloneResponse = JsonRpc.GetValidResult<CloneAgentResponse>(response as JsonRpcResponse);

            // Clone should have created 2 workspaces. 

            // assert API response
            Assert.True(200 == cloneResponse.Code, $"cloneResponse should have success status Code (200). Got '{cloneResponse.Code}' with message: '{cloneResponse.Message}'");


            var disk = pullMockModule.DiskMock;

            var workspacesFiles = disk.Writers.OrderBy(x => x.Key.ToString()).ToArray();

            Assert.Equal(2, workspacesFiles.Length);

            // Root was sent in clone request.
            Assert.Equal("c:/test/MyCC_333/", workspacesFiles[0].Key.ToString());
            Assert.Equal("c:/test/Test Agent/", workspacesFiles[1].Key.ToString());

            // Notably:
            Assert.Equal([
                ".mcs/.gitignore",
                ".mcs/botdefinition.json",
                ".mcs/changetoken.txt",
                ".mcs/conn.json",
                "collection.mcs.yml", // import for CC 
                "topics/cr924_agentgkVyK-.topic.CC_Topic2.mcs.yml", // not truncates since prefix is different than CC's 
                "topics/cr924_agentMXECGF.topic.CC_Topic1.mcs.yml"],
                workspacesFiles[0].Value.Filenames);

            Assert.Equal([
                    ".mcs/.gitignore",
                    ".mcs/botdefinition.json",
                    ".mcs/changetoken.txt",
                    ".mcs/conn.json",
                    "agent.mcs.yml", // important for agent 
                    "icon.png",
                    "references.mcs.yml", // Important since we ref to another workspace 
                    "settings.mcs.yml",
                    "topics/ConversationStart.mcs.yml",
                    "topics/EndofConversation.mcs.yml",
                    "topics/Escalate.mcs.yml",
                    "topics/Fallback.mcs.yml",
                    "topics/Goodbye.mcs.yml",
                    "topics/Greeting.mcs.yml",
                    "topics/MultipleTopicsMatched.mcs.yml",
                    "topics/OnError.mcs.yml",
                    "topics/ResetConversation.mcs.yml",
                    "topics/Search.mcs.yml",
                    "topics/Signin.mcs.yml",
                    "topics/StartOver.mcs.yml",
                    "topics/ThankYou.mcs.yml"
                ],
                workspacesFiles[1].Value.Filenames);

            // Compare content.
            // Key thing is this has touched up the references ot the local dir. 
            var refContents = await workspacesFiles[1].Value.ReadStringAsync(new AgentFilePath("references.mcs.yml"), default);
            Assert.Equal("componentCollections:\n  - schemaName:\n    directory: ../MyCC_333/", refContents.ReplaceLineEndings("\n"));

            Assert.NotNull(context); // prevent dispose
        }
    }
}
