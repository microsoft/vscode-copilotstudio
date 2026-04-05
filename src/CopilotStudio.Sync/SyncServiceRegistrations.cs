// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Registers shared sync library services into the DI container.
/// Each host calls this to wire up the internal sync types.
/// </summary>
public static class SyncServiceRegistrations
{
    /// <summary>
    /// Registers all shared sync library services. The host must register
    /// <see cref="ISyncAuthProvider"/>, <see cref="ISyncProgress"/>, and
    /// <see cref="Microsoft.Agents.Platform.Content.Abstractions.IDataverseHttpClientAccessor"/>
    /// before calling this method.
    /// </summary>
    public static void AddSyncServices(this IServiceCollection services, string userAgent = "CopilotStudio.Sync", bool isIslandPreauthorized = false)
    {
        services.AddSingleton<IIslandControlPlaneService>(sp =>
            new IslandControlPlaneService(
                sp.GetRequiredService<ISyncAuthProvider>(),
                sp.GetRequiredService<IContentAuthoringService>(),
                isIslandPreauthorized,
                sp.GetService<IHttpClientFactory>()));
        services.AddSingleton<IOperationContextProvider, OperationContextProvider>();
        services.AddSingleton<ISyncDataverseClient>(sp =>
            new SyncDataverseClient(sp.GetRequiredService<IDataverseHttpClientAccessor>(), userAgent));
        services.AddSingleton<IFileAccessorFactory, FileAccessorFactory>();
        services.AddSingleton(LspProjectorService.Instance);
        services.AddSingleton<IMcsFileParser, SyncMcsFileParser>();
        services.AddSingleton<IComponentPathResolver, LspComponentPathResolver>();
        services.AddSingleton<IWorkspaceSynchronizer, WorkspaceSynchronizer>();
    }
}
