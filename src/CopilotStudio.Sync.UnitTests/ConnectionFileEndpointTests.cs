using System.Text.Json;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ConnectionFileEndpointTests
{
    private const string ConnectionFileTemplate = """
        {
            "DataverseEndpoint": "https://orgdd8356c3.crm.dynamics.com",
            "EnvironmentId": "a7975ddd-87fe-e986-9280-bfe47a2e7b10",
            "AccountInfo": {
                "AccountId": "674b4cab-fb0c-466a-a81d-5a94243993b7.a30263b9-1caf-4db5-ab53-ed3850c0bd1f",
                "TenantId": "a30263b9-1caf-4db5-ab53-ed3850c0bd1f",
                "AccountEmail": "dev@contoso.com",
                "clusterCategory": null
            },
            "AgentId": "17bf0849-310d-4305-b230-d6fe30b61557",
            "ComponentCollectionId": null{0}
        }
        """;

    private static AgentSyncInfo Deserialize(string? endpointProperty)
    {
        var json = ConnectionFileTemplate.Replace(
            "{0}",
            endpointProperty is null ? string.Empty : $",\n    \"AgentManagementEndpoint\": {endpointProperty}",
            StringComparison.Ordinal);

        var syncInfo = JsonSerializer.Deserialize<AgentSyncInfo>(json);
        Assert.NotNull(syncInfo);
        return syncInfo;
    }

    [Fact]
    public void NullAgentManagementEndpointDoesNotFailToDeserialize()
    {
        Assert.Null(Deserialize("null").AgentManagementEndpoint);
    }

    [Fact]
    public void MissingAgentManagementEndpointDoesNotFailToDeserialize()
    {
        Assert.Null(Deserialize(null).AgentManagementEndpoint);
    }

    [Fact]
    public void PopulatedAgentManagementEndpointIsPreserved()
    {
        var syncInfo = Deserialize("\"https://powervamg.us-il106.gateway.prod.island.powerapps.com/\"");

        Assert.Equal(
            new Uri("https://powervamg.us-il106.gateway.prod.island.powerapps.com/"),
            syncInfo.AgentManagementEndpoint);
    }

    [Fact]
    public void RepairedEndpointRoundTripsThroughTheConnectionFile()
    {
        var original = Deserialize(null);
        var repaired = new AgentSyncInfo
        {
            DataverseEndpoint = original.DataverseEndpoint,
            EnvironmentId = original.EnvironmentId,
            AccountInfo = original.AccountInfo,
            AgentId = original.AgentId,
            AgentManagementEndpoint = new Uri("https://powervamg.us-il106.gateway.prod.island.powerapps.com/")
        };

        var reloaded = JsonSerializer.Deserialize<AgentSyncInfo>(JsonSerializer.Serialize(repaired));

        Assert.NotNull(reloaded);
        Assert.Equal(repaired.AgentManagementEndpoint, reloaded.AgentManagementEndpoint);
        Assert.Equal(repaired.EnvironmentId, reloaded.EnvironmentId);
        Assert.Equal(repaired.AgentId, reloaded.AgentId);
    }
}
