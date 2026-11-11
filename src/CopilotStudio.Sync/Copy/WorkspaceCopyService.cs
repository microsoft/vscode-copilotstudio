// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Copy;

/// <summary>Prepares a copied agent workspace as a new agent of its own.</summary>
public interface IWorkspaceCopyService
{
    /// <summary>Rewrites a staged workspace into a new agent and audits the result, reading only the target environment the request names.</summary>
    /// <param name="request">What to copy, and into what.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>What was written, and the audit that passed.</returns>
    /// <exception cref="CopyPreparationException">The workspace cannot be copied; the caller discards the staged copy.</exception>
    Task<CopyPreparationResult> PrepareAsync(CopyPreparationRequest request, CancellationToken cancellationToken);

    /// <summary>Proposes a schema name for a copy that keeps every component schema of the staged workspace within the Dataverse limit.</summary>
    /// <param name="workspacePath">The staged workspace whose components the copy renames.</param>
    /// <param name="sourceSchemaName">The source agent's schema name, whose publisher prefix the proposal keeps.</param>
    /// <param name="displayName">The copy's display name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The proposed schema name, or <see langword="null"/> when no name fits.</returns>
    Task<string?> ProposeSchemaNameAsync(string workspacePath, string sourceSchemaName, string displayName, CancellationToken cancellationToken);
}

internal sealed class WorkspaceCopyService : IWorkspaceCopyService
{
    private const int MinimumProposedSchemaNameLength = 32;

    private const int ProposedSchemaSuffixLength = 6;

    private readonly CopyIdentityAuditor _auditor;

    public WorkspaceCopyService(WorkspaceSynchronizer synchronizer)
    {
        _auditor = new CopyIdentityAuditor(synchronizer ?? throw new ArgumentNullException(nameof(synchronizer)));
    }

    public async Task<CopyPreparationResult> PrepareAsync(CopyPreparationRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var prepared = await WorkspaceCopyTransform.RunAsync(request, cancellationToken).ConfigureAwait(false);
        var audit = await _auditor.AuditWorkspaceAsync(request.WorkspacePath, prepared.Identities, cancellationToken).ConfigureAwait(false);

        if (audit.Passed && request.TargetEnvironment is { } environment)
        {
            audit = audit.Merge(await _auditor.AuditEnvironmentAsync(prepared.Identities, environment, cancellationToken).ConfigureAwait(false));
        }

        return audit.Passed ? prepared with { Audit = prepared.Audit.Merge(audit) } : throw new CopyPreparationException(audit.Blockers);
    }

    public async Task<string?> ProposeSchemaNameAsync(string workspacePath, string sourceSchemaName, string displayName, CancellationToken cancellationToken)
    {
        var stem = SchemaNameGenerator.GenerateAgentSchemaName(SchemaNameGenerator.PublisherPrefixOf(sourceSchemaName) ?? SchemaNameGenerator.DefaultPublisherPrefix, displayName, Math.Min(Math.Max(sourceSchemaName.Length, MinimumProposedSchemaNameLength), CopyIdentityAuditor.SchemaNameBudget((await _auditor.ReadWorkspaceAsync(workspacePath, validate: false, checkKnowledgeFiles: true, new List<CopyAuditFinding>(), cancellationToken).ConfigureAwait(false)).ComponentSchemas, sourceSchemaName)) - ProposedSchemaSuffixLength - 1);

        return stem is null ? null : $"{stem}_{SchemaNameGenerator.RandomString(ProposedSchemaSuffixLength, SchemaNameGenerator.LetterAlphabet)}";
    }
}
