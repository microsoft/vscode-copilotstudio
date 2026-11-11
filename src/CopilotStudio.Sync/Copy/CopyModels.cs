// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Copy;

/// <summary>The kinds of identity a copy maps from its source.</summary>
public static class CopyIdentityKinds
{
    /// <summary>The agent's root schema name, from which owned component schemas derive.</summary>
    public const string Schema = "schema";

    /// <summary>A Dataverse workflow identifier.</summary>
    public const string Workflow = "workflow";

    /// <summary>An AI Builder model identifier.</summary>
    public const string AIModel = "aiModel";

    /// <summary>A custom connector row identifier.</summary>
    public const string Connector = "connector";

    /// <summary>A custom connector internal identifier, which connection references name.</summary>
    public const string ConnectorInternalId = "connectorInternalId";

    /// <summary>A connection reference logical name.</summary>
    public const string ConnectionReference = "connectionReference";
}

/// <summary>One source identity and the destination identity the copy gives it.</summary>
/// <param name="Kind">One of <see cref="CopyIdentityKinds"/>.</param>
/// <param name="Source">The source identity.</param>
/// <param name="Destination">The identity the copy uses instead.</param>
public sealed record CopyIdentityMapping(string Kind, string Source, string Destination);

/// <summary>What <see cref="IWorkspaceCopyService.PrepareAsync"/> needs to turn a copied workspace into a new agent.</summary>
public sealed record CopyPreparationRequest
{
    /// <summary>The staged copy of the source workspace, without its cloud cache, which is rewritten in place.</summary>
    public string WorkspacePath { get; init; } = string.Empty;

    /// <summary>The schema name the source agent is expected to carry, when known.</summary>
    public string? SourceSchemaName { get; init; }

    /// <summary>The new agent's display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The new agent's root schema name.</summary>
    public string SchemaName { get; init; } = string.Empty;

    /// <summary>The source's captured cloud definition (<c>botdefinition.json</c>), when one exists.</summary>
    public string? SourceDefinitionPath { get; init; }

    /// <summary>Whether the source's content was captured in the environment the copy deploys to, so its custom connector suffix already fits.</summary>
    public bool CapturedInTargetEnvironment { get; init; }

    /// <summary>Reads the environment the copy deploys to, already connected by the host, for its custom connector suffix and to check that the copy's new identities are free there.</summary>
    public ICopyDataverseClient? TargetEnvironment { get; init; }
}

/// <summary>What preparation wrote.</summary>
public sealed record CopyPreparationResult
{
    /// <summary>Every source identity the copy replaced, with its replacement.</summary>
    public IReadOnlyList<CopyIdentityMapping> Identities { get; init; } = Array.Empty<CopyIdentityMapping>();

    /// <summary>The transform's notices merged with the audits of the prepared workspace and the target environment.</summary>
    public CopyAuditReport Audit { get; init; } = new();

    /// <summary>Workspace files whose content the transform changed, relative to the workspace root.</summary>
    public IReadOnlyList<string> RewrittenFiles { get; init; } = Array.Empty<string>();

    /// <summary>Workspace files and folders the transform renamed, as source and destination relative paths.</summary>
    public IReadOnlyList<CopyIdentityMapping> RenamedPaths { get; init; } = Array.Empty<CopyIdentityMapping>();

    /// <summary>Copied workflows by name, which start as drafts and wait for activation.</summary>
    public IReadOnlyList<string> DraftWorkflows { get; init; } = Array.Empty<string>();
}

/// <summary>Stable codes for <see cref="CopyAuditFinding"/>.</summary>
public static class CopyAuditCodes
{
    /// <summary>A component's effective schema does not derive from the new root schema.</summary>
    public const string ForeignComponentSchema = "foreign-component-schema";

    /// <summary>A schema name exceeds the platform limit.</summary>
    public const string SchemaTooLong = "schema-too-long";

    /// <summary>A source identity remains in a typed field.</summary>
    public const string LeftoverSourceIdentity = "leftover-source-identity";

    /// <summary>A reference names a workflow, prompt, or connector the workspace does not hold.</summary>
    public const string UnresolvedReference = "unresolved-reference";

    /// <summary>A resource kind the copy cannot copy yet.</summary>
    public const string UnsupportedResource = "unsupported-resource";

    /// <summary>An identity the copy would create already exists in the environment.</summary>
    public const string IdentityTaken = "identity-taken";

    /// <summary>The workspace could not be read as push reads it.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The agent's display name breaks a rule Copilot Studio enforces.</summary>
    public const string AgentNameUnsupported = "agent-name-unsupported";
}

/// <summary>One problem the effective-identity audit found.</summary>
/// <param name="Code">One of <see cref="CopyAuditCodes"/>.</param>
/// <param name="Message">The problem, described for the operator.</param>
/// <param name="Path">The workspace file the problem is in, relative to the workspace root, when it is in one.</param>
public sealed record CopyAuditFinding(string Code, string Message, string? Path = null);

/// <summary>The outcome of the effective-identity audit.</summary>
public sealed record CopyAuditReport
{
    /// <summary>Problems that stop the copy.</summary>
    public IReadOnlyList<CopyAuditFinding> Blockers { get; init; } = Array.Empty<CopyAuditFinding>();

    /// <summary>Advisories that do not stop anything.</summary>
    public IReadOnlyList<CopyAuditFinding> Notices { get; init; } = Array.Empty<CopyAuditFinding>();

    /// <summary>The effective schema name of every component the workspace defines, as push derives them.</summary>
    public IReadOnlyList<string> ComponentSchemaNames { get; init; } = Array.Empty<string>();

    /// <summary>Whether the audit found nothing that stops the copy.</summary>
    public bool Passed => Blockers.Count == 0;

    /// <summary>Combines this report with another.</summary>
    /// <param name="other">The report to add.</param>
    /// <returns>A report holding the findings of both.</returns>
    public CopyAuditReport Merge(CopyAuditReport other) => new()
    {
        Blockers = Blockers.Concat(other.Blockers).ToArray(),
        Notices = Notices.Concat(other.Notices).ToArray(),
        ComponentSchemaNames = ComponentSchemaNames.Count > 0 ? ComponentSchemaNames : other.ComponentSchemaNames,
    };
}

/// <summary>Raised when a workspace cannot be copied.</summary>
public sealed class CopyPreparationException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="findings">Everything that stopped the copy.</param>
    public CopyPreparationException(IReadOnlyList<CopyAuditFinding> findings) : base(Describe(findings))
    {
        Findings = findings;
    }

    /// <summary>Everything that stopped the copy.</summary>
    public IReadOnlyList<CopyAuditFinding> Findings { get; }

    private static string Describe(IReadOnlyList<CopyAuditFinding> findings) => "The workspace cannot be copied: " + string.Join(" ", findings.Select(finding => finding.Path is null ? finding.Message : $"{finding.Message} ({finding.Path})"));
}
