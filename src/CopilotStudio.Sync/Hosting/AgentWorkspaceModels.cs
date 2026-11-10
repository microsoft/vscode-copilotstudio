// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Hosting;

/// <summary>An agent or workflow folder and the component-collection folders it declares, as repository-relative '/'-separated paths mapped to file content.</summary>
/// <param name="Folder">The top-level folder holding the agent or workflow project.</param>
/// <param name="Files">The content of every file, keyed by its repository-relative path.</param>
public sealed record AgentWorkspaceSnapshot(string Folder, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files);

/// <summary>What validating a snapshot found.</summary>
/// <param name="IsValid">Whether the snapshot may be pushed.</param>
/// <param name="Diagnostics">Every rule the snapshot breaks.</param>
/// <param name="DeclaredCollectionFolders">The component-collection folders the agent declares, in declaration order.</param>
/// <param name="AuthoringMode">The agent's authoring mode, <c>cli-copilot</c> or <c>classic</c>, or <see langword="null"/> when it cannot be told.</param>
public sealed record AgentWorkspaceValidation(bool IsValid, IReadOnlyList<AgentSyncDiagnostic> Diagnostics, IReadOnlyList<string> DeclaredCollectionFolders, string? AuthoringMode)
{
    /// <summary>The folder of each declared component collection, keyed by the schema name its collection file declares and compared ignoring case, in the form a clone takes as its collection folders.</summary>
    public IReadOnlyDictionary<string, string> DeclaredCollections { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What validating a standalone workflow snapshot found.</summary>
/// <param name="IsValid">Whether the snapshot may be pushed.</param>
/// <param name="Diagnostics">Every rule the snapshot breaks.</param>
/// <param name="WorkflowId">The workflow the folder declares, or <see langword="null"/> when it cannot be told.</param>
public sealed record WorkflowWorkspaceValidation(bool IsValid, IReadOnlyList<AgentSyncDiagnostic> Diagnostics, string? WorkflowId)
{
    internal WorkflowFile? Workflow { get; init; }
}

/// <summary>The bot an operation reads from or writes to.</summary>
/// <param name="DataverseEndpoint">The Dataverse organization of the bot's environment.</param>
/// <param name="EnvironmentId">The bot's environment.</param>
/// <param name="TenantId">The caller's Microsoft Entra tenant, which owns the environment.</param>
/// <param name="BotId">The bot's identifier, or <see langword="null"/> when it is found by schema name.</param>
/// <param name="SchemaName">The bot's schema name.</param>
/// <param name="DisplayName">The display name a bot is created with, of at most <see cref="AgentWorkspaceIdentity.MaximumDisplayNameLength"/> characters.</param>
/// <param name="AuthoringShape">The bot's authoring mode, <c>cli-copilot</c> or <c>classic</c>, empty when it is not known.</param>
public sealed record AgentSyncTarget(Uri DataverseEndpoint, string EnvironmentId, Guid TenantId, string? BotId, string SchemaName, string DisplayName, string AuthoringShape);

/// <summary>What cloning a bot and its linked component collections needs.</summary>
/// <param name="Source">The bot to clone.</param>
/// <param name="Folder">The top-level folder the agent is written to.</param>
/// <param name="CollectionFolders">The folders already holding collections, keyed by collection schema name.</param>
/// <param name="OccupiedFolders">The top-level folders a new collection folder must not take.</param>
public sealed record AgentCloneRequest(AgentSyncTarget Source, string Folder, IReadOnlyDictionary<string, string> CollectionFolders, IReadOnlyCollection<string> OccupiedFolders)
{
    /// <summary>Tells whether a top-level folder name the clone considers for a new collection folder is already taken, asked only about names outside the known, occupied and already chosen folders, or <see langword="null"/> to ask nothing.</summary>
    public Func<string, CancellationToken, ValueTask<bool>>? IsFolderTaken { get; init; }

    /// <summary>Whether the clone reads the bot's change probe before it reads the bot and returns it as <see cref="AgentCloneResult.ChangeProbe"/>, which it does only when this is set to <see langword="true"/>, so a clone that leaves it unset sends no probe request.</summary>
    public bool ReadChangeProbe { get; init; }
}

/// <summary>The standalone workflow an operation reads from or writes to.</summary>
/// <param name="DataverseEndpoint">The Dataverse organization of the workflow's environment.</param>
/// <param name="EnvironmentId">The workflow's environment.</param>
/// <param name="TenantId">The caller's Microsoft Entra tenant, which owns the environment.</param>
/// <param name="WorkflowId">The workflow's identifier, or <see langword="null"/> to take it from the snapshot.</param>
public sealed record WorkflowSyncTarget(Uri DataverseEndpoint, string EnvironmentId, Guid TenantId, string? WorkflowId);

/// <summary>What cloning a standalone workflow needs.</summary>
/// <param name="Source">The workflow to clone.</param>
/// <param name="Folder">The top-level folder the workflow is written to.</param>
public sealed record WorkflowCloneRequest(WorkflowSyncTarget Source, string Folder);

/// <summary>The identity a standalone workflow folder declares for itself.</summary>
/// <param name="Name">The workflow's name.</param>
public sealed record WorkflowWorkspaceIdentity(string Name);

/// <summary>What cloning a bot produced.</summary>
/// <param name="Snapshot">The agent folder and the collection folders it declares, without any sync state.</param>
/// <param name="BotId">The cloned bot.</param>
public sealed record AgentCloneResult(AgentWorkspaceSnapshot Snapshot, string BotId)
{
    /// <summary>The bot's change probe as read just before the clone read the bot when <see cref="AgentCloneRequest.ReadChangeProbe"/> asked for it, or <see langword="null"/> when it was not asked for or could not be read.</summary>
    public AgentChangeProbe? ChangeProbe { get; init; }
}

/// <summary>What cloning a standalone workflow produced.</summary>
/// <param name="Snapshot">The workflow folder, without any sync state.</param>
/// <param name="WorkflowId">The cloned workflow.</param>
public sealed record WorkflowCloneResult(AgentWorkspaceSnapshot Snapshot, string WorkflowId);

/// <summary>How a push treats the target, what it does once the content lands and what it tells of its progress.</summary>
/// <param name="CreateIfMissing">Whether a bot is created when the target holds none with the schema name.</param>
public sealed record AgentPushOptions(bool CreateIfMissing)
{
    internal TimeSpan PublishTimeout { get; init; } = TimeSpan.FromMinutes(5);

    internal TimeSpan ProvisioningTimeout { get; init; } = AgentProvisioning.DefaultTimeout;

    /// <summary>Whether an agent push publishes the bot once its content is in place and its workflows activated, which it does unless this is set to <see langword="false"/>, in which case it asks for no publish and reports the bot as not published.</summary>
    public bool Publish { get; init; } = true;

    /// <summary>Whether an agent push reads the bot's change probe once it finishes and returns it as <see cref="AgentPushResult.ChangeProbe"/>, which it does only when this is set to <see langword="true"/>, so a push that leaves it unset sends no probe request.</summary>
    public bool ReadChangeProbe { get; init; }

    /// <summary>Called with the <see cref="AgentPushStages"/> name each time an agent or workflow push moves into another stage, in order and starting with the agent stage of an agent push or the workflows stage of a workflow push, with any exception it throws other than running out of memory ignored so it never fails or changes the push.</summary>
    public Action<string>? StageStarted { get; init; }
}

/// <summary>What pushing an agent achieved.</summary>
/// <param name="BotId">The bot the snapshot was pushed to.</param>
/// <param name="Published">Whether the push published the bot and Copilot Studio finished that publish with the status Succeeded.</param>
/// <param name="FlowsLeftInDraft">The workflows left in draft, with the connection references still unbound.</param>
/// <param name="SharedCollections">The pushed component collections other bots are linked to.</param>
/// <param name="Diagnostics">What the push could not finish without failing.</param>
public sealed record AgentPushResult(string BotId, bool Published, IReadOnlyList<DraftFlow> FlowsLeftInDraft, IReadOnlyList<SharedCollection> SharedCollections, IReadOnlyList<AgentSyncDiagnostic> Diagnostics)
{
    /// <summary>When the publish this push asked for finished, or else when the bot was last published as the push read it, which is <see langword="null"/> for a bot never published, such as one the push created.</summary>
    public DateTimeOffset? PublishedOn { get; init; }

    /// <summary>The bot's change probe as read once the push and its publish finished when <see cref="AgentPushOptions.ReadChangeProbe"/> asked for it, or <see langword="null"/> when it was not asked for or could not be read.</summary>
    public AgentChangeProbe? ChangeProbe { get; init; }
}

/// <summary>A workflow a push left in draft.</summary>
/// <param name="WorkflowId">The workflow.</param>
/// <param name="Name">The workflow's display name.</param>
/// <param name="UnboundConnectionReferences">The connection references the workflow uses that are not bound.</param>
public sealed record DraftFlow(string WorkflowId, string Name, IReadOnlyList<string> UnboundConnectionReferences);

/// <summary>A pushed component collection and the other bots linked to it.</summary>
/// <param name="SchemaName">The collection's schema name.</param>
/// <param name="OtherBotSchemaNames">The schema names of the other bots linked to the collection.</param>
public sealed record SharedCollection(string SchemaName, IReadOnlyList<string> OtherBotSchemaNames);

/// <summary>What pushing a standalone workflow achieved.</summary>
/// <param name="Activated">Whether the workflow is running after the push.</param>
/// <param name="UnboundConnectionReferences">The connection references the workflow uses that are not bound, empty once the workflow runs.</param>
/// <param name="Diagnostics">What the push could not finish without failing.</param>
public sealed record WorkflowPushResult(bool Activated, IReadOnlyList<string> UnboundConnectionReferences, IReadOnlyList<AgentSyncDiagnostic> Diagnostics);

/// <summary>One finding of a validation or an operation.</summary>
/// <param name="Code">The stable code of the finding.</param>
/// <param name="Message">What was found.</param>
/// <param name="Path">The repository-relative path the finding is about, when it concerns one.</param>
public sealed record AgentSyncDiagnostic(string Code, string Message, string? Path = null)
{
    private readonly string _message = DataverseFailureText.Redact(Message);

    private readonly string? _detail;

    /// <summary>What was found, with any Dataverse failure in it described by its status and reported error rather than its raw response.</summary>
    public string Message { get => _message; init => _message = DataverseFailureText.Redact(value); }

    /// <summary>The item the finding concerns, such as a workflow, an AI prompt or a kind of change, or <see langword="null"/> when it concerns no single item.</summary>
    public string? Subject { get; init; }

    /// <summary>The reason the service gave, with any Dataverse failure in it described by its status and reported error rather than its raw response, or <see langword="null"/> when the service gave none apart from <see cref="Message"/>.</summary>
    public string? Detail { get => _detail; init => _detail = value is null ? null : DataverseFailureText.Redact(value); }
}
