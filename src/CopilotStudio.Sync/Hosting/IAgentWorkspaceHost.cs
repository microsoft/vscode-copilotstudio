// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Hosting;

/// <summary>Validates, reads, finds, probes, clones and pushes agents and standalone workflows entirely in memory, one isolated session and one Dataverse organization per call.</summary>
public interface IAgentWorkspaceHost
{
    /// <summary>Checks a snapshot against the layout and declaration rules without touching any environment.</summary>
    /// <param name="snapshot">The snapshot to check.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<AgentWorkspaceValidation> ValidateAsync(AgentWorkspaceSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Reads the identity the agent snapshot's folder declares, returning <see langword="null"/> when it declares no schema name and throwing <see cref="AgentWorkspaceHostException"/> with the code SnapshotInvalid when the snapshot breaks a rule, as a standalone workflow snapshot does.</summary>
    /// <param name="snapshot">The agent folder and the collection folders it declares.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AgentWorkspaceIdentity?> ReadIdentityAsync(AgentWorkspaceSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Reads the identifier and name the standalone workflow snapshot's metadata declares, returning <see langword="null"/> when the metadata cannot be read, names another workflow or declares no name, and throwing <see cref="AgentWorkspaceHostException"/> with the code SnapshotInvalid when the snapshot breaks a rule, as one that holds no workflow with its metadata, several workflows or an agent does.</summary>
    /// <param name="snapshot">The workflow folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<WorkflowWorkspaceIdentity?> ReadWorkflowIdentityAsync(AgentWorkspaceSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Returns the identifier of the bot that carries the target's schema name in the target's Dataverse organization, or <see langword="null"/> when none does, without changing anything.</summary>
    /// <param name="target">The bot to find, named by its schema name.</param>
    /// <param name="authentication">Supplies tokens for the target's Dataverse organization only.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<string?> FindAgentAsync(AgentSyncTarget target, ISyncAuthProvider authentication, CancellationToken cancellationToken);

    /// <summary>Returns the identifier of the standalone workflow the target names when the target's Dataverse organization holds it, or <see langword="null"/> when it does not, in one read and without changing anything, throwing <see cref="AgentWorkspaceHostException"/> with the code LookupFailed when the environment fails the read.</summary>
    /// <param name="target">The workflow to find, named by its identifier.</param>
    /// <param name="authentication">Supplies tokens for the target's Dataverse organization only.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<string?> FindWorkflowAsync(WorkflowSyncTarget target, ISyncAuthProvider authentication, CancellationToken cancellationToken);

    /// <summary>Reads the change probe of the target bot, found by its identifier or else by its schema name, without changing anything, throwing <see cref="AgentWorkspaceHostException"/> with the code BotNotFound when the environment holds no such bot and LookupFailed when the environment fails the read.</summary>
    /// <param name="target">The bot to probe.</param>
    /// <param name="authentication">Supplies tokens for the target's Dataverse organization only.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AgentChangeProbe> ProbeAsync(AgentSyncTarget target, ISyncAuthProvider authentication, CancellationToken cancellationToken);

    /// <summary>Clones a bot and the component collections linked to it, returning folders without any sync state.</summary>
    /// <param name="request">The bot to clone, the folders to clone into and whether the clone reads the bot's change probe.</param>
    /// <param name="authentication">Supplies tokens for the source's Dataverse organization only.</param>
    /// <param name="cancellationToken">Cancels the clone between steps.</param>
    Task<AgentCloneResult> CloneAsync(AgentCloneRequest request, ISyncAuthProvider authentication, CancellationToken cancellationToken);

    /// <summary>Refuses with SnapshotInvalid and a DisplayNameTooLong diagnostic before any call a push that may create the bot, having no bot id with CreateIfMissing set, under the target's display name or the one the snapshot declares when either holds more than <see cref="AgentWorkspaceIdentity.MaximumDisplayNameLength"/> characters, and otherwise waits until Copilot Studio no longer reports the target bot as provisioning unless it was created from a CLI agent template, which Copilot Studio provisions only on its first publish, failing with PushFailed and an AgentNotProvisioned diagnostic before writing anything when it still does after the provisioning timeout, then makes the bot and its declared component collections match the snapshot, with every name the agent folder derives from the schema name it declares taking the target's schema name instead, activates eligible workflows and publishes the bot unless the options say not to.</summary>
    /// <param name="snapshot">The agent folder and the collection folders it declares.</param>
    /// <param name="target">The bot to push to.</param>
    /// <param name="options">Whether a missing bot is created, the bot published and its change probe read, how long the push waits for Copilot Studio to provision and publish the bot, and what is told of each stage the push enters.</param>
    /// <param name="authentication">Supplies tokens for the target's Dataverse organization only.</param>
    /// <param name="cancellationToken">Cancels the push between steps.</param>
    Task<AgentPushResult> PushAsync(AgentWorkspaceSnapshot snapshot, AgentSyncTarget target, AgentPushOptions options, ISyncAuthProvider authentication, CancellationToken cancellationToken);

    /// <summary>Checks a standalone workflow snapshot against the layout rules and the rule that its folder holds exactly one workflow and no agent, without touching any environment.</summary>
    /// <param name="snapshot">The workflow folder to check.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<WorkflowWorkspaceValidation> ValidateWorkflowAsync(AgentWorkspaceSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Clones a standalone workflow into a folder without any sync state.</summary>
    /// <param name="request">The workflow to clone and the top-level folder it is written to.</param>
    /// <param name="authentication">Supplies tokens for the source's Dataverse organization only.</param>
    /// <param name="cancellationToken">Cancels the clone between steps.</param>
    Task<WorkflowCloneResult> CloneWorkflowAsync(WorkflowCloneRequest request, ISyncAuthProvider authentication, CancellationToken cancellationToken);

    /// <summary>Makes the target workflow match the snapshot, inserting it with its folder's identifier when missing, then activates it when eligible.</summary>
    /// <param name="snapshot">The workflow folder.</param>
    /// <param name="target">The workflow to push to.</param>
    /// <param name="options">What is told of each stage the push enters.</param>
    /// <param name="authentication">Supplies tokens for the target's Dataverse organization only.</param>
    /// <param name="cancellationToken">Cancels the push between steps.</param>
    Task<WorkflowPushResult> PushWorkflowAsync(AgentWorkspaceSnapshot snapshot, WorkflowSyncTarget target, AgentPushOptions options, ISyncAuthProvider authentication, CancellationToken cancellationToken);
}
