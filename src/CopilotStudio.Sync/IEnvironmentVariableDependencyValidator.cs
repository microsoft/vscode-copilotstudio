// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Checks that a target environment can resolve every environment variable a workspace references.</summary>
public interface IEnvironmentVariableDependencyValidator
{
    /// <summary>Returns a diagnostic for each component reference to an environment variable that neither the workspace files nor the target environment provide.</summary>
    /// <param name="workspaceFolder">The root of the workspace.</param>
    /// <param name="workspaceDefinition">The workspace definition to check.</param>
    /// <param name="dataverseClient">The client bound to the target environment.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<ImmutableArray<WorkspaceDiagnostic>> ValidateEnvironmentVariableDependenciesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken);
}
