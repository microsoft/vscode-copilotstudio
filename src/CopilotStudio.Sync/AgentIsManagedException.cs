// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Thrown when a clone targets a managed agent, which changes only through the solution that installed it.</summary>
public sealed class AgentIsManagedException : InvalidOperationException
{
    /// <summary>Creates the failure for the managed agent.</summary>
    public AgentIsManagedException(AgentInfo agentInfo)
        : base($"The agent '{agentInfo.DisplayName}' ({agentInfo.AgentId}) is managed. A managed agent changes only through the solution that installed it, so it cannot be cloned. Clone it from the environment where it is unmanaged.")
    {
        AgentId = agentInfo.AgentId;
        AgentName = agentInfo.DisplayName;
    }

    /// <summary>The identifier of the managed agent.</summary>
    public Guid AgentId { get; }

    /// <summary>The display name of the managed agent.</summary>
    public string AgentName { get; }

    /// <summary>Throws when the agent is managed, so a clone stops before it writes anything.</summary>
    public static void ThrowIfManaged(AgentInfo agentInfo)
    {
        if (agentInfo.IsManaged)
        {
            throw new AgentIsManagedException(agentInfo);
        }
    }
}
