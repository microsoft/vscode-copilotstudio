// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>A publish of an agent that Copilot Studio finished, as the bot's synchronization status reports it.</summary>
/// <param name="Status">The status Copilot Studio finished the publish with, such as Succeeded.</param>
/// <param name="OperationStart">When the publish started, or <see langword="null"/> when the status names no time.</param>
/// <param name="OperationEnd">When the publish finished, or <see langword="null"/> when the status names no time.</param>
public sealed record AgentPublishOperation(string Status, DateTimeOffset? OperationStart, DateTimeOffset? OperationEnd);

/// <summary>Publishes agents in one environment.</summary>
public interface IAgentPublishDataverseClient : IDataverseEnvironmentTarget
{
    /// <summary>Asks Copilot Studio to publish an agent's current content, which it finishes some time after the request returns.</summary>
    /// <param name="agentId">The agent to publish.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task PublishAgentAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>Reads the last publish of an agent that Copilot Studio finished, or <see langword="null"/> when it finished none, throwing a <see cref="FormatException"/> when the bot's synchronization status cannot be read.</summary>
    /// <param name="agentId">The agent whose publishes are read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AgentPublishOperation?> GetLastPublishOperationAsync(Guid agentId, CancellationToken cancellationToken);
}

/// <summary>Reads how far Copilot Studio has provisioned agents in one environment.</summary>
public interface IAgentProvisioningDataverseClient : IDataverseEnvironmentTarget
{
    /// <summary>Reads in one request the provisioning status the bot's synchronization status reports for an agent and the template the bot was created from, throwing a <see cref="FormatException"/> when the bot's synchronization status cannot be read.</summary>
    /// <param name="agentId">The agent whose provisioning status is read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AgentProvisioningStatus> GetProvisioningStatusAsync(Guid agentId, CancellationToken cancellationToken);
}

/// <summary>What a bot reports about Copilot Studio provisioning it.</summary>
/// <param name="Status">The provisioning status the bot's synchronization status reports, such as Provisioning while Copilot Studio still provisions it, or <see langword="null"/> when it reports none.</param>
/// <param name="Template">The template the bot was created from, which tells whether Copilot Studio provisions it on its own or, for a CLI agent template, only on its first publish, or <see langword="null"/> when the bot names none.</param>
public sealed record AgentProvisioningStatus(string? Status, string? Template);
