using System.Text.Json;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ConnectionFileTenantIdTests
{
    private const string ConnectionFileTemplate = """
        {
            "DataverseEndpoint": "https://orgdd8356c3.crm.dynamics.com",
            "EnvironmentId": "a7975ddd-87fe-e986-9280-bfe47a2e7b10",
            "AccountInfo": {
                "AccountId": "",
                "TenantId": {0},
                "AccountEmail": null,
                "clusterCategory": null
            },
            "AgentId": "17bf0849-310d-4305-b230-d6fe30b61557",
            "ComponentCollectionId": null
        }
        """;

    private static AgentSyncInfo Deserialize(string tenantIdJson)
    {
        var json = ConnectionFileTemplate.Replace("{0}", tenantIdJson, StringComparison.Ordinal);
        var syncInfo = JsonSerializer.Deserialize<AgentSyncInfo>(json);
        Assert.NotNull(syncInfo);
        return syncInfo;
    }

    [Fact]
    public void NullTenantIdDoesNotFailToDeserialize()
    {
        var syncInfo = Deserialize("null");

        Assert.NotNull(syncInfo.AccountInfo);
        Assert.Equal(Guid.Empty, syncInfo.AccountInfo.TenantId);
    }

    [Fact]
    public void EmptyStringTenantIdDoesNotFailToDeserialize()
    {
        var syncInfo = Deserialize("\"\"");

        Assert.NotNull(syncInfo.AccountInfo);
        Assert.Equal(Guid.Empty, syncInfo.AccountInfo.TenantId);
    }

    [Fact]
    public void WhitespaceTenantIdDoesNotFailToDeserialize()
    {
        var syncInfo = Deserialize("\"   \"");

        Assert.NotNull(syncInfo.AccountInfo);
        Assert.Equal(Guid.Empty, syncInfo.AccountInfo.TenantId);
    }

    [Fact]
    public void AllZeroTenantIdIsPreserved()
    {
        var syncInfo = Deserialize("\"00000000-0000-0000-0000-000000000000\"");

        Assert.NotNull(syncInfo.AccountInfo);
        Assert.Equal(Guid.Empty, syncInfo.AccountInfo.TenantId);
    }

    [Fact]
    public void RealTenantIdIsPreserved()
    {
        var syncInfo = Deserialize("\"a30263b9-1caf-4db5-ab53-ed3850c0bd1f\"");

        Assert.NotNull(syncInfo.AccountInfo);
        Assert.Equal(Guid.Parse("a30263b9-1caf-4db5-ab53-ed3850c0bd1f"), syncInfo.AccountInfo.TenantId);
    }

    [Fact]
    public void MalformedTenantIdStillFails()
    {
        Assert.ThrowsAny<JsonException>(() => Deserialize("\"not-a-guid\""));
    }

    [Fact]
    public void TenantIdRoundTripsAsAString()
    {
        var tenantId = Guid.Parse("a30263b9-1caf-4db5-ab53-ed3850c0bd1f");
        var json = JsonSerializer.Serialize(new AccountInfo { AccountId = "id", TenantId = tenantId });

        Assert.Contains("\"a30263b9-1caf-4db5-ab53-ed3850c0bd1f\"", json, StringComparison.Ordinal);
        Assert.Equal(tenantId, JsonSerializer.Deserialize<AccountInfo>(json)!.TenantId);
    }

    [Fact]
    public void EmptyTenantIdRoundTripsBackToEmpty()
    {
        var json = JsonSerializer.Serialize(new AccountInfo { AccountId = "id", TenantId = Guid.Empty });

        Assert.Equal(Guid.Empty, JsonSerializer.Deserialize<AccountInfo>(json)!.TenantId);
    }
}
