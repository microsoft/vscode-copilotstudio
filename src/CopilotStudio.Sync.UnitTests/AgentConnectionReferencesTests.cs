using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentConnectionReferencesTests
{
    private static ConnectionReference MakeConnectionRef(string logicalName, string connectorId) =>
        new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = logicalName,
            ConnectorId = connectorId,
        }.Build();

    [Fact]
    public async Task GetAgentConnectionReferencesAsync_NoConnectionReferences_ReturnsEmpty()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var dataverse = new Mock<ISyncDataverseClient>();

        var result = await synchronizer.GetAgentConnectionReferencesAsync(
            new BotDefinition(), dataverse.Object, CancellationToken.None);

        Assert.Empty(result);
        dataverse.Verify(
            c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAgentConnectionReferencesAsync_AnnotatesBoundConnectionAndDeduplicates()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var refs = new List<ConnectionReference>
        {
            MakeConnectionRef("cr_bound", "/providers/Microsoft.PowerApps/apis/shared_office365"),
            MakeConnectionRef("cr_unbound", "/providers/Microsoft.PowerApps/apis/shared_sharepointonline"),
            MakeConnectionRef("cr_bound", "/providers/Microsoft.PowerApps/apis/shared_office365"),
        };
        var definition = new BotDefinition().WithConnectionReferences(refs);

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SyncDataverseClient.ConnectionReferenceInfo
                {
                    ConnectionReferenceLogicalName = "cr_bound",
                    ConnectionId = "connection-123",
                },
            });

        var result = await synchronizer.GetAgentConnectionReferencesAsync(
            definition, dataverse.Object, CancellationToken.None);

        Assert.Equal(2, result.Count);

        var bound = Assert.Single(result, r => r.ConnectionReferenceLogicalName == "cr_bound");
        Assert.Equal("connection-123", bound.BoundConnectionId);
        Assert.Equal("shared_office365", bound.ConnectorName);

        var unbound = Assert.Single(result, r => r.ConnectionReferenceLogicalName == "cr_unbound");
        Assert.Equal(string.Empty, unbound.BoundConnectionId);
        Assert.Equal("shared_sharepointonline", unbound.ConnectorName);
    }

    [Fact]
    public async Task GetAgentConnectionReferencesAsync_SharedConnector_DoesNotQueryConnectorPrefix()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var definition = new BotDefinition().WithConnectionReferences(new List<ConnectionReference>
        {
            MakeConnectionRef("cr_shared", "/providers/Microsoft.PowerApps/apis/shared_office365"),
        });

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());

        var result = await synchronizer.GetAgentConnectionReferencesAsync(
            definition, dataverse.Object, CancellationToken.None);

        var single = Assert.Single(result);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_office365", single.ConnectorId);
        Assert.Equal("shared_office365", single.ConnectorName);
        dataverse.Verify(
            c => c.GetConnectorsByInternalIdPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAgentConnectionReferencesAsync_EnvironmentSpecificConnector_NoExactMatch_RewritesToNewest()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        const string originalConnectorId = "/providers/Microsoft.PowerApps/apis/cr1a2_myconnector-5f1234567890abcdef";
        var definition = new BotDefinition().WithConnectionReferences(new List<ConnectionReference>
        {
            MakeConnectionRef("cr_custom", originalConnectorId),
        });

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());
        dataverse
            .Setup(c => c.GetConnectorsByInternalIdPrefixAsync("cr1a2_myconnector-5f", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new CustomConnectorMetadata { ConnectorInternalId = "cr1a2_myconnector-5faaaaaaaaaaaaaaaa", ModifiedOn = new DateTime(2024, 1, 1) },
                new CustomConnectorMetadata { ConnectorInternalId = "cr1a2_myconnector-5fbbbbbbbbbbbbbbbb", ModifiedOn = new DateTime(2024, 6, 1) },
            });

        var result = await synchronizer.GetAgentConnectionReferencesAsync(
            definition, dataverse.Object, CancellationToken.None);

        var single = Assert.Single(result);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/cr1a2_myconnector-5fbbbbbbbbbbbbbbbb", single.ConnectorId);
        Assert.Equal("cr1a2_myconnector-5fbbbbbbbbbbbbbbbb", single.ConnectorName);
    }

    [Fact]
    public async Task GetAgentConnectionReferencesAsync_EnvironmentSpecificConnector_ExactMatch_KeepsOriginal()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        const string originalConnectorId = "/providers/Microsoft.PowerApps/apis/cr1a2_myconnector-5f1234567890abcdef";
        var definition = new BotDefinition().WithConnectionReferences(new List<ConnectionReference>
        {
            MakeConnectionRef("cr_custom", originalConnectorId),
        });

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());
        dataverse
            .Setup(c => c.GetConnectorsByInternalIdPrefixAsync("cr1a2_myconnector-5f", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new CustomConnectorMetadata { ConnectorInternalId = "cr1a2_myconnector-5f1234567890abcdef", ModifiedOn = new DateTime(2024, 1, 1) },
                new CustomConnectorMetadata { ConnectorInternalId = "cr1a2_myconnector-5fbbbbbbbbbbbbbbbb", ModifiedOn = new DateTime(2024, 6, 1) },
            });

        var result = await synchronizer.GetAgentConnectionReferencesAsync(
            definition, dataverse.Object, CancellationToken.None);

        var single = Assert.Single(result);
        Assert.Equal(originalConnectorId, single.ConnectorId);
        Assert.Equal("cr1a2_myconnector-5f1234567890abcdef", single.ConnectorName);
    }

    [Fact]
    public async Task GetAgentConnectionReferencesAsync_EnvironmentSpecificConnector_NoPrefixMatches_KeepsOriginal()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        const string originalConnectorId = "/providers/Microsoft.PowerApps/apis/cr1a2_myconnector-5f1234567890abcdef";
        var definition = new BotDefinition().WithConnectionReferences(new List<ConnectionReference>
        {
            MakeConnectionRef("cr_custom", originalConnectorId),
        });

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());
        dataverse
            .Setup(c => c.GetConnectorsByInternalIdPrefixAsync("cr1a2_myconnector-5f", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CustomConnectorMetadata>());

        var result = await synchronizer.GetAgentConnectionReferencesAsync(
            definition, dataverse.Object, CancellationToken.None);

        var single = Assert.Single(result);
        Assert.Equal(originalConnectorId, single.ConnectorId);
        Assert.Equal("cr1a2_myconnector-5f1234567890abcdef", single.ConnectorName);
    }
}
