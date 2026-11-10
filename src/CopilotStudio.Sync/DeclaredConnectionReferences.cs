// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

internal static class DeclaredConnectionReferences
{
    public static ImmutableArray<ConnectionReference> Read(IFileAccessor workspace)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        var references = ImmutableArray.CreateBuilder<ConnectionReference>();
        var declaredBy = new Dictionary<string, (int Index, string Path, string ConnectorId)>(StringComparer.OrdinalIgnoreCase);

        foreach (var declarationPath in DeclarationPaths(workspace))
        {
            foreach (var reference in Parse(workspace, declarationPath))
            {
                var logicalName = reference.ConnectionReferenceLogicalName.Value;
                var connectorId = NormalizeConnectorId(reference.ConnectorId.Value);

                if (!declaredBy.TryGetValue(logicalName, out var first))
                {
                    declaredBy.Add(logicalName, (references.Count, declarationPath, connectorId));
                    references.Add(reference);
                }
                else if (first.ConnectorId.Length == 0)
                {
                    declaredBy[logicalName] = (first.Index, declarationPath, connectorId);
                    references[first.Index] = reference;
                }
                else if (connectorId.Length > 0 && !first.ConnectorId.Equals(connectorId, StringComparison.OrdinalIgnoreCase))
                {
                    throw DeclaredConnectionReferenceException.Conflict(logicalName, first.Path, first.ConnectorId, declarationPath, connectorId);
                }
            }
        }

        return references.ToImmutable();
    }

    public static async Task<ImmutableArray<ConnectionReference>> ProvisionAsync(IFileAccessor workspace, IWorkspaceSynchronizer synchronizer, ISyncDataverseClient dataverseClient, IReadOnlyDictionary<string, Guid> pushedConnectorIds, CancellationToken cancellationToken)
    {
        if (synchronizer is null)
        {
            throw new ArgumentNullException(nameof(synchronizer));
        }

        if (dataverseClient is null)
        {
            throw new ArgumentNullException(nameof(dataverseClient));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var declared = Read(workspace);

        if (declared.IsDefaultOrEmpty)
        {
            return ImmutableArray<ConnectionReference>.Empty;
        }

        await synchronizer.ProvisionConnectionReferencesAsync(BotDefinition.Empty.WithConnectionReferences(declared), dataverseClient, cancellationToken, pushedConnectorIds).ConfigureAwait(false);

        return await ConfirmAsync(declared, dataverseClient, cancellationToken).ConfigureAwait(false);
    }

    [return: NotNullIfNotNull(nameof(reported))]
    public static CloudFlowMetadata? WithProvisioned(CloudFlowMetadata? reported, ImmutableArray<ConnectionReference> provisioned)
    {
        if (provisioned.IsDefaultOrEmpty)
        {
            return reported;
        }

        return new CloudFlowMetadata
        {
            Workflows = reported?.Workflows ?? ImmutableArray<CloudFlowDefinition>.Empty,
            ConnectionReferences = Merge(provisioned, reported?.ConnectionReferences ?? ImmutableArray<ConnectionReference>.Empty),
            Succeeded = reported?.Succeeded ?? true,
            WorkflowsReported = reported?.WorkflowsReported ?? false,
        };
    }

    public static ImmutableArray<ConnectionReference> Merge(ImmutableArray<ConnectionReference> preferred, ImmutableArray<ConnectionReference> additional)
    {
        var merged = ImmutableArray.CreateBuilder<ConnectionReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in (preferred.IsDefault ? ImmutableArray<ConnectionReference>.Empty : preferred).Concat(additional.IsDefault ? ImmutableArray<ConnectionReference>.Empty : additional))
        {
            if (seen.Add(reference.ConnectionReferenceLogicalName.Value))
            {
                merged.Add(reference);
            }
        }

        return merged.ToImmutable();
    }

    internal static async Task<ImmutableArray<ConnectionReference>> ConfirmAsync(ImmutableArray<ConnectionReference> declared, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        var byName = (await dataverseClient.GetConnectionReferencesByLogicalNamesAsync(declared.Select(reference => reference.ConnectionReferenceLogicalName.Value).ToArray(), cancellationToken).ConfigureAwait(false)).Where(row => !string.IsNullOrWhiteSpace(row.ConnectionReferenceLogicalName)).ToDictionary(row => row.ConnectionReferenceLogicalName, StringComparer.OrdinalIgnoreCase);
        var unconfirmed = declared.Where(reference => !IsServedAsDeclared(reference, byName)).Select(reference => reference.ConnectionReferenceLogicalName.Value).ToArray();

        if (unconfirmed.Length > 0)
        {
            throw DeclaredConnectionReferenceException.NotConfirmed(unconfirmed);
        }

        return declared.Select(reference => WorkspaceSynchronizer.ToConnectionReference(byName[reference.ConnectionReferenceLogicalName.Value])).ToImmutableArray();
    }

    private static bool IsServedAsDeclared(ConnectionReference declared, Dictionary<string, SyncDataverseClient.ConnectionReferenceInfo> rows) =>
        rows.TryGetValue(declared.ConnectionReferenceLogicalName.Value, out var row) && row.ConnectionReferenceId != Guid.Empty && ServesConnector(declared.ConnectorId.Value, row.ConnectorId);

    private static bool ServesConnector(string? declared, string? confirmed)
    {
        var requested = NormalizeConnectorId(declared);
        var served = NormalizeConnectorId(confirmed);

        if (requested.Length == 0 || served.Length == 0)
        {
            return false;
        }

        return requested.Equals(served, StringComparison.OrdinalIgnoreCase) || (WorkspaceSynchronizer.GetStableConnectorPrefix(requested) is { } prefix && served.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeConnectorId(string? connectorId) => SyncDataverseClient.ExtractConnectorInternalId(connectorId?.Trim().TrimEnd('/')) ?? string.Empty;

    private static IEnumerable<string> DeclarationPaths(IFileAccessor workspace)
    {
        if (workspace.Exists(ConnectionReferenceStore.FlatPath))
        {
            yield return ConnectionReferenceStore.FlatFileName;
        }

        foreach (var path in CliAgentConnectionsWriter.Enumerate(workspace).Select(file => file.Path.ToString()).OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    private static ImmutableArray<ConnectionReference> Parse(IFileAccessor workspace, string declarationPath)
    {
        var content = ReadDeclaration(workspace, declarationPath);

        try
        {
            using var serializationContext = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: true);
            var parsed = CodeSerializer.Deserialize<ConnectionReferencesSourceFile>(content, sourceUri: null);

            if (parsed is null || parsed.ConnectionReferences.IsDefaultOrEmpty)
            {
                return ImmutableArray<ConnectionReference>.Empty;
            }

            return parsed.ConnectionReferences.Any(reference => string.IsNullOrWhiteSpace(reference?.ConnectionReferenceLogicalName.Value))
                ? throw DeclaredConnectionReferenceException.Unreadable(declarationPath, "a declared reference has no connection reference logical name")
                : parsed.ConnectionReferences;
        }
        catch (Exception failure) when (failure is not DeclaredConnectionReferenceException and not OperationCanceledException)
        {
            throw DeclaredConnectionReferenceException.Unreadable(declarationPath, failure.Message, failure);
        }
    }

    private static string ReadDeclaration(IFileAccessor workspace, string declarationPath)
    {
        try
        {
            using var stream = workspace.OpenRead(new AgentFilePath(declarationPath));
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw DeclaredConnectionReferenceException.Unreadable(declarationPath, failure.Message, failure);
        }
    }
}
