// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net.Http;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>How waiting for Copilot Studio to finish publishing an agent ended.</summary>
public enum AgentPublishOutcomeKind
{
    /// <summary>Copilot Studio finished the publish with the status Succeeded.</summary>
    Succeeded,

    /// <summary>Copilot Studio finished the publish with another status, or its progress could not be read.</summary>
    Failed,

    /// <summary>Copilot Studio did not finish the publish within the timeout, though it may still complete, or was still provisioning a bot it provisions on its own when the timeout passed, so no publish was asked for.</summary>
    TimedOut,
}

/// <summary>How waiting for Copilot Studio to finish publishing an agent ended.</summary>
/// <param name="Kind">Whether the publish succeeded, failed or did not finish within the timeout.</param>
/// <param name="Status">The status Copilot Studio finished the publish with, or <see langword="null"/> when it reported none.</param>
/// <param name="FailureReason">Why the publish's progress could not be read or the publish was not asked for, naming no service response, or <see langword="null"/> when neither happened.</param>
public sealed record AgentPublishOutcome(AgentPublishOutcomeKind Kind, string? Status, string? FailureReason)
{
    /// <summary>When Copilot Studio finished the publish, or <see langword="null"/> when it did not finish or reported no time.</summary>
    public DateTimeOffset? FinishedOn { get; init; }
}

/// <summary>Publishes agents and waits for Copilot Studio to finish each publish.</summary>
public interface IAgentPublishService
{
    /// <summary>Waits until Copilot Studio no longer reports the agent's bot as provisioning unless it was created from a CLI agent template, which the publish itself provisions, then asks it to publish the agent's current content, once more after five seconds when Dataverse refuses the first request, and waits until it finishes or the timeout passes, throwing only when the publish could not be asked for and never cancelling one that was.</summary>
    /// <param name="agentId">The agent to publish.</param>
    /// <param name="timeout">How long to wait for Copilot Studio to finish provisioning a bot it provisions on its own before the publish is asked for, and again for the publish to finish once it was, which must be positive.</param>
    /// <param name="cancellationToken">Stops the request or the wait, leaving a publish already asked for to finish.</param>
    Task<AgentPublishOutcome> PublishAsync(Guid agentId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Publishes agents through the publish client, polling the bot's last finished publish until Copilot Studio reports a new one.</summary>
public sealed class AgentPublishService : IAgentPublishService
{
    private const string SucceededStatus = "Succeeded";

    private const int TooManyRequests = 429;

    internal static readonly TimeSpan PublishRetryDelay = TimeSpan.FromSeconds(5);

    private readonly IAgentPublishDataverseClient _publisher;

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="publisher">The publish client, already pointed at the agent's environment.</param>
    /// <param name="timeProvider">The clock that times the polls and the timeout.</param>
    public AgentPublishService(IAgentPublishDataverseClient publisher, TimeProvider timeProvider)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<AgentPublishOutcome> PublishAsync(Guid agentId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A publish timeout must be positive.");
        }

        if (!await AgentProvisioning.WaitAsync(_publisher as IAgentProvisioningDataverseClient, agentId, timeout, _timeProvider, cancellationToken).ConfigureAwait(false))
        {
            return new AgentPublishOutcome(AgentPublishOutcomeKind.TimedOut, null, $"Copilot Studio was still provisioning the agent after {timeout}, so no publish was asked for.");
        }

        var baseline = await RequestPublishAsync(agentId, cancellationToken).ConfigureAwait(false);
        AgentPublishOutcome? outcome = null;

        return await CopilotStudioPolling.UntilAsync(_timeProvider, CopilotStudioPolling.EverySecond, timeout, async token => (outcome = await PollAsync(agentId, baseline, token).ConfigureAwait(false)) is not null, cancellationToken).ConfigureAwait(false) && outcome is { } finished ? finished : new AgentPublishOutcome(AgentPublishOutcomeKind.TimedOut, null, null);
    }

    private static AgentPublishOutcome Finished(AgentPublishOperation operation) => new(string.Equals(operation.Status, SucceededStatus, StringComparison.OrdinalIgnoreCase) ? AgentPublishOutcomeKind.Succeeded : AgentPublishOutcomeKind.Failed, operation.Status, null) { FinishedOn = operation.OperationEnd };

    private static bool IsTransient(Exception failure) => failure is HttpRequestException or IOException or OperationCanceledException || (failure is DataverseRequestException dataverseFailure && ((int)dataverseFailure.StatusCode == TooManyRequests || (int)dataverseFailure.StatusCode >= 500));

    private static string DescribeUnreadableProgress(Exception failure) => failure switch
    {
        DataverseRequestException dataverseFailure => $"The request for the publish's progress failed with {dataverseFailure.Describe()}",
        FormatException => "The bot's synchronization status is not in a form Copilot Studio is known to write.",
        _ => $"The request for the publish's progress failed with {failure.GetType().Name}.",
    };

    private async Task<AgentPublishOperation?> RequestPublishAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var baseline = await _publisher.GetLastPublishOperationAsync(agentId, cancellationToken).ConfigureAwait(false);

        try
        {
            await _publisher.PublishAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (DataverseRequestException)
        {
            await DelayAsync(PublishRetryDelay, cancellationToken).ConfigureAwait(false);
            baseline = await _publisher.GetLastPublishOperationAsync(agentId, cancellationToken).ConfigureAwait(false);
            await _publisher.PublishAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        }

        return baseline;
    }

    private async Task<AgentPublishOutcome?> PollAsync(Guid agentId, AgentPublishOperation? baseline, CancellationToken cancellationToken)
    {
        try
        {
            return await _publisher.GetLastPublishOperationAsync(agentId, cancellationToken).ConfigureAwait(false) is { } operation && operation != baseline ? Finished(operation) : null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return IsTransient(failure) ? null : new AgentPublishOutcome(AgentPublishOutcomeKind.Failed, null, DescribeUnreadableProgress(failure));
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => TimeProviderDelay.WaitAsync(_timeProvider, delay, cancellationToken);
}
