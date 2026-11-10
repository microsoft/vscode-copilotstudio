// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>How many rows of one table a capture of a bot reads and when the most recently modified of them was modified.</summary>
/// <param name="RowCount">How many rows there are.</param>
/// <param name="NewestModifiedOn">The latest modifiedon among the rows, or <see langword="null"/> when no row carries one.</param>
public sealed record AgentTableProbe(int RowCount, DateTimeOffset? NewestModifiedOn);

/// <summary>The rows a capture of one bot reads, summarized per table as plain values that stay equal only while none of those rows is changed, added or deleted.</summary>
/// <param name="Bot">The bot's own row.</param>
/// <param name="ComponentCollections">The component collections linked to the bot.</param>
/// <param name="Components">The components of the bot and of those component collections.</param>
/// <param name="Flows">The workflows those components other than test cases use.</param>
/// <param name="Prompts">The AI prompts those components other than test cases name.</param>
/// <param name="PromptConfigurations">The configurations of those AI prompts.</param>
/// <param name="ConnectionReferences">The connection references those components use.</param>
/// <param name="CustomConnectors">The unmanaged custom connectors those connection references name.</param>
/// <param name="EnvironmentVariables">The environment variable definitions those components use.</param>
/// <param name="EnvironmentVariableValues">The values of those environment variable definitions.</param>
public sealed record AgentChangeProbe(AgentTableProbe Bot, AgentTableProbe ComponentCollections, AgentTableProbe Components, AgentTableProbe Flows, AgentTableProbe Prompts, AgentTableProbe PromptConfigurations, AgentTableProbe ConnectionReferences, AgentTableProbe CustomConnectors, AgentTableProbe EnvironmentVariables, AgentTableProbe EnvironmentVariableValues);

/// <summary>Reads the change probe of a bot in a few small requests.</summary>
public interface IAgentChangeProbeDataverseClient : IDataverseEnvironmentTarget
{
    /// <summary>Reads the row count and the newest modifiedon of each table a capture of the bot reads, or returns <see langword="null"/> when the environment holds no such bot.</summary>
    /// <param name="agentId">The bot's identifier, or <see langword="null"/> to find it by its schema name.</param>
    /// <param name="schemaName">The bot's schema name, which finds it when no identifier is given.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AgentChangeProbe?> ProbeAgentAsync(Guid? agentId, string schemaName, CancellationToken cancellationToken);
}
