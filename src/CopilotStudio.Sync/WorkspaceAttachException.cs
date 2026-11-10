// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Why attaching a workspace to an agent failed.</summary>
public enum WorkspaceAttachFailure
{
    /// <summary>The workspace is already connected and the request does not allow retargeting it.</summary>
    AlreadyConnected = 0,

    /// <summary>The workspace holds content that cannot be reattached.</summary>
    ReattachNotSupported = 1,

    /// <summary>An agent other than the expected one already carries the schema name.</summary>
    AgentAlreadyExists = 2,

    /// <summary>The attach failed before any agent existed, and the target holds none with the schema name.</summary>
    AttachFailed = 3,

    /// <summary>The attach failed before an agent was known, and the target could not be re-read to say whether one exists.</summary>
    AttachUncertain = 4,

    /// <summary>The attach failed after an agent with the schema name existed in the target.</summary>
    ReattachFailed = 5,

    /// <summary>The target holds no agent the request names and the request does not allow creating one.</summary>
    AgentNotFound = 6,

    /// <summary>A folder the connect would write is already connected to another agent, component collection or environment.</summary>
    ConnectBoundToAnotherAgent = 7,
}

/// <summary>Raised when a workspace cannot be attached to an agent, saying what the target holds afterwards.</summary>
public sealed class WorkspaceAttachException : Exception
{
    /// <summary>Creates an exception with no detail.</summary>
    public WorkspaceAttachException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public WorkspaceAttachException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the failure that caused it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure that caused it.</param>
    public WorkspaceAttachException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private WorkspaceAttachException(WorkspaceAttachFailure failure, string message, Guid? agentId, string? reason, Exception? innerException)
        : base(message, innerException)
    {
        Failure = failure;
        AgentId = agentId;
        Reason = reason;
    }

    /// <summary>Why the attach failed.</summary>
    public WorkspaceAttachFailure Failure { get; }

    /// <summary>The agent that exists in the target after the failure, when one is known to.</summary>
    public Guid? AgentId { get; }

    /// <summary>Why the workspace cannot be reattached, when that is the failure.</summary>
    public string? Reason { get; }

    internal static WorkspaceAttachException AlreadyConnected(string schemaName) => new(WorkspaceAttachFailure.AlreadyConnected, $"The workspace for '{schemaName}' is already connected to an agent and the request does not allow retargeting it.", null, null, null);

    internal static WorkspaceAttachException ReattachNotSupported(string schemaName, string reason) => new(WorkspaceAttachFailure.ReattachNotSupported, $"The workspace for '{schemaName}' cannot be reattached: {reason}", null, reason, null);

    internal static WorkspaceAttachException AgentAlreadyExists(string schemaName, Guid existingAgentId) => new(WorkspaceAttachFailure.AgentAlreadyExists, $"Agent '{existingAgentId}' already carries the schema name '{schemaName}' and is not the agent the workspace expects.", existingAgentId, null, null);

    internal static WorkspaceAttachException AttachFailed(string schemaName, string environmentId, Exception innerException) => new(WorkspaceAttachFailure.AttachFailed, $"The workspace could not be attached to a new agent '{schemaName}' in {environmentId}, and no agent with that schema name exists there: {innerException.Message}", null, null, innerException);

    internal static WorkspaceAttachException AttachUncertain(string schemaName, string environmentId, Exception innerException) => new(WorkspaceAttachFailure.AttachUncertain, $"The workspace could not be attached to '{schemaName}' in {environmentId}, and the environment could not be re-read to say whether the agent exists: {innerException.Message}", null, null, innerException);

    internal static WorkspaceAttachException ReattachFailed(Guid agentId, string schemaName, Exception innerException) => new(WorkspaceAttachFailure.ReattachFailed, $"The workspace could not be attached to agent '{agentId}' ('{schemaName}'), which exists in the target: {innerException.Message}", agentId, null, innerException);

    internal static WorkspaceAttachException AgentNotFound(string schemaName, string environmentId) => new(WorkspaceAttachFailure.AgentNotFound, $"No agent with the schema name '{schemaName}' exists in {environmentId}, and the request does not allow creating one.", null, null, null);

    internal static WorkspaceAttachException ConnectAgentNotFound(string agent, string environmentId) => new(WorkspaceAttachFailure.AgentNotFound, $"No agent '{agent}' exists in {environmentId}, and connecting never creates one.", null, null, null);

    internal static WorkspaceAttachException ConnectBoundToAnotherAgent(string agent, Guid agentId, string environmentId, IEnumerable<DirectoryPath> folders) => new(WorkspaceAttachFailure.ConnectBoundToAnotherAgent, $"Connecting to '{agent}' in {environmentId} never rebinds a folder, and these folders are already connected to another agent, component collection or environment: {string.Join(", ", folders.Select(folder => $"'{folder}'"))}.", agentId, null, null);
}
