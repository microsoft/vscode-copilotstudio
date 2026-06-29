// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkflowDraftPushTests : IDisposable
{
    private const string RefLogicalName = "cr_testref";

    private readonly string _root;
    private readonly DirectoryPath _workspace;
    private readonly Guid _workflowId = Guid.NewGuid();
    private readonly string _workflowFolder;

    public WorkflowDraftPushTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcs-workflow-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new DirectoryPath(_root.Replace('\\', '/') + "/");
        _workflowFolder = Path.Combine(_root, "workflows", "MyFlow-" + _workflowId.ToString("D"));
        Directory.CreateDirectory(_workflowFolder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static WorkspaceSynchronizer CreateSynchronizer()
    {
        var fileParser = new SyncMcsFileParser(LspProjectorService.Instance);
        var fileAccessorFactory = new FileAccessorFactory();
        var island = new Mock<IIslandControlPlaneService>();
        var progress = new TestSyncProgress(new List<string>());
        var pathResolver = new LspComponentPathResolver();

        return new WorkspaceSynchronizer(fileParser, fileAccessorFactory, island.Object, progress, pathResolver);
    }

    private void WriteWorkflowFiles(string workflowJson, int stateCode = 1, int statusCode = 2)
    {
        var metadata =
            $"workflowId: {_workflowId}\n" +
            "name: My Flow\n" +
            "type: 1\n" +
            "category: 5\n" +
            $"stateCode: {stateCode}\n" +
            $"statusCode: {statusCode}\n";

        File.WriteAllText(Path.Combine(_workflowFolder, "metadata.yml"), metadata);
        File.WriteAllText(Path.Combine(_workflowFolder, "workflow.json"), workflowJson);
    }

    private static string WorkflowJsonWithReference() =>
        "{\n" +
        "  \"properties\": {\n" +
        "    \"connectionReferences\": {\n" +
        "      \"shared_x\": {\n" +
        $"        \"connection\": {{ \"connectionReferenceLogicalName\": \"{RefLogicalName}\" }}\n" +
        "      }\n" +
        "    }\n" +
        "  }\n" +
        "}";

    private static Mock<ISyncDataverseClient> CreateDataverse(WorkflowMetadata?[] captured, ConnectionReferenceInfo[]? references = null)
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpdateWorkflowAsync(It.IsAny<Guid?>(), It.IsAny<WorkflowMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<Guid?, WorkflowMetadata?, CancellationToken>((_, m, _) => captured[0] = m)
            .ReturnsAsync(new WorkflowResponse());

        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(references ?? Array.Empty<ConnectionReferenceInfo>());

        return dataverse;
    }

    [Fact]
    public async Task ReattachWithUnboundConnection_UploadsAsDraft()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = string.Empty,
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.ActivateWhenConnectionsBound);

        Assert.NotNull(captured[0]);
        Assert.Equal(0, captured[0]!.StateCode);
        Assert.Equal(1, captured[0]!.StatusCode);
    }

    [Fact]
    public async Task ReattachWithBoundConnection_Activates()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = "shared-x-connection",
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.ActivateWhenConnectionsBound);

        Assert.NotNull(captured[0]);
        Assert.Equal(1, captured[0]!.StateCode);
        Assert.Equal(2, captured[0]!.StatusCode);
    }

    [Fact]
    public async Task ReattachWithoutConnectionReferences_Activates()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles("{\n  \"properties\": {}\n}");

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured);

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.ActivateWhenConnectionsBound);

        Assert.NotNull(captured[0]);
        Assert.Equal(1, captured[0]!.StateCode);
        Assert.Equal(2, captured[0]!.StatusCode);
    }

    [Fact]
    public async Task ReattachDraftsWorkflowWithConnectionReference_EvenWhenConnectionIdPresent()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = "stale-or-missing-connection",
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionReferencesExist);

        Assert.NotNull(captured[0]);
        Assert.Equal(0, captured[0]!.StateCode);
        Assert.Equal(1, captured[0]!.StatusCode);
    }

    [Fact]
    public async Task ReattachWithoutConnectionReference_StaysActivatedUnderDraftMode()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles("{\n  \"properties\": {}\n}");

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured);

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionReferencesExist);

        Assert.NotNull(captured[0]);
        Assert.Equal(1, captured[0]!.StateCode);
        Assert.Equal(2, captured[0]!.StatusCode);
        dataverse.Verify(
            c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PushPreservesSavedActivatedState()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles("{\n  \"properties\": {}\n}");

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured);

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(captured[0]);
        Assert.Equal(1, captured[0]!.StateCode);
        Assert.Equal(2, captured[0]!.StatusCode);
    }

    [Fact]
    public async Task PushWithUnboundConnection_DowngradesToDraft()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = string.Empty,
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);

        Assert.NotNull(captured[0]);
        Assert.Equal(0, captured[0]!.StateCode);
        Assert.Equal(1, captured[0]!.StatusCode);
    }

    [Fact]
    public async Task PushWithBoundConnection_PreservesActivated()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = "shared-x-connection",
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);

        Assert.NotNull(captured[0]);
        Assert.Equal(1, captured[0]!.StateCode);
        Assert.Equal(2, captured[0]!.StatusCode);
    }

    [Fact]
    public async Task PushWithSavedDraftAndBoundConnection_StaysDraft()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference(), stateCode: 0, statusCode: 1);

        var captured = new WorkflowMetadata?[1];
        var dataverse = CreateDataverse(captured, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = "shared-x-connection",
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);

        Assert.NotNull(captured[0]);
        Assert.Equal(0, captured[0]!.StateCode);
        Assert.Equal(1, captured[0]!.StatusCode);
    }

    [Fact]
    public async Task PushUnchangedActivatedWorkflow_ConnectionBecameUnbound_LeavesWorkflowUntouched()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var capturedBound = new WorkflowMetadata?[1];
        var boundDataverse = CreateDataverse(capturedBound, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = "shared-x-connection",
            },
        });

        var (_, cloudFlowMetadata) = await synchronizer.UpsertWorkflowForAgentAsync(_workspace, boundDataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);
        Assert.Equal(1, capturedBound[0]!.StateCode);

        var fileAccessor = new FileAccessorFactory().Create(_workspace);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithFlows(cloudFlowMetadata.Workflows));

        var capturedUnbound = new WorkflowMetadata?[1];
        var unboundDataverse = CreateDataverse(capturedUnbound, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = string.Empty,
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, unboundDataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);

        Assert.Null(capturedUnbound[0]);
    }

    [Fact]
    public async Task PushWithUnboundConnection_WritesDraftStateToLocalMetadata()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var dataverse = CreateDataverse(new WorkflowMetadata?[1], new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = string.Empty,
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);

        var content = File.ReadAllText(Path.Combine(_workflowFolder, "metadata.yml"));
        Assert.Contains("stateCode: 0", content);
        Assert.Contains("statusCode: 1", content);
    }

    [Fact]
    public async Task PushWithBoundConnection_LeavesLocalMetadataActivated()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var dataverse = CreateDataverse(new WorkflowMetadata?[1], new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = "shared-x-connection",
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);

        var content = File.ReadAllText(Path.Combine(_workflowFolder, "metadata.yml"));
        Assert.Contains("stateCode: 1", content);
        Assert.Contains("statusCode: 2", content);
    }

    [Fact]
    public async Task PushDraftConnectionReferenceMode_WritesDraftStateToLocalMetadata()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var dataverse = CreateDataverse(new WorkflowMetadata?[1], new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = "stale-or-missing-connection",
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionReferencesExist);

        var content = File.ReadAllText(Path.Combine(_workflowFolder, "metadata.yml"));
        Assert.Contains("stateCode: 0", content);
        Assert.Contains("statusCode: 1", content);
    }

    [Fact]
    public async Task PushDowngradedWorkflow_SubsequentPushDetectsNoChange()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var capturedFirst = new WorkflowMetadata?[1];
        var firstDataverse = CreateDataverse(capturedFirst, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = string.Empty,
            },
        });

        var (_, cloudFlowMetadata) = await synchronizer.UpsertWorkflowForAgentAsync(_workspace, firstDataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);

        Assert.Equal(0, capturedFirst[0]!.StateCode);
        Assert.Contains("stateCode: 0", File.ReadAllText(Path.Combine(_workflowFolder, "metadata.yml")));

        var fileAccessor = new FileAccessorFactory().Create(_workspace);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithFlows(cloudFlowMetadata.Workflows));

        var capturedSecond = new WorkflowMetadata?[1];
        var secondDataverse = CreateDataverse(capturedSecond, new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = string.Empty,
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, secondDataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionsUnbound);

        Assert.Null(capturedSecond[0]);
        Assert.Contains("stateCode: 0", File.ReadAllText(Path.Combine(_workflowFolder, "metadata.yml")));
    }

    [Fact]
    public async Task PushDraftConnectionReferenceMode_KeepsLocalWorkflowFiles()
    {
        var synchronizer = CreateSynchronizer();
        WriteWorkflowFiles(WorkflowJsonWithReference());

        var dataverse = CreateDataverse(new WorkflowMetadata?[1], new[]
        {
            new ConnectionReferenceInfo
            {
                ConnectionReferenceLogicalName = RefLogicalName,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_x",
                ConnectionId = "stale-or-missing-connection",
            },
        });

        await synchronizer.UpsertWorkflowForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None, WorkflowActivationMode.DraftWhenConnectionReferencesExist);

        Assert.True(Directory.Exists(_workflowFolder));
        Assert.True(File.Exists(Path.Combine(_workflowFolder, "metadata.yml")));
        Assert.True(File.Exists(Path.Combine(_workflowFolder, "workflow.json")));
    }
}
