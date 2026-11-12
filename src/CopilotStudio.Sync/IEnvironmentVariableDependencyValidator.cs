// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Checks that a target environment can resolve every environment variable a workspace references and accept the workspace's environment variable changes.</summary>
public interface IEnvironmentVariableDependencyValidator
{
    /// <summary>Reports references that neither the workspace files nor the target environment provide and local changes to environment variables the target environment manages, and lists the shared environment variables whose target values the local changes would overwrite.</summary>
    /// <param name="workspaceFolder">The root of the workspace.</param>
    /// <param name="workspaceDefinition">The workspace definition to check.</param>
    /// <param name="isRetarget">Whether the workspace is retargeted from the environment its cloud cache came from, so shared environment variable files that match that cache count as unedited.</param>
    /// <param name="dataverseClient">The client bound to the target environment.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<EnvironmentVariableDependencyCheck> ValidateEnvironmentVariableDependenciesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, bool isRetarget, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken);
}

/// <summary>The problems that block a workspace's environment variables in a target environment, and the shared environment variables whose target values its local changes would overwrite.</summary>
public readonly record struct EnvironmentVariableDependencyCheck(ImmutableArray<WorkspaceDiagnostic> Diagnostics, ImmutableArray<string> OverwrittenSchemaNames)
{
    /// <summary>A check that found nothing to report.</summary>
    public static EnvironmentVariableDependencyCheck None { get; } = new(ImmutableArray<WorkspaceDiagnostic>.Empty, ImmutableArray<string>.Empty);
}
