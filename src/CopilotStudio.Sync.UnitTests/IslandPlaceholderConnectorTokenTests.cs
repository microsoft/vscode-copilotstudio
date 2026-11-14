// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.DeltaTokens;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class IslandPlaceholderConnectorTokenTests
{
    private const string DataverseConnectorId = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps";
    private const string Office365UsersConnectorId = "/providers/Microsoft.PowerApps/apis/shared_office365users";
    private const string TeamsConnectorId = "/providers/Microsoft.PowerApps/apis/shared_teams";
    private const long TeamsConnectorVersion = 639291081844730338;
    private const long DataverseConnectorVersion = 639299305600560213;
    private const string ListRecordsOperationId = "ListRecordsWithOrganization";

    private static readonly AgentFilePath ChangeTokenPath = new(".mcs/changetoken.txt");

    [Fact]
    public async Task GetComponentsAsync_TokenWithPlaceholderHostedConnectorVersions_AsksIslandForThoseConnectors()
    {
        var changeToken = CreateChangeToken(
            new ConnectorDefinitionVersionInfo(DataverseConnectorId, 0),
            new ConnectorDefinitionVersionInfo(Office365UsersConnectorId, 0),
            new ConnectorDefinitionVersionInfo(TeamsConnectorId, TeamsConnectorVersion));

        var (islandToken, dataverseToken) = await GetChangeTokensSentAsync(changeToken);

        Assert.NotNull(islandToken);
        var sentToken = DeltaToken.FromBase64String(islandToken);
        Assert.Equal(
            new[] { (TeamsConnectorId, TeamsConnectorVersion) },
            sentToken.HostedConnectorDefinitionVersions.Select(version => (version.ComponentId, version.VersionNumber)));
        Assert.Equal(changeToken, sentToken.WithHostedConnectors(DeltaToken.FromBase64String(changeToken).HostedConnectorDefinitionVersions.ToArray()).ToBase64String());
        Assert.Equal(changeToken, dataverseToken);
    }

    [Fact]
    public async Task GetComponentsAsync_TokenWithDownloadedHostedConnectorVersions_SendsTokenUnchanged()
    {
        var changeToken = CreateChangeToken(new ConnectorDefinitionVersionInfo(TeamsConnectorId, TeamsConnectorVersion));

        var (islandToken, dataverseToken) = await GetChangeTokensSentAsync(changeToken);

        Assert.Equal(changeToken, islandToken);
        Assert.Equal(changeToken, dataverseToken);
    }

    [Fact]
    public async Task GetComponentsAsync_UnreadableToken_SendsTokenUnchanged()
    {
        const string changeToken = "not-a-change-token";

        var (islandToken, dataverseToken) = await GetChangeTokensSentAsync(changeToken);

        Assert.Equal(changeToken, islandToken);
        Assert.Equal(changeToken, dataverseToken);
    }

    [Fact]
    public async Task PullExistingChangesAsync_WorkspaceClonedWithPlaceholderConnector_ReplacesPlaceholderWithDownloadedMetadataInOnePull()
    {
        var (pacSynchronizer, files, pacIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/placeholder-connector-{Guid.NewGuid():N}/");
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var dataverse = CreateDataverseClient();
        pacIsland
            .Setup(service => service.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, new BotEntity.Builder { SchemaName = new BotEntitySchemaName("cr123_agent") }.Build(), CreateChangeToken(new ConnectorDefinitionVersionInfo(DataverseConnectorId, 0)))
                .WithConnectorDefinitionChanges(new[] { new ConnectorDefinitionInsert(new ConnectorDefinition.Builder { ConnectorId = DataverseConnectorId }.Build()) }));
        await pacSynchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, dataverse.Object, syncInfo, CancellationToken.None);

        var island = new IslandStub(SendDataverseConnectorMetadataWhenTokenOmitsIt);
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), files, CreateIslandService(island, CreateContentAuthoringService()), new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var pulled = await synchronizer.PullExistingChangesAsync(workspace, operationContext, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), dataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = files.Create(workspace);
        AssertDownloadedDataverseConnector(pulled);
        AssertDownloadedDataverseConnector(WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor) ?? throw new Xunit.Sdk.XunitException("Expected the cloud cache to be written."));
        Assert.Equal(
            new[] { (DataverseConnectorId, DataverseConnectorVersion) },
            DeltaToken.FromBase64String(await fileAccessor.ReadStringAsync(ChangeTokenPath, CancellationToken.None)).HostedConnectorDefinitionVersions.Select(version => (version.ComponentId, version.VersionNumber)));
    }

    private static string CreateChangeToken(params ConnectorDefinitionVersionInfo[] hostedConnectorVersions)
        => DeltaToken.FromBot(BotDefinition.Empty, 42)
            .WithHostedConnectors(hostedConnectorVersions)
            .ToBase64String();

    private static async Task<(string? IslandToken, string? DataverseToken)> GetChangeTokensSentAsync(string changeToken)
    {
        var island = new IslandStub(_ => new PvaComponentChangeSet(null, null, "island-token"));
        string? dataverseToken = null;
        var service = CreateIslandService(island, CreateContentAuthoringService(token => dataverseToken = token));
        var operationContext = new AuthoringOperationContext(null, new CdsOrganizationInfo(), new BotReference("environment-id", Guid.NewGuid()), null, false);

        await service.GetComponentsAsync(operationContext, changeToken, CancellationToken.None);

        return (island.ComponentDeltaToken, dataverseToken);
    }

    private static IslandControlPlaneService CreateIslandService(HttpMessageHandler island, IContentAuthoringService contentAuthoringService)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(island, disposeHandler: false));
        var service = new IslandControlPlaneService(new Mock<ISyncAuthProvider>().Object, contentAuthoringService, isIslandPreauthorized: true, httpClientFactory.Object);
        service.SetConnectionContext("https://island.example.test/", CoreServicesClusterCategory.Prod);
        return service;
    }

    private static IContentAuthoringService CreateContentAuthoringService(Action<string?>? onChangeToken = null)
    {
        var contentAuthoringService = new Mock<IContentAuthoringService>();
        contentAuthoringService
            .Setup(service => service.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((AuthoringOperationContextBase _, string? token, bool _, bool _, CancellationToken _) => onChangeToken?.Invoke(token))
            .ReturnsAsync(new PvaComponentChangeSet(null, null, "dataverse-token"));
        return contentAuthoringService.Object;
    }

    private static Mock<ISyncDataverseClient> CreateDataverseClient()
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        dataverse
            .Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        return dataverse;
    }

    private static PvaComponentChangeSet SendDataverseConnectorMetadataWhenTokenOmitsIt(string? componentDeltaToken)
    {
        var token = DeltaToken.FromBase64String(componentDeltaToken ?? throw new Xunit.Sdk.XunitException("Expected Island to receive a change token."));
        if (token.HostedConnectorDefinitionVersions.Any(version => version.ComponentId == DataverseConnectorId))
        {
            return new PvaComponentChangeSet(null, null, componentDeltaToken);
        }

        var downloadedConnector = new ConnectorDefinition.Builder
        {
            ConnectorId = DataverseConnectorId,
            Version = DataverseConnectorVersion,
            Operations =
            {
                new ConnectorOperation.Builder
                {
                    OperationId = ListRecordsOperationId,
                    InputType = DataType.EmptyRecord,
                    OutputType = DataType.EmptyRecord,
                },
            },
        }.Build();

        return new PvaComponentChangeSet(null, null, token.WithHostedConnectors(token.HostedConnectorDefinitionVersions.Append(new ConnectorDefinitionVersionInfo(DataverseConnectorId, DataverseConnectorVersion)).ToArray()).ToBase64String())
            .WithConnectorDefinitionChanges(new[] { new ConnectorDefinitionInsert(downloadedConnector) });
    }

    private static void AssertDownloadedDataverseConnector(DefinitionBase definition)
    {
        var connector = Assert.Single(definition.ConnectorDefinitions);
        Assert.Equal(DataverseConnectorId, connector.ConnectorId.Value);
        Assert.Equal(DataverseConnectorVersion, connector.Version);
        Assert.Equal(new[] { ListRecordsOperationId }, connector.Operations.Select(operation => operation.OperationId.Value));
    }

    private sealed class IslandStub(Func<string?, PvaComponentChangeSet> respond) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Options = ElementSerializer.CreateOptions();

        public string? ComponentDeltaToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                ComponentDeltaToken = body.RootElement.EnumerateObject()
                    .Single(property => string.Equals(property.Name, "componentDeltaToken", StringComparison.OrdinalIgnoreCase))
                    .Value.GetString();
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(respond(ComponentDeltaToken), Options), Encoding.UTF8, "application/json"),
            };
        }
    }
}
