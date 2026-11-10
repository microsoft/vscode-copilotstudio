// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

internal static class AgentProvisioning
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static void RequirePositive(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, timeout, "The provisioning timeout must be positive.");
        }
    }

    public static async Task<bool> WaitAsync(IAgentProvisioningDataverseClient? provisioning, Guid agentId, TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken) => provisioning is null || !await IsProvisioningAsync(provisioning, agentId, cancellationToken).ConfigureAwait(false) || await CopilotStudioPolling.UntilAsync(timeProvider, CopilotStudioPolling.Backoff, timeout, async token => !await IsProvisioningAsync(provisioning, agentId, token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public static async Task RequireProvisionedAsync(IAgentProvisioningDataverseClient? provisioning, Guid agentId, TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!await WaitAsync(provisioning, agentId, timeout, timeProvider, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkspacePushException(WorkspacePushErrorCodes.AgentNotProvisioned, $"Copilot Studio was still provisioning the classic bot '{agentId}' after {timeout}, so nothing was pushed into it. The bot is kept, and a later deploy pushes into the same bot once Copilot Studio finishes provisioning it.", null);
        }
    }

    private static async Task<bool> IsProvisioningAsync(IAgentProvisioningDataverseClient provisioning, Guid agentId, CancellationToken cancellationToken)
    {
        try
        {
            return await provisioning.GetProvisioningStatusAsync(agentId, cancellationToken).ConfigureAwait(false) is { } status && string.Equals(status.Status, BotSynchronizationStatus.Provisioning, StringComparison.OrdinalIgnoreCase) && !AgentClassifier.IsCliTemplate(status.Template);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}