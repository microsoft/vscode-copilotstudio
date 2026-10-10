// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Holds <see cref="ISyncDataverseClient"/> to the shape consumers outside this repository compile
/// against. It implements every member explicitly, so moving one onto a base interface stops
/// compiling here rather than throwing <c>MissingMethodException</c> inside an already-shipped host.
/// </summary>
internal sealed class BaselineSyncDataverseClient : ISyncDataverseClient
{
    void ISyncDataverseClient.SetDataverseUrl(string dataverseUrl) => throw new NotSupportedException();

    Task<AgentInfo> ISyncDataverseClient.CreateNewAgentAsync(string displayName, string schemaName, AuthoringShape authoringShape, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<Guid> ISyncDataverseClient.GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<WorkflowMetadata[]> ISyncDataverseClient.DownloadAllWorkflowsForAgentAsync(AgentSyncInfo syncInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<WorkflowResponse> ISyncDataverseClient.UpdateWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<WorkflowResponse> ISyncDataverseClient.InsertWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<bool> ISyncDataverseClient.ConnectionReferenceExistsAsync(string connectionReferenceLogicalName, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task ISyncDataverseClient.CreateConnectionReferenceAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken, Guid? customConnectorRowId) => throw new NotSupportedException();

    Task ISyncDataverseClient.EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken, Guid? customConnectorRowId) => throw new NotSupportedException();

    Task ISyncDataverseClient.BindConnectionReferenceAsync(string connectionReferenceLogicalName, string connectionLogicalName, CancellationToken cancellationToken, string? connectionReferenceDisplayName) => throw new NotSupportedException();

    Task ISyncDataverseClient.SetWorkflowStateAsync(Guid workflowId, bool activate, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<ConnectionReferenceInfo[]> ISyncDataverseClient.GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<CustomConnectorMetadata[]> ISyncDataverseClient.DownloadConnectorsByInternalIdsAsync(IEnumerable<string> connectorInternalIds, bool isManaged, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<CustomConnectorMetadata[]> ISyncDataverseClient.GetConnectorVersionsByInternalIdsAsync(IEnumerable<string> connectorInternalIds, bool isManaged, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<CustomConnectorMetadata[]> ISyncDataverseClient.GetConnectorsByInternalIdPrefixAsync(string connectorInternalIdPrefix, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<bool> ISyncDataverseClient.UpsertConnectorAsync(CustomConnectorMetadata connector, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<SolutionInfo> ISyncDataverseClient.GetSolutionVersionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<AgentInfo> ISyncDataverseClient.GetAgentInfoAsync(Guid agentId, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task ISyncDataverseClient.DownloadKnowledgeFileAsync(string knowledgeFileFolder, BotComponentId botComponentId, string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task ISyncDataverseClient.UploadKnowledgeFileAsync(string knowledgeFileFolder, Guid botComponentId, string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<AIPromptMetadata[]> ISyncDataverseClient.DownloadAllAIPromptsForAgentAsync(AgentSyncInfo syncInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<AIPromptMetadata[]> ISyncDataverseClient.DownloadAIPromptsByModelIdsAsync(IReadOnlyCollection<Guid> aiModelIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<AIPromptResponse> ISyncDataverseClient.UpsertAIPromptAsync(Guid? agentId, AIPromptMetadata? promptMetadata, CancellationToken cancellationToken) => throw new NotSupportedException();
}

public class SyncDataverseClientContractTests
{
    [Fact]
    public void TheInterfaceStillDeclaresEveryMemberItself()
    {
        var contract = typeof(ISyncDataverseClient);

        Assert.Empty(contract.GetInterfaces());

        foreach (var member in new[]
        {
            nameof(ISyncDataverseClient.SetDataverseUrl),
            nameof(ISyncDataverseClient.ConnectionReferenceExistsAsync),
            nameof(ISyncDataverseClient.EnsureConnectionReferenceExistsAsync),
            nameof(ISyncDataverseClient.SetWorkflowStateAsync),
            nameof(ISyncDataverseClient.GetConnectionReferencesByLogicalNamesAsync),
        })
        {
            Assert.Equal(contract, contract.GetMethod(member)!.DeclaringType);
        }
    }

    [Fact]
    public void AnImplementationOfTheShippedShapeStillSatisfiesTheInterface() =>
        Assert.IsAssignableFrom<ISyncDataverseClient>(new BaselineSyncDataverseClient());

    [Fact]
    public void TheStandaloneContractIsSeparateFromTheShippedOne()
    {
        Assert.False(typeof(ISyncDataverseClient).IsAssignableFrom(typeof(IStandaloneWorkflowDataverseClient)));
        Assert.False(typeof(IStandaloneWorkflowDataverseClient).IsAssignableFrom(typeof(ISyncDataverseClient)));
        Assert.True(typeof(ISyncDataverseClient).IsAssignableFrom(typeof(SyncDataverseClient)));
        Assert.True(typeof(IStandaloneWorkflowDataverseClient).IsAssignableFrom(typeof(SyncDataverseClient)));
    }
}
