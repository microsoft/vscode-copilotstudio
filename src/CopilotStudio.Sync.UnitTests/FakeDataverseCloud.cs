// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed class FakeDataverseCloud
{
    public static readonly Guid CreatedAgentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly object _gate = new();

    private int _agentProbes;

    public FakeDataverseCloud()
    {
        Client.Setup(client => client.EnsureConnectionReferenceExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>())).Returns<string, string, CancellationToken, Guid?>(EnsureConnectionReferenceAsync);
        Client.Setup(client => client.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).Returns<IEnumerable<string>, CancellationToken>(GetConnectionReferencesAsync);
        Client.Setup(client => client.GetConnectorsByInternalIdPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<CustomConnectorMetadata>());
        Client.Setup(client => client.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns<string, CancellationToken>(GetAgentIdAsync);
        Client.Setup(client => client.CreateNewAgentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AuthoringShape>(), It.IsAny<CancellationToken>())).Returns<string, string, AuthoringShape, CancellationToken>(CreateAgentAsync);
        Client.Setup(client => client.GetSolutionVersionsAsync(It.IsAny<CancellationToken>())).Returns(() => SolutionVersionsThrows is null ? Task.FromResult(new SolutionInfo()) : Task.FromException<SolutionInfo>(SolutionVersionsThrows));
        Client.Setup(client => client.UpdateWorkflowAsync(It.IsAny<Guid?>(), It.IsAny<WorkflowMetadata?>(), It.IsAny<CancellationToken>())).Returns<Guid?, WorkflowMetadata?, CancellationToken>((_, workflow, _) => Task.FromResult(StoreWorkflow(workflow!)));
        Client.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(Workflows.Values.Select(Copy).ToArray()));
        Client.Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AIPromptMetadata>());
        Client.Setup(client => client.BindConnectionReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>())).Returns<string, string, CancellationToken, string?>(BindConnectionReferenceAsync);
        Client.Setup(client => client.SetWorkflowStateAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns<Guid, bool, CancellationToken>(SetWorkflowStateAsync);
    }

    public Mock<ISyncDataverseClient> Client { get; } = new();

    public ConcurrentDictionary<string, ConnectionReferenceInfo> ConnectionReferences { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string LogicalName, string ConnectorId, Guid? CustomConnectorRowId)> EnsuredConnectionReferences { get; } = new();

    public ConcurrentDictionary<Guid, WorkflowMetadata> Workflows { get; } = new();

    public Exception? ProvisioningThrows { get; set; }

    public Exception? QueryThrows { get; set; }

    public bool OmitProvisionedReferences { get; set; }

    public Guid ExistingAgentId { get; set; }

    public string? SchemaNameReturns { get; set; }

    public Exception? CreateThrows { get; set; }

    public Guid SchemaProbeReturns { get; set; }

    public Exception? SchemaProbeThrows { get; set; }

    public Exception? SolutionVersionsThrows { get; set; }

    public AuthoringShape LastAuthoringShape { get; private set; } = AuthoringShape.Unknown;

    public IReadOnlyList<string> EnsuredLogicalNames
    {
        get
        {
            lock (_gate)
            {
                return EnsuredConnectionReferences.Select(entry => entry.LogicalName).ToArray();
            }
        }
    }

    public IReadOnlyList<string> CallsTo(params string[] methodNames) => Client.Invocations.Select(invocation => invocation.Method.Name).Where(methodNames.Contains).ToArray();

    public ConnectionReferenceInfo AddConnectionReference(string logicalName, string connectorId, string? connectionId = null)
    {
        var row = new ConnectionReferenceInfo { ConnectionReferenceId = Guid.NewGuid(), ConnectionReferenceLogicalName = logicalName, ConnectorId = connectorId, ConnectionId = connectionId ?? string.Empty, Version = 7 };
        ConnectionReferences[logicalName] = row;

        return row;
    }

    private Task EnsureConnectionReferenceAsync(string logicalName, string connectorId, CancellationToken cancellationToken, Guid? customConnectorRowId)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ProvisioningThrows is not null)
        {
            return Task.FromException(ProvisioningThrows);
        }

        lock (_gate)
        {
            EnsuredConnectionReferences.Add((logicalName, connectorId, customConnectorRowId));
        }

        ConnectionReferences.TryAdd(logicalName, new ConnectionReferenceInfo { ConnectionReferenceId = Guid.NewGuid(), ConnectionReferenceLogicalName = logicalName, ConnectorId = connectorId, Version = 1 });

        return Task.CompletedTask;
    }

    private Task<ConnectionReferenceInfo[]> GetConnectionReferencesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (QueryThrows is not null)
        {
            return Task.FromException<ConnectionReferenceInfo[]>(QueryThrows);
        }

        return Task.FromResult(OmitProvisionedReferences ? Array.Empty<ConnectionReferenceInfo>() : logicalNames.Where(ConnectionReferences.ContainsKey).Select(name => ConnectionReferences[name]).ToArray());
    }

    private Task<Guid> GetAgentIdAsync(string schemaName, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _agentProbes) == 1)
        {
            return Task.FromResult(ExistingAgentId);
        }

        return SchemaProbeThrows is null ? Task.FromResult(SchemaProbeReturns) : Task.FromException<Guid>(SchemaProbeThrows);
    }

    private Task<AgentInfo> CreateAgentAsync(string displayName, string schemaName, AuthoringShape authoringShape, CancellationToken cancellationToken)
    {
        LastAuthoringShape = authoringShape;

        return CreateThrows is null
            ? Task.FromResult(new AgentInfo { AgentId = CreatedAgentId, DisplayName = displayName, SchemaName = SchemaNameReturns ?? schemaName })
            : Task.FromException<AgentInfo>(CreateThrows);
    }

    private WorkflowResponse StoreWorkflow(WorkflowMetadata workflow)
    {
        var stored = Copy(workflow);
        Workflows[stored.WorkflowId] = stored;

        return new WorkflowResponse { WorkflowId = stored.WorkflowId, WorkflowName = stored.Name ?? stored.WorkflowId.ToString(), IsDisabled = stored.StateCode != 1, StoredWorkflow = Copy(stored) };
    }

    private Task BindConnectionReferenceAsync(string logicalName, string connectionId, CancellationToken cancellationToken, string? displayName)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ConnectionReferences.TryGetValue(logicalName, out var row))
        {
            return Task.FromException(new InvalidOperationException($"Connection reference '{logicalName}' was not found."));
        }

        ConnectionReferences[logicalName] = new ConnectionReferenceInfo { ConnectionReferenceId = row.ConnectionReferenceId, ConnectionReferenceLogicalName = row.ConnectionReferenceLogicalName, ConnectorId = row.ConnectorId, ConnectionId = connectionId, Version = row.Version + 1 };

        return Task.CompletedTask;
    }

    private Task SetWorkflowStateAsync(Guid workflowId, bool activate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Workflows.TryGetValue(workflowId, out var workflow))
        {
            return Task.FromException(new InvalidOperationException($"Workflow '{workflowId}' was not found."));
        }

        workflow.StateCode = activate ? 1 : 0;
        workflow.StatusCode = activate ? 2 : 1;

        return Task.CompletedTask;
    }

    internal static WorkflowMetadata Copy(WorkflowMetadata workflow) => JsonSerializer.Deserialize<WorkflowMetadata>(JsonSerializer.Serialize(workflow))!;
}
