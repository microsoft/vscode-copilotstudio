using Microsoft.Agents.Platform.Content;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class OperationContextProviderTests
{
    [Fact]
    public async Task GetAsync_ComponentCollectionSyncInfo_ReturnsComponentCollectionContext()
    {
        var collectionId = Guid.NewGuid();
        var context = await new OperationContextProvider().GetAsync(new AgentSyncInfo
        {
            ComponentCollectionId = collectionId,
            EnvironmentId = "environment-id",
            DataverseEndpoint = new Uri("https://test.crm.dynamics.com"),
            AccountInfo = new AccountInfo
            {
                AccountId = "account-id",
                TenantId = Guid.NewGuid(),
                AccountEmail = "test@example.com"
            },
            SolutionVersions = new SolutionInfo
            {
                CopilotStudioSolutionVersion = new Version(1, 0, 0, 0),
                SolutionVersions = new Dictionary<string, Version>
                {
                    ["msdyn_RelevanceSearch"] = new Version(1, 0, 0, 0),
                    ["msft_AIPlatformExtensionsComponents"] = new Version(1, 0, 0, 0)
                }
            }
        });

        Assert.Equal(collectionId, Assert.IsType<BotComponentCollectionAuthoringOperationContext>(context).BotComponentCollectionReference.CdsId);
    }
}
