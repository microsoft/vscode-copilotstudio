// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel.InitializableFeatures;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.Agents.Platform.Content.Internal.Modules;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal sealed class AgentWorkspaceCallFactory
{
    public const string HttpClientName = "Microsoft.CopilotStudio.Sync.Hosting.Dataverse";

    private readonly string _userAgent;

    private readonly IHttpMessageHandlerFactory _handlerFactory;

    private readonly Action<IServiceCollection>? _configureServices;

    static AgentWorkspaceCallFactory() => ObjectModelInitializableFeatures.NodeCache.IsEnabled = false;

    public AgentWorkspaceCallFactory(string userAgent, IHttpMessageHandlerFactory handlerFactory, Action<IServiceCollection>? configureServices = null)
    {
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? throw new ArgumentException("A user agent is required.", nameof(userAgent)) : userAgent;
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
        _configureServices = configureServices;
    }

    public AgentWorkspaceCall Open(Uri organization, ISyncAuthProvider authentication)
    {
        if (organization is not { IsAbsoluteUri: true })
        {
            throw new ArgumentException("The Dataverse endpoint has to be an absolute URI.", nameof(organization));
        }

        var guard = new AudienceGuardedSyncAuthProvider(authentication, organization);
        var accessor = guard.CreateDataverseAccessor(_handlerFactory, HttpClientName);
        var services = new ServiceCollection();

        services.AddLogging();
        ServiceRegistrations.AddServices(services, _userAgent);
        services.AddAuthoringStackBaselines();
        services.AddSingleton<ISyncAuthProvider>(guard);
        services.AddSingleton<IDataverseHttpClientAccessor>(accessor);
        services.AddSingleton<ISyncProgress>(SilentSyncProgress.Instance);
        services.AddSyncServices(_userAgent, isIslandPreauthorized: false, storageMode: SyncStorageMode.InMemory);
        _configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();

        try
        {
            return new AgentWorkspaceCall(provider, accessor, organization);
        }
        catch
        {
            provider.Dispose();
            accessor.Dispose();
            throw;
        }
    }
}

internal sealed class AgentWorkspaceCall : IAsyncDisposable
{
    private const string SessionPrefix = "agent-host-";

    private readonly ServiceProvider _services;

    private readonly OriginGuardedDataverseHttpClientAccessor _accessor;

    private readonly IWorkspaceLease _lease;

    private Action<string>? _stageStarted;

    public AgentWorkspaceCall(ServiceProvider services, OriginGuardedDataverseHttpClientAccessor accessor, Uri organization)
    {
        _services = services;
        _accessor = accessor;
        Files = services.GetRequiredService<IFileAccessorFactory>();
        WorkflowDataverse = services.GetRequiredService<IStandaloneWorkflowDataverseClient>();
        Dataverse = services.GetRequiredService<ISyncDataverseClient>();
        ChangeProbe = services.GetRequiredService<IAgentChangeProbeDataverseClient>();

        var dataverseUrl = organization.ToString();
        Dataverse.SetDataverseUrl(dataverseUrl);

        foreach (var target in new IDataverseEnvironmentTarget[] { WorkflowDataverse, services.GetRequiredService<IAgentPublishDataverseClient>(), ChangeProbe })
        {
            target.SetDataverseUrl(dataverseUrl);
        }

        _lease = Files.LeaseTemporaryWorkspace(SessionPrefix);
    }

    public IFileAccessorFactory Files { get; }

    public IWorkspaceSynchronizer Synchronizer => Service<IWorkspaceSynchronizer>();

    public IWorkspaceRetargetService RetargetService => Service<IWorkspaceRetargetService>();

    public IOperationContextProvider OperationContexts => Service<IOperationContextProvider>();

    public WorkspaceAttachService AttachService => Service<WorkspaceAttachService>();

    public WorkflowActivationPass ActivationPass => Service<WorkflowActivationPass>();

    public IStandaloneWorkflowSynchronizer Workflows => Service<IStandaloneWorkflowSynchronizer>();

    public IStandaloneWorkflowDataverseClient WorkflowDataverse { get; }

    public ISyncDataverseClient Dataverse { get; }

    public ISyncComponentCollectionDataverseClient Collections => Service<ISyncComponentCollectionDataverseClient>();

    public ISyncComponentCollectionLinkDataverseClient CollectionLinks => Service<ISyncComponentCollectionLinkDataverseClient>();

    public IAgentPublishService PublishService => Service<IAgentPublishService>();

    public IAgentChangeProbeDataverseClient ChangeProbe { get; }

    public IWorkspacePushService PushService => Service<IWorkspacePushService>();

    public string Stage { get; private set; } = AgentPushStages.Agent;

    public DirectoryPath Root => _lease.Root;

    public DirectoryPath FolderOf(string folder) => _lease.Root.GetChildDirectoryPath(folder);

    public IFileAccessor Open(string folder) => Files.Create(FolderOf(folder));

    public async Task<AgentChangeProbe?> ProbeOrNullAsync(Guid? botId, string schemaName, CancellationToken cancellationToken)
    {
        try
        {
            return await ChangeProbe.ProbeAgentAsync(botId, schemaName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void EnterFirstStage(string stage, Action<string>? stageStarted)
    {
        _stageStarted = stageStarted;
        BeginStage(stage);
    }

    public void Enter(string stage)
    {
        if (stage != Stage)
        {
            BeginStage(stage);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lease.Dispose();
        await _services.DisposeAsync().ConfigureAwait(false);
        _accessor.Dispose();
    }

    private T Service<T>() where T : notnull => _services.GetRequiredService<T>();

    private void BeginStage(string stage)
    {
        Stage = stage;

        try
        {
            _stageStarted?.Invoke(stage);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
        }
    }
}
