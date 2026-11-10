// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed class HostedCloud
{
    public const string EnvironmentId = "env-target";

    public static readonly Uri Organization = new("https://contoso.crm.dynamics.com/");

    public static readonly Guid TenantId = new("3f6b1c2e-8d4a-4b7e-9c5d-2a1e0f9b7c64");

    private readonly object _gate = new();

    private readonly Dictionary<string, AgentInfo> _agents = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ComponentCollectionInfo> _collections = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<Guid, List<Guid>> _links = new();

    private readonly Dictionary<Guid, ComponentCollectionInfo> _reportedCollections = new();

    private readonly Dictionary<Guid, Guid?> _workflowOwners = new();

    private readonly Dictionary<Guid, (Guid? AgentId, AIPromptMetadata Prompt)> _prompts = new();

    private readonly Dictionary<Guid, CustomConnectorMetadata> _connectors = new();

    public HostedCloud()
    {
        Collections = Cloud.Client.As<ISyncComponentCollectionDataverseClient>();
        Links = Cloud.Client.As<ISyncComponentCollectionLinkDataverseClient>();
        Publisher = Cloud.Client.As<IAgentPublishDataverseClient>();
        Provisioning = Cloud.Client.As<IAgentProvisioningDataverseClient>();
        Cloud.Client.As<IStreamingKnowledgeFileClient>();
        Island.LinkedCollections = LinkedCollectionsOf;
        Island.CollectionOf = collectionId => Locked(() => _collections.Values.FirstOrDefault(collection => collection.Id == collectionId)) is { } collection ? CodeSerializer.Deserialize<BotComponentCollection>($"schemaName: {collection.SchemaName}\ndisplayName: {collection.DisplayName}")!.WithId(new BotComponentCollectionId(collection.Id)) : null;

        Cloud.Client.Setup(client => client.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns<string, CancellationToken>((schemaName, _) => Task.FromResult(Locked(() => _agents.TryGetValue(schemaName, out var agent) ? agent.AgentId : Guid.Empty)));
        Cloud.Client.Setup(client => client.CreateNewAgentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AuthoringShape>(), It.IsAny<CancellationToken>())).Returns<string, string, AuthoringShape, CancellationToken>((displayName, schemaName, shape, _) => CreateThrows is { } failure ? Task.FromException<AgentInfo>(failure) : Task.FromResult(CreateAgent(displayName, schemaName, shape)));
        Cloud.Client.Setup(client => client.GetAgentInfoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns<Guid, CancellationToken>((agentId, _) => AgentInfoOf(agentId));
        Cloud.Client.Setup(client => client.GetSolutionVersionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new SolutionInfo { CopilotStudioSolutionVersion = new Version(1, 0), SolutionVersions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase) { ["msdyn_RelevanceSearch"] = new Version(1, 0), ["msft_AIPlatformExtensionsComponents"] = new Version(1, 0) } });
        Cloud.Client.Setup(client => client.UpdateWorkflowAsync(It.IsAny<Guid?>(), It.IsAny<WorkflowMetadata?>(), It.IsAny<CancellationToken>())).Returns<Guid?, WorkflowMetadata?, CancellationToken>((agentId, workflow, _) => Task.FromResult(StoreWorkflow(agentId, workflow!)));
        Cloud.Client.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).Returns<AgentSyncInfo, CancellationToken>((syncInfo, _) => Task.FromResult(Locked(() => (syncInfo.AgentId ?? syncInfo.ComponentCollectionId) is { } ownerId ? Cloud.Workflows.Values.Where(workflow => _workflowOwners.TryGetValue(workflow.WorkflowId, out var owner) && owner == ownerId).Select(FakeDataverseCloud.Copy).ToArray() : Array.Empty<WorkflowMetadata>())));
        Cloud.Client.Setup(client => client.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<AIPromptMetadata?>(), It.IsAny<CancellationToken>())).Returns<Guid?, AIPromptMetadata?, CancellationToken>((agentId, prompt, _) => Task.FromResult(StorePrompt(agentId, prompt!)));
        Cloud.Client.Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).Returns<AgentSyncInfo, CancellationToken>((syncInfo, _) => Task.FromResult(Locked(() => _prompts.Values.Where(prompt => (syncInfo.AgentId ?? syncInfo.ComponentCollectionId) is { } ownerId && prompt.AgentId == ownerId).Select(prompt => Copy(prompt.Prompt)).ToArray())));
        Cloud.Client.Setup(client => client.DownloadAIPromptsByModelIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).Returns<IReadOnlyCollection<Guid>, CancellationToken>((promptIds, _) => Task.FromResult(Locked(() => promptIds.Distinct().Where(_prompts.ContainsKey).Select(promptId => Copy(_prompts[promptId].Prompt)).ToArray())));
        Cloud.Client.Setup(client => client.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>())).Returns<CustomConnectorMetadata, CancellationToken>((connector, _) => Task.FromResult(StoreConnector(connector)));
        Cloud.Client.Setup(client => client.GetConnectorVersionsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns<IEnumerable<string>, bool, CancellationToken>((internalIds, _, _) => Task.FromResult(ConnectorsOf(internalIds)));
        Cloud.Client.Setup(client => client.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns<IEnumerable<string>, bool, CancellationToken>((internalIds, _, _) => Task.FromResult(ConnectorsOf(internalIds)));
        Cloud.Client.Setup(client => client.SetWorkflowStateAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns<Guid, bool, CancellationToken>(SetWorkflowStateAsync);
        Collections.Setup(collectionClient => collectionClient.CreateComponentCollectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns<string, string, CancellationToken>((displayName, schemaName, _) => Task.FromResult(CreateCollection(displayName, schemaName)));
        Collections.Setup(collectionClient => collectionClient.GetComponentCollectionIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns<string, CancellationToken>((schemaName, _) => Task.FromResult(Locked(() => _collections.TryGetValue(schemaName, out var collection) ? collection.Id : Guid.Empty)));
        Collections.Setup(collectionClient => collectionClient.InstallComponentCollectionOnAgentAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns<Guid, Guid, CancellationToken>((agentId, collectionId, _) => Run(() => Link(agentId, collectionId)));
        Collections.Setup(collectionClient => collectionClient.UninstallComponentCollectionFromAgentAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns<Guid, Guid, CancellationToken>((agentId, collectionId, _) => Run(() => Locked(() => _links.TryGetValue(collectionId, out var agents) && agents.Remove(agentId))));
        Collections.Setup(collectionClient => collectionClient.GetAgentIdsForComponentCollectionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns<Guid, CancellationToken>((collectionId, _) => Task.FromResult<IReadOnlyList<Guid>>(Locked(() => _links.TryGetValue(collectionId, out var agents) ? agents.ToArray() : Array.Empty<Guid>())));
        Links.Setup(linkClient => linkClient.GetComponentCollectionsBySchemaNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).Returns<IEnumerable<string>, CancellationToken>((schemaNames, _) => Task.FromResult<IReadOnlyDictionary<string, ComponentCollectionInfo>>(Locked(() => schemaNames.Where(_collections.ContainsKey).ToDictionary(schemaName => schemaName, schemaName => _collections[schemaName], StringComparer.OrdinalIgnoreCase))));
        Links.Setup(linkClient => linkClient.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).Returns<IEnumerable<string>, CancellationToken>((schemaNames, _) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<AgentInfo>>>(Locked(() => schemaNames.Where(_collections.ContainsKey).ToDictionary(schemaName => schemaName, schemaName => (IReadOnlyList<AgentInfo>)AgentsLinkedTo(_collections[schemaName].Id), StringComparer.OrdinalIgnoreCase))));
        Publisher.Setup(publisher => publisher.PublishAgentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns<Guid, CancellationToken>((agentId, _) => PublishRefusals.TryDequeue(out var refusal) ? Task.FromException(refusal) : PublishThrows is { } failure ? Task.FromException(failure) : Run(() => Published.Enqueue(agentId)));
        Publisher.Setup(publisher => publisher.GetLastPublishOperationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns<Guid, CancellationToken>((agentId, _) => LastPublishOf(agentId));
        Provisioning.Setup(provisioning => provisioning.GetProvisioningStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns<Guid, CancellationToken>((agentId, _) => ProvisioningStatusOf(agentId));
    }

    public FakeDataverseCloud Cloud { get; } = new();

    public FakeIsland Island { get; } = new();

    public FakeStandaloneWorkflowClient StandaloneWorkflows { get; } = new();

    public Mock<ISyncComponentCollectionDataverseClient> Collections { get; }

    public Mock<ISyncComponentCollectionLinkDataverseClient> Links { get; }

    public Mock<IAgentPublishDataverseClient> Publisher { get; }

    public Mock<IAgentProvisioningDataverseClient> Provisioning { get; }

    public ConcurrentQueue<string?> ProvisioningStatuses { get; } = new();

    public string? ProvisioningStatus { get; set; }

    public Exception? ProvisioningThrows { get; set; }

    public Action? ProvisioningRead { get; set; }

    public ConcurrentQueue<ProvisioningStatusRead> ProvisioningReads { get; } = new();

    public Mock<IAgentChangeProbeDataverseClient> ChangeProbe { get; } = new();

    public ConcurrentQueue<Guid> Published { get; } = new();

    public ConcurrentQueue<string> CreatedAgents { get; } = new();

    public Exception? PublishThrows { get; set; }

    public ConcurrentQueue<Exception> PublishRefusals { get; } = new();

    public string? PublishFinishesWith { get; set; } = "Succeeded";

    public Exception? PublishProgressThrows { get; set; }

    public SteppingTimeProvider Clock { get; } = new();

    public Exception? CreateThrows { get; set; }

    public Func<WorkflowMetadata, string?>? RefusesWorkflow { get; set; }

    public Func<AIPromptMetadata, string?>? RefusesPrompt { get; set; }

    public Func<Guid, Exception?>? RefusesActivation { get; set; }

    public static AgentSyncTarget Target(string schemaName, string? botId = null, string authoringShape = "") => new(Organization, EnvironmentId, TenantId, botId, schemaName, "Contoso Helpdesk", authoringShape);

    public IReadOnlyList<string> ContentWritesAmong(int dataverseCalls) => Cloud.Client.Invocations.Take(dataverseCalls).Select(invocation => invocation.Method.Name).Where(name => name is nameof(ISyncDataverseClient.UpsertConnectorAsync) or nameof(ISyncDataverseClient.EnsureConnectionReferenceExistsAsync) or nameof(ISyncDataverseClient.UpdateWorkflowAsync) or nameof(ISyncDataverseClient.InsertWorkflowAsync) or nameof(ISyncDataverseClient.SetWorkflowStateAsync) or nameof(ISyncDataverseClient.UpsertAIPromptAsync) or nameof(ISyncDataverseClient.UploadKnowledgeFileAsync) or nameof(ISyncComponentCollectionDataverseClient.CreateComponentCollectionAsync) or nameof(ISyncComponentCollectionDataverseClient.InstallComponentCollectionOnAgentAsync) or nameof(IAgentPublishDataverseClient.PublishAgentAsync)).ToArray();

    public IReadOnlyList<string> DataverseCalls() => Cloud.Client.Invocations.Select(invocation => invocation.Method.Name).Where(name => name != nameof(IDataverseEnvironmentTarget.SetDataverseUrl)).ToArray();

    public static AgentPushOptions Options(bool createIfMissing = true) => new(createIfMissing);

    public AgentWorkspaceHost CreateHost(IFileAccessorFactory? files = null, IHttpMessageHandlerFactory? handlers = null, Action<IServiceCollection>? configure = null) => new(new AgentWorkspaceCallFactory("Microsoft.CopilotStudio.Sync.UnitTests", handlers ?? new StubHandlerFactory(), services =>
    {
        Register(services);

        if (files is not null)
        {
            services.AddSingleton(files);
        }

        configure?.Invoke(services);
    }));

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(Island.Service.Object);
        services.AddSingleton(Cloud.Client.Object);
        services.AddSingleton(Collections.Object);
        services.AddSingleton(Links.Object);
        services.AddSingleton(Publisher.Object);
        services.AddSingleton(ChangeProbe.Object);
        services.AddSingleton<IStandaloneWorkflowDataverseClient>(StandaloneWorkflows);
        services.AddSingleton<TimeProvider>(Clock);
    }

    public void ReportProvisioningStatuses(params string?[] statuses)
    {
        foreach (var status in statuses)
        {
            ProvisioningStatuses.Enqueue(status);
        }
    }

    public Guid SeedAgent(string schemaName, string displayName, params BotComponentBase[] components) => SeedAgent(schemaName, displayName, AuthoringShape.Classic, components);

    public Guid SeedAgent(string schemaName, string displayName, AuthoringShape shape, params BotComponentBase[] components)
    {
        var agentId = CreateAgent(displayName, schemaName, shape).AgentId;
        Island.Seed(agentId, components);

        return agentId;
    }

    public Guid SeedCollection(string schemaName, string displayName, params BotComponentBase[] components)
    {
        var collectionId = CreateCollection(displayName, schemaName).Id;
        Island.Seed(collectionId, components);

        return collectionId;
    }

    public void Link(Guid agentId, Guid collectionId) => Locked(() =>
    {
        if (!_links.TryGetValue(collectionId, out var agents))
        {
            agents = new List<Guid>();
            _links[collectionId] = agents;
        }

        if (!agents.Contains(agentId))
        {
            agents.Add(agentId);
        }

        return agents;
    });

    public void AddWorkflow(Guid ownerId, WorkflowMetadata workflow) => Locked(() =>
    {
        Cloud.Workflows[workflow.WorkflowId] = FakeDataverseCloud.Copy(workflow);
        _workflowOwners[workflow.WorkflowId] = ownerId;

        return workflow;
    });

    public void AddPrompt(Guid agentId, AIPromptMetadata prompt) => Locked(() => _prompts[prompt.AIModelId] = (agentId, Copy(prompt)));

    public void ReportCollectionAs(Guid collectionId, string? displayName, string? schemaName) => Locked(() => _reportedCollections[collectionId] = new ComponentCollectionInfo { Id = collectionId, SchemaName = schemaName!, DisplayName = displayName! });

    public Guid AgentIdOf(string schemaName) => Locked(() => _agents[schemaName].AgentId);

    public Guid CollectionIdOf(string schemaName) => Locked(() => _collections[schemaName].Id);

    public IReadOnlyList<AIPromptMetadata> PromptsOf(Guid agentId) => Locked(() => _prompts.Values.Where(prompt => prompt.AgentId == agentId).Select(prompt => prompt.Prompt).ToArray());

    private static AIPromptMetadata Copy(AIPromptMetadata prompt) => new() { AIModelId = prompt.AIModelId, Name = prompt.Name, TemplateId = prompt.TemplateId, CustomConfiguration = prompt.CustomConfiguration };

    private static CustomConnectorMetadata Copy(CustomConnectorMetadata connector) => JsonSerializer.Deserialize<CustomConnectorMetadata>(JsonSerializer.Serialize(connector))!;

    private Task<AgentProvisioningStatus> ProvisioningStatusOf(Guid agentId)
    {
        ProvisioningReads.Enqueue(new ProvisioningStatusRead(agentId, Cloud.Client.Invocations.Count, Island.Saves.Count));
        ProvisioningRead?.Invoke();

        return ProvisioningThrows is { } failure ? Task.FromException<AgentProvisioningStatus>(failure) : Task.FromResult(new AgentProvisioningStatus(ProvisioningStatuses.TryDequeue(out var status) ? status : ProvisioningStatus, Island.EntityOf(agentId)?.Template));
    }

    private Task<AgentPublishOperation?> LastPublishOf(Guid agentId)
    {
        var publishes = Published.Count(published => published == agentId);

        if (publishes > 0 && PublishProgressThrows is { } failure)
        {
            return Task.FromException<AgentPublishOperation?>(failure);
        }

        return Task.FromResult(publishes > 0 && PublishFinishesWith is { } status ? new AgentPublishOperation(status, new DateTimeOffset(2026, 10, 25, 19, publishes, 2, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 19, publishes, 12, TimeSpan.Zero)) : null);
    }

    private static Task Run(Action action)
    {
        action();

        return Task.CompletedTask;
    }

    private AgentInfo CreateAgent(string displayName, string schemaName, AuthoringShape shape)
    {
        var agent = Locked(() =>
        {
            var created = new AgentInfo { AgentId = Guid.NewGuid(), DisplayName = displayName, SchemaName = schemaName };
            _agents[schemaName] = created;
            CreatedAgents.Enqueue(schemaName);

            return created;
        });

        Island.SetEntity(agent.AgentId, CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {schemaName}\ndisplayName: {displayName}\n{(shape == AuthoringShape.CliCopilot ? "authenticationMode: Integrated\nauthenticationTrigger: Always\nconfiguration:\n  authoringModel: CliCopilot\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\ntemplate: cliagent-1.0.0\n" : "authenticationMode: None\nauthenticationTrigger: AsNeeded\ntemplate: empty-1.0.0\n")}language: 1033\n")!);

        return new AgentInfo { AgentId = agent.AgentId, DisplayName = displayName, SchemaName = schemaName };
    }

    private ComponentCollectionInfo CreateCollection(string displayName, string schemaName) => Locked(() =>
    {
        var created = new ComponentCollectionInfo { Id = Guid.NewGuid(), SchemaName = schemaName, DisplayName = displayName };
        _collections[schemaName] = created;

        return created;
    });

    private Task<AgentInfo> AgentInfoOf(Guid agentId) => Locked(() =>
    {
        if (_agents.Values.FirstOrDefault(agent => agent.AgentId == agentId) is not { } agent)
        {
            return Task.FromException<AgentInfo>(new DataverseRequestException(HttpStatusCode.NotFound, $"Bot {agentId} was not found."));
        }

        return Task.FromResult(new AgentInfo { AgentId = agent.AgentId, DisplayName = agent.DisplayName, SchemaName = agent.SchemaName, ComponentCollections = _collections.Values.Where(collection => _links.TryGetValue(collection.Id, out var agents) && agents.Contains(agentId)).Select(collection => _reportedCollections.TryGetValue(collection.Id, out var reported) ? reported : new ComponentCollectionInfo { Id = collection.Id, SchemaName = collection.SchemaName, DisplayName = collection.DisplayName }).ToList() });
    });

    private IEnumerable<BotComponentCollection> LinkedCollectionsOf(Guid agentId) => Locked(() => _collections.Values.Where(collection => _links.TryGetValue(collection.Id, out var agents) && agents.Contains(agentId)).OrderBy(collection => collection.SchemaName, StringComparer.Ordinal).Select(collection => CodeSerializer.Deserialize<BotComponentCollection>($"schemaName: {collection.SchemaName}")!.WithId(new BotComponentCollectionId(collection.Id))).ToArray());

    private List<AgentInfo> AgentsLinkedTo(Guid collectionId) => _links.TryGetValue(collectionId, out var agents) ? agents.Select(agentId => _agents.Values.First(agent => agent.AgentId == agentId)).Select(agent => new AgentInfo { AgentId = agent.AgentId, DisplayName = agent.DisplayName, SchemaName = agent.SchemaName }).ToList() : new List<AgentInfo>();

    private WorkflowResponse StoreWorkflow(Guid? agentId, WorkflowMetadata workflow)
    {
        if (RefusesWorkflow?.Invoke(workflow) is { } refusal)
        {
            return new WorkflowResponse { WorkflowId = workflow.WorkflowId, WorkflowName = workflow.Name ?? string.Empty, ErrorMessage = refusal };
        }

        var stored = FakeDataverseCloud.Copy(workflow);
        Locked(() =>
        {
            Cloud.Workflows[stored.WorkflowId] = stored;
            _workflowOwners[stored.WorkflowId] = agentId;

            return stored;
        });

        return new WorkflowResponse { WorkflowId = stored.WorkflowId, WorkflowName = stored.Name ?? stored.WorkflowId.ToString(), IsDisabled = stored.StateCode != 1, StoredWorkflow = FakeDataverseCloud.Copy(stored) };
    }

    private AIPromptResponse StorePrompt(Guid? agentId, AIPromptMetadata prompt)
    {
        if (RefusesPrompt?.Invoke(prompt) is { } refusal)
        {
            return new AIPromptResponse { PromptName = prompt.Name, ErrorMessage = refusal };
        }

        Locked(() => _prompts[prompt.AIModelId] = (agentId, Copy(prompt)));

        return new AIPromptResponse { PromptName = prompt.Name };
    }

    private bool StoreConnector(CustomConnectorMetadata connector) => Locked(() =>
    {
        var created = !_connectors.ContainsKey(connector.ConnectorId);
        _connectors[connector.ConnectorId] = Copy(connector);

        return created;
    });

    private CustomConnectorMetadata[] ConnectorsOf(IEnumerable<string> internalIds)
    {
        var requested = new HashSet<string>(internalIds, StringComparer.OrdinalIgnoreCase);

        return Locked(() => _connectors.Values.Where(connector => connector.ConnectorInternalId is { } internalId && requested.Contains(internalId)).Select(Copy).ToArray());
    }

    private Task SetWorkflowStateAsync(Guid workflowId, bool activate, CancellationToken cancellationToken)
    {
        if (activate && RefusesActivation?.Invoke(workflowId) is { } refusal)
        {
            return Task.FromException(refusal);
        }

        if (StandaloneWorkflows.Stored(workflowId) is { } standalone)
        {
            standalone.StateCode = activate ? 1 : 0;
            standalone.StatusCode = activate ? 2 : 1;
            StandaloneWorkflows.Seed(standalone);

            return Task.CompletedTask;
        }

        if (!Cloud.Workflows.TryGetValue(workflowId, out var workflow))
        {
            return Task.FromException(new InvalidOperationException($"Workflow '{workflowId}' was not found."));
        }

        workflow.StateCode = activate ? 1 : 0;
        workflow.StatusCode = activate ? 2 : 1;

        return Task.CompletedTask;
    }

    private T Locked<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }
}

internal sealed record ProvisioningStatusRead(Guid AgentId, int DataverseCallsSoFar, int SavesSoFar);

internal sealed class StubHandlerFactory : IHttpMessageHandlerFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHandlerFactory(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) => _respond = respond ?? (request => throw new InvalidOperationException($"No request was expected, but {request.Method} {request.RequestUri} was sent."));

    public ConcurrentQueue<string> CreatedNames { get; } = new();

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public HttpMessageHandler CreateHandler(string name)
    {
        CreatedNames.Enqueue(name);

        return new StubHandler(this);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly StubHandlerFactory _factory;

        public StubHandler(StubHandlerFactory factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _factory.Requests.Enqueue(request);

            return Task.FromResult(_factory._respond(request));
        }
    }
}

internal sealed class RecordingAuthProvider : ISyncAuthProvider
{
    public ConcurrentQueue<Uri> Audiences { get; } = new();

    public Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default)
    {
        Audiences.Enqueue(audience);

        return Task.FromResult("token-for-" + audience.Host);
    }
}
