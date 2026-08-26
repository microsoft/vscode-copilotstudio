// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.CopilotStudio.McsCore;
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
    /// <param name="services">The container to register into.</param>
    /// <param name="userAgent">The user agent reported on Dataverse requests.</param>
    /// <param name="isIslandPreauthorized">Whether the island control plane is already authorized.</param>
    /// <param name="storageMode">Where workspaces are held. <see cref="SyncStorageMode.InMemory"/> keeps
    /// workspace content in process and retains it until the container scope ends.</param>
    public static void AddSyncServices(this IServiceCollection services, string userAgent = "CopilotStudio.Sync", bool isIslandPreauthorized = false, SyncStorageMode storageMode = SyncStorageMode.Physical)
    {
        services.AddSingleton<IIslandControlPlaneService>(sp =>
            new IslandControlPlaneService(
                sp.GetRequiredService<ISyncAuthProvider>(),
                sp.GetRequiredService<IContentAuthoringService>(),
                isIslandPreauthorized,
                sp.GetService<IHttpClientFactory>()));
        services.AddSingleton<IOperationContextProvider, OperationContextProvider>();
        services.AddSingleton(sp => new SyncDataverseClient(sp.GetRequiredService<IDataverseHttpClientAccessor>(), userAgent));
        services.AddSingleton<ISyncDataverseClient>(sp => sp.GetRequiredService<SyncDataverseClient>());
        services.AddSingleton<ISyncComponentCollectionDataverseClient>(sp => sp.GetRequiredService<SyncDataverseClient>());
        if (storageMode == SyncStorageMode.InMemory)
        {
            services.AddSingleton<IFileAccessorFactory>(_ => new InMemoryFileAccessorFactory(requireSession: true));
        }
        else
        {
            services.AddSingleton<IFileAccessorFactory, FileAccessorFactory>();
        }
        services.AddSingleton(LspProjectorService.Instance);
        services.AddSingleton<IMcsFileParser, SyncMcsFileParser>();
        services.AddSingleton<IComponentPathResolver, LspComponentPathResolver>();
        services.AddSingleton<WorkspaceSynchronizer>();
        services.AddSingleton<IWorkspaceSynchronizer>(sp => sp.GetRequiredService<WorkspaceSynchronizer>());
        services.AddSingleton<IConnectionManagementService>(sp => sp.GetRequiredService<WorkspaceSynchronizer>());
        services.AddSingleton<IWorkflowActivationService>(sp => sp.GetRequiredService<WorkspaceSynchronizer>());
        services.AddSingleton<IWorkspaceRetargetService>(sp => sp.GetRequiredService<WorkspaceSynchronizer>());
    }
}
