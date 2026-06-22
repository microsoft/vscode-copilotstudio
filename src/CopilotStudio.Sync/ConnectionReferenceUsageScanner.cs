// Copyright (C) Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Microsoft.CopilotStudio.Sync;

internal static class ConnectionReferenceText
{
    private static readonly Regex ConnectionReferenceLine = new Regex(@"^[ \t]*connectionReference:[ \t]*(?:'(?<value>[^']*)'|""(?<value>[^""]*)""|(?<value>[^\s#'""]+))", RegexOptions.Compiled | RegexOptions.Multiline);

    public static IEnumerable<string> ExtractConnectionReferenceNames(string? yamlText)
    {
        if (string.IsNullOrEmpty(yamlText) || yamlText!.IndexOf("connectionReference:", StringComparison.OrdinalIgnoreCase) < 0)
        {
            yield break;
        }

        foreach (Match match in ConnectionReferenceLine.Matches(yamlText))
        {
            var value = match.Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }
}

public sealed class ConnectionReferenceUsageScanner
{
    private const string ComponentExtension = ".mcs.yml";
    private const string ComponentExtensionLong = ".mcs.yaml";
    private const string ConnectionReferencesFileName = "connectionreferences.mcs.yml";
    private const string ConnectionReferencesFileNameLong = "connectionreferences.mcs.yaml";
    private const string WorkflowsFolder = "workflows";
    private const string ConnectorsFolder = "connectors";
    private const string HiddenFolder = ".mcs";

    /// <summary>
    /// Scans the workspace for connection reference usages.
    /// </summary>
    /// <param name="fileAccessor">Accessor for the agent workspace files.</param>
    /// <param name="connectorInternalIdByLogicalName">Maps each declared connection reference logical name to its connector internal id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scan result.</returns>
    public ConnectionReferenceUsageScan Scan(IFileAccessor fileAccessor, IReadOnlyDictionary<string, string> connectorInternalIdByLogicalName, CancellationToken cancellationToken)
    {
        if (fileAccessor == null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        connectorInternalIdByLogicalName ??= ImmutableDictionary<string, string>.Empty;
        var usages = new Dictionary<string, List<ConnectionReferenceUsage>>(StringComparer.OrdinalIgnoreCase);
        var authored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workflows = new List<ScannedWorkflow>();

        var allFiles = fileAccessor.ListFiles().ToList();

        ScanComponents(fileAccessor, allFiles, usages, authored, cancellationToken);
        ScanWorkflows(fileAccessor, allFiles, usages, workflows, cancellationToken);
        ScanConnectors(fileAccessor, allFiles, connectorInternalIdByLogicalName, usages, cancellationToken);

        return new ConnectionReferenceUsageScan(usages.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableArray(), StringComparer.OrdinalIgnoreCase), authored.ToImmutableArray(), workflows.ToImmutableArray());
    }

    private static void ScanComponents(IFileAccessor fileAccessor, IReadOnlyList<AgentFilePath> allFiles, Dictionary<string, List<ConnectionReferenceUsage>> usages, HashSet<string> authored, CancellationToken cancellationToken)
    {
        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizePath(file.ToString());

            if (!path.EndsWith(ComponentExtension, StringComparison.OrdinalIgnoreCase) && !path.EndsWith(ComponentExtensionLong, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsUnder(path, HiddenFolder) || IsConnectionReferencesFile(path))
            {
                continue;
            }

            var content = ReadText(fileAccessor, file);
            foreach (var value in ConnectionReferenceText.ExtractConnectionReferenceNames(content))
            {
                authored.Add(value);
                AddUsage(usages, value, new ConnectionReferenceUsage
                {
                    LogicalName = value,
                    FilePath = path,
                    Kind = ClassifyComponent(path),
                    DisplayName = GetFileDisplayName(path),
                });
            }
        }
    }

    private static void ScanWorkflows(IFileAccessor fileAccessor, IReadOnlyList<AgentFilePath> allFiles, Dictionary<string, List<ConnectionReferenceUsage>> usages, List<ScannedWorkflow> workflows, CancellationToken cancellationToken)
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

        foreach (var (path, content) in EnumerateMetadataFiles(fileAccessor, allFiles, WorkflowsFolder, cancellationToken))
        {
            SyncDataverseClient.WorkflowMetadata? metadata;
            try
            {
                metadata = deserializer.Deserialize<SyncDataverseClient.WorkflowMetadata>(content);
            }
            catch (YamlDotNet.Core.YamlException)
            {
                continue;
            }

            if (metadata == null)
            {
                continue;
            }

            var connectionNames = (metadata.ConnectionReferences ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            if (connectionNames.IsEmpty)
            {
                connectionNames = ReadWorkflowJsonConnectionReferences(fileAccessor, path, cancellationToken);
            }

            var displayName = !string.IsNullOrWhiteSpace(metadata.Name) ? metadata.Name! : GetFileDisplayName(path);

            workflows.Add(new ScannedWorkflow
            {
                WorkflowId = metadata.WorkflowId == Guid.Empty ? string.Empty : metadata.WorkflowId.ToString(),
                DisplayName = displayName,
                FilePath = path,
                State = MapWorkflowState(metadata.StateCode, metadata.StatusCode),
                ConnectionReferenceLogicalNames = connectionNames,
            });

            foreach (var name in connectionNames)
            {
                AddUsage(usages, name, new ConnectionReferenceUsage
                {
                    LogicalName = name,
                    FilePath = path,
                    Kind = UsageKind.Workflow,
                    DisplayName = displayName,
                });
            }
        }
    }

    private static ImmutableArray<string> ReadWorkflowJsonConnectionReferences(IFileAccessor fileAccessor, string metadataPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lastSlash = metadataPath.LastIndexOf('/');
        var workflowJsonPath = lastSlash >= 0 ? metadataPath.Substring(0, lastSlash + 1) + "workflow.json" : "workflow.json";

        var json = ReadText(fileAccessor, new AgentFilePath(workflowJsonPath));
        if (string.IsNullOrWhiteSpace(json))
        {
            return ImmutableArray<string>.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json!);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("properties", out var propertiesElement)
                || propertiesElement.ValueKind != JsonValueKind.Object
                || !propertiesElement.TryGetProperty("connectionReferences", out var connectionsElement)
                || connectionsElement.ValueKind != JsonValueKind.Object)
            {
                return ImmutableArray<string>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (var connection in connectionsElement.EnumerateObject())
            {
                var value = connection.Value;
                if (value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("connection", out var connectionObj)
                    && connectionObj.ValueKind == JsonValueKind.Object
                    && connectionObj.TryGetProperty("connectionReferenceLogicalName", out var logicalNameElement)
                    && logicalNameElement.ValueKind == JsonValueKind.String)
                {
                    var logicalName = logicalNameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(logicalName))
                    {
                        builder.Add(logicalName!);
                    }
                }
            }

            return builder
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        }
        catch (JsonException)
        {
            return ImmutableArray<string>.Empty;
        }
    }

    private static void ScanConnectors(IFileAccessor fileAccessor, IReadOnlyList<AgentFilePath> allFiles, IReadOnlyDictionary<string, string> connectorInternalIdByLogicalName, Dictionary<string, List<ConnectionReferenceUsage>> usages, CancellationToken cancellationToken)
    {
        if (connectorInternalIdByLogicalName.Count == 0)
        {
            return;
        }

        var logicalNamesByInternalId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in connectorInternalIdByLogicalName)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            if (!logicalNamesByInternalId.TryGetValue(pair.Value, out var list))
            {
                list = new List<string>();
                logicalNamesByInternalId[pair.Value] = list;
            }

            list.Add(pair.Key);
        }

        foreach (var (path, content) in EnumerateMetadataFiles(fileAccessor, allFiles, ConnectorsFolder, cancellationToken))
        {
            CustomConnectorMetadata? connector;
            try
            {
                connector = JsonSerializer.Deserialize<CustomConnectorMetadata>(content);
            }
            catch (JsonException)
            {
                continue;
            }

            if (connector == null || string.IsNullOrWhiteSpace(connector.ConnectorInternalId))
            {
                continue;
            }

            if (!logicalNamesByInternalId.TryGetValue(connector.ConnectorInternalId!, out var logicalNames))
            {
                continue;
            }

            foreach (var logicalName in logicalNames)
            {
                AddUsage(usages, logicalName, new ConnectionReferenceUsage
                {
                    LogicalName = logicalName,
                    FilePath = path,
                    Kind = UsageKind.Connector,
                    DisplayName = !string.IsNullOrWhiteSpace(connector.DisplayName) ? connector.DisplayName! : (!string.IsNullOrWhiteSpace(connector.Name) ? connector.Name! : GetFileDisplayName(path)),
                });
            }
        }
    }

    private static void AddUsage(Dictionary<string, List<ConnectionReferenceUsage>> usages, string logicalName, ConnectionReferenceUsage usage)
    {
        if (!usages.TryGetValue(logicalName, out var list))
        {
            list = new List<ConnectionReferenceUsage>();
            usages[logicalName] = list;
        }

        list.Add(usage);
    }

    private static UsageKind ClassifyComponent(string normalizedPath)
    {
        if (IsUnder(normalizedPath, "topics"))
        {
            return UsageKind.Topic;
        }

        return UsageKind.Action;
    }

    private static WorkflowState MapWorkflowState(int? stateCode, int? statusCode)
    {
        if (stateCode == null)
        {
            return WorkflowState.Unknown;
        }

        return stateCode.Value switch
        {
            0 => WorkflowState.Draft,
            1 => WorkflowState.Activated,
            2 => WorkflowState.Suspended,
            _ => WorkflowState.Unknown,
        };
    }

    private static string? ReadText(IFileAccessor fileAccessor, AgentFilePath path)
    {
        try
        {
            using var stream = fileAccessor.OpenRead(path);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsConnectionReferencesFile(string normalizedPath)
    {
        return normalizedPath.EndsWith("/" + ConnectionReferencesFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, ConnectionReferencesFileName, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.EndsWith("/" + ConnectionReferencesFileNameLong, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, ConnectionReferencesFileNameLong, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string normalizedPath, string folder)
    {
        return normalizedPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) || normalizedPath.IndexOf("/" + folder + "/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<(string Path, string Content)> EnumerateMetadataFiles(IFileAccessor fileAccessor, IReadOnlyList<AgentFilePath> allFiles, string folder, CancellationToken cancellationToken)
    {
        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizePath(file.ToString());

            if (!IsUnder(path, folder) || !path.EndsWith("/metadata.yml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = ReadText(fileAccessor, file);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            yield return (path, content!);
        }
    }

    private static string GetFileDisplayName(string normalizedPath)
    {
        var name = normalizedPath;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name.Substring(slash + 1);
        }

        if (string.Equals(name, "metadata.yml", StringComparison.OrdinalIgnoreCase))
        {
            var folder = normalizedPath;
            var trimmed = folder.EndsWith("/metadata.yml", StringComparison.OrdinalIgnoreCase) ? folder.Substring(0, folder.Length - "/metadata.yml".Length) : folder;
            var folderSlash = trimmed.LastIndexOf('/');
            return folderSlash >= 0 ? trimmed.Substring(folderSlash + 1) : trimmed;
        }

        if (name.EndsWith(ComponentExtensionLong, StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - ComponentExtensionLong.Length);
        }
        else if (name.EndsWith(ComponentExtension, StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - ComponentExtension.Length);
        }

        return name;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}

public sealed class ConnectionReferenceUsageScan
{
    internal ConnectionReferenceUsageScan(IReadOnlyDictionary<string, ImmutableArray<ConnectionReferenceUsage>> usagesByLogicalName, ImmutableArray<string> authoredLogicalNames, ImmutableArray<ScannedWorkflow> workflows)
    {
        UsagesByLogicalName = usagesByLogicalName;
        AuthoredLogicalNames = authoredLogicalNames;
        Workflows = workflows;
    }

    public IReadOnlyDictionary<string, ImmutableArray<ConnectionReferenceUsage>> UsagesByLogicalName { get; }

    public ImmutableArray<string> AuthoredLogicalNames { get; }

    public ImmutableArray<ScannedWorkflow> Workflows { get; }

    public ImmutableArray<ConnectionReferenceUsage> GetUsages(string logicalName)
    {
        return UsagesByLogicalName.TryGetValue(logicalName, out var found) ? found : ImmutableArray<ConnectionReferenceUsage>.Empty;
    }
}

public sealed class ScannedWorkflow
{
    public string WorkflowId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public WorkflowState State { get; init; }

    public ImmutableArray<string> ConnectionReferenceLogicalNames { get; init; } = ImmutableArray<string>.Empty;
}
