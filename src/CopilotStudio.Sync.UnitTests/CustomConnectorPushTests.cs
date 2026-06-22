using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CustomConnectorPushTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryPath _workspace;
    private readonly Guid _connectorId = Guid.NewGuid();
    private readonly string _connectorFolder;

    public CustomConnectorPushTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcs-connector-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new DirectoryPath(_root.Replace('\\', '/') + "/");
        _connectorFolder = Path.Combine(_root, "connectors", "MyConnector-" + _connectorId.ToString("D"));
        Directory.CreateDirectory(_connectorFolder);
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

    private void WriteConnectorFiles(string openApiJson)
    {
        var metadata =
            "{" +
            $"\"connectorid\":\"{_connectorId}\"," +
            "\"name\":\"MyConnector\"," +
            "\"displayname\":\"My Connector\"," +
            "\"connectorinternalid\":\"shared_myconnector-5f1234567890abcdef\"," +
            "\"openapidefinition\":\"connectors/MyConnector-" + _connectorId.ToString("D") + "/openapidefinition.json\"," +
            "\"connectortype\":0" +
            "}";

        File.WriteAllText(Path.Combine(_connectorFolder, "metadata.yml"), metadata);
        File.WriteAllText(Path.Combine(_connectorFolder, "openapidefinition.json"), openApiJson);
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_UnchangedSinceLastPush_SkipsUpsert()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\n  \"swagger\": \"2.0\"\n}");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        dataverse.Verify(
            c => c.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_ContentChanged_ReUpserts()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\n  \"swagger\": \"2.0\"\n}");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        WriteConnectorFiles("{\n  \"swagger\": \"2.0\",\n  \"host\": \"example.com\"\n}");
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        dataverse.Verify(
            c => c.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_WhitespaceOnlyJsonChange_SkipsUpsert()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\",\"host\":\"example.com\"}");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        WriteConnectorFiles("{\n  \"swagger\": \"2.0\",\n  \"host\": \"example.com\"\n}");
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        dataverse.Verify(
            c => c.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
