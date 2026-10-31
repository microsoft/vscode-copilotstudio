// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.TestHarness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// Tests offline, without a tenant, that the harness containers can build every service the harness commands resolve.
/// </summary>
public sealed class HostServicesTests
{
    private static readonly Uri EnvironmentUrl = new("https://contoso.crm.dynamics.com");

    [Fact]
    public async Task Build_ResolvesEveryServiceTheHarnessCommandsUse()
    {
        await using var services = HostServices.Build(EnvironmentUrl, new NoTokenAuthProvider());

        Assert.NotNull(services.GetRequiredService<ISyncDataverseClient>());
        Assert.NotNull(services.GetRequiredService<IOperationContextProvider>());
        Assert.NotNull(services.GetRequiredService<IIslandControlPlaneService>());
        Assert.Same(services.GetRequiredService<IWorkspaceSynchronizer>(), services.GetRequiredService<IWorkspaceRetargetService>());
    }

    [Fact]
    public async Task BuildWithBridgeTypes_ResolvesEveryServiceTheBridgeCloneUses()
    {
        await using var services = BridgeHostServices.BuildWithBridgeTypes(EnvironmentUrl, "token");

        Assert.NotNull(services.GetRequiredService<ISyncDataverseClient>());
        Assert.NotNull(services.GetRequiredService<IOperationContextProvider>());
        Assert.NotNull(services.GetRequiredService<IWorkspaceSynchronizer>());
    }

    private sealed class NoTokenAuthProvider : ISyncAuthProvider
    {
        public Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }
}
