// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class RetargetConnectorPreservationTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryPath _workspace;
    private readonly string _connectorFolder;

    public RetargetConnectorPreservationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcs-retarget-connector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new DirectoryPath(_root.Replace('\\', '/') + "/");
        _connectorFolder = Path.Combine(_root, "connectors", "MyConnector-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(_connectorFolder);
        File.WriteAllText(Path.Combine(_connectorFolder, "openapidefinition.json"), "{\"swagger\":\"2.0\"}");
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
        island
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, null, "token"));
        var progress = new TestSyncProgress(new List<string>());
        var pathResolver = new LspComponentPathResolver();

        return new WorkspaceSynchronizer(fileParser, fileAccessorFactory, island.Object, progress, pathResolver);
    }

    private Task SyncAsync(WorkspaceSynchronizer synchronizer, bool syncCustomConnectors) => synchronizer.SyncWorkspaceAsync(
        _workspace,
        ComponentWriterDefensiveTests.CreateMockOperationContext(),
        changeToken: null,
        updateWorkspaceDirectory: false,
        new Mock<ISyncDataverseClient>().Object,
        new AgentSyncInfo { AgentId = Guid.Empty },
        cloudFlowMetadata: null,
        CancellationToken.None,
        ImmutableArray<AIPromptMetadata>.Empty,
        syncCustomConnectors);

    [Fact]
    public async Task RetargetSync_WithSyncCustomConnectorsFalse_PreservesLocalConnectorFolder()
    {
        var synchronizer = CreateSynchronizer();

        await SyncAsync(synchronizer, syncCustomConnectors: false);

        Assert.True(Directory.Exists(_connectorFolder), "local connector folder must be preserved during the retarget pre-push sync so the follow-up push can upload it");
    }

    [Fact]
    public async Task DefaultSync_WithSyncCustomConnectorsTrue_ReconcilesOrphanedConnectorFolder()
    {
        var synchronizer = CreateSynchronizer();

        await SyncAsync(synchronizer, syncCustomConnectors: true);

        Assert.False(Directory.Exists(_connectorFolder), "the default sync reconciles connector folders not referenced by the target definition");
    }
}
