// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>A connected agent or component-collection workspace to push, and how the host wants it pushed.</summary>
public sealed class WorkspacePushRequest
{
    /// <summary>The workspace folder, already connected to the agent or component collection it pushes to.</summary>
    public DirectoryPath WorkspaceFolder { get; init; }

    /// <summary>Whether local changes replace components, custom connectors and AI prompts that also changed in the cloud instead of failing the push.</summary>
    public bool Overwrite { get; init; }

    internal IReadOnlyList<BotComponentCollection> DeclaredCollections { get; init; } = Array.Empty<BotComponentCollection>();

    internal bool DiscardRefusedWorkflows { get; init; }

    internal TimeSpan ProvisioningTimeout { get; init; } = AgentProvisioning.DefaultTimeout;

    internal bool AgentProvisioned { get; init; }

    internal Action<string>? StageStarted { get; init; }
}

/// <summary>What a workspace push sent, what the service reported for it, and what the push could not finish.</summary>
public sealed class WorkspacePushResult
{
    /// <summary>The changes the workspace held when the push started, without those of the custom connectors, workflows and AI prompts the service refused.</summary>
    public ImmutableArray<Change> Changes { get; init; } = ImmutableArray<Change>.Empty;

    /// <summary>How many workflows the push wrote that Changes does not already report.</summary>
    public int AuxiliaryChangeCount { get; init; }

    /// <summary>The knowledge files the push uploaded.</summary>
    public ImmutableArray<string> UploadedFileNames { get; init; } = ImmutableArray<string>.Empty;

    internal DefinitionBase Definition { get; init; } = new BotDefinition();

    /// <summary>When the bot was last published as the push read it from its cloud copy, or <see langword="null"/> for a bot never published or a component collection.</summary>
    public DateTimeOffset? PublishedOn { get; init; }

    /// <summary>The custom connectors, workflows and AI prompts the service refused or left disabled.</summary>
    public IReadOnlyList<WorkspacePushDiagnostic> Diagnostics { get; init; } = Array.Empty<WorkspacePushDiagnostic>();
}

/// <summary>One finding of a workspace push.</summary>
/// <param name="Code">The stable code from <see cref="WorkspacePushDiagnosticCodes"/>.</param>
/// <param name="Subject">The custom connector, workflow or AI prompt, or the kind of change, the finding concerns, or <see langword="null"/> when it concerns the whole push.</param>
/// <param name="Detail">The service's explanation, with any Dataverse failure described by its status and reported error rather than its raw response, or <see langword="null"/> when there is none.</param>
/// <param name="Message">The finding as a sentence.</param>
public sealed record WorkspacePushDiagnostic(string Code, string? Subject, string? Detail, string Message);

/// <summary>The stable codes a <see cref="WorkspacePushDiagnostic"/> carries.</summary>
public static class WorkspacePushDiagnosticCodes
{
    /// <summary>The service refused a custom connector the push wrote.</summary>
    public const string ConnectorRefused = "ConnectorRefused";

    /// <summary>The service refused a workflow the push wrote.</summary>
    public const string WorkflowRefused = "WorkflowRefused";

    /// <summary>The service stored a workflow but left it disabled.</summary>
    public const string WorkflowNotRunning = "WorkflowNotRunning";

    /// <summary>The service refused an AI prompt the push wrote.</summary>
    public const string PromptRefused = "PromptRefused";
}