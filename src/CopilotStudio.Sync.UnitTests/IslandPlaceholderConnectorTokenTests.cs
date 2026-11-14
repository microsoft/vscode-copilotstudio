// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.DeltaTokens;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class IslandPlaceholderConnectorTokenTests
{
    private const string DataverseConnectorId = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps";
    private const string Office365UsersConnectorId = "/providers/Microsoft.PowerApps/apis/shared_office365users";
    private const string TeamsConnectorId = "/providers/Microsoft.PowerApps/apis/shared_teams";
    private const long TeamsConnectorVersion = 639291081844730338;

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

    private static string CreateChangeToken(params ConnectorDefinitionVersionInfo[] hostedConnectorVersions)
        => DeltaToken.FromBot(BotDefinition.Empty, 42)
            .WithHostedConnectors(hostedConnectorVersions)
            .ToBase64String();

    private static async Task<(string? IslandToken, string? DataverseToken)> GetChangeTokensSentAsync(string changeToken)
    {
        var island = new IslandStub();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(island));

        string? dataverseToken = null;
        var contentAuthoringService = new Mock<IContentAuthoringService>();
        contentAuthoringService
            .Setup(service => service.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((AuthoringOperationContextBase _, string? token, bool _, bool _, CancellationToken _) => dataverseToken = token)
            .ReturnsAsync(new PvaComponentChangeSet(null, null, "dataverse-token"));

        var service = new IslandControlPlaneService(new Mock<ISyncAuthProvider>().Object, contentAuthoringService.Object, isIslandPreauthorized: true, httpClientFactory.Object);
        service.SetConnectionContext("https://island.example.test/", CoreServicesClusterCategory.Prod);
        var operationContext = new AuthoringOperationContext(null, new CdsOrganizationInfo(), new BotReference("environment-id", Guid.NewGuid()), null, false);

        await service.GetComponentsAsync(operationContext, changeToken, CancellationToken.None);

        return (island.ComponentDeltaToken, dataverseToken);
    }

    private sealed class IslandStub : HttpMessageHandler
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
                Content = new StringContent(JsonSerializer.Serialize(new PvaComponentChangeSet(null, null, "island-token"), Options), Encoding.UTF8, "application/json"),
            };
        }
    }
}
