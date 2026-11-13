using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Copy;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

public class CopyServiceRegistrationTests
{
    [Fact]
    public void AddSyncServices_RegistersTheCopyServicesOverThePlainSyncClients()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IDataverseHttpClientAccessor>().Object);
        services.AddSingleton(new Mock<ISyncAuthProvider>().Object);
        services.AddSingleton(new Mock<IContentAuthoringService>().Object);
        services.AddSingleton<ISyncProgress>(new TestSyncProgress(new List<string>()));
        services.AddSyncServices();
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<SyncDataverseClient>();

        Assert.IsType<SyncDataverseClient>(client);
        Assert.Same(client, provider.GetRequiredService<ISyncDataverseClient>());
        Assert.Same(client, provider.GetRequiredService<IStandaloneWorkflowDataverseClient>());
        Assert.Same(client, provider.GetRequiredService<ISyncComponentCollectionDataverseClient>());
        Assert.Same(client, provider.GetRequiredService<ICopyDataverseClient>());
        Assert.IsType<IslandControlPlaneService>(provider.GetRequiredService<IIslandControlPlaneService>());
        Assert.IsType<WorkspaceCopyService>(provider.GetRequiredService<IWorkspaceCopyService>());
    }
}
