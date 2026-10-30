// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.ObjectModel;
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

    private static Mock<ISyncDataverseClient> CreateDataverse(Func<CustomConnectorMetadata, CustomConnectorMetadata>? storeConnector = null)
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<ICustomConnectorOperations>()
            .Setup(c => c.SaveConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CustomConnectorMetadata connector, bool _, long? _, CancellationToken _) => storeConnector?.Invoke(connector) ?? connector);
        return dataverse;
    }

    private static Mock<ISyncDataverseClient> CreateCloudDataverse()
    {
        var cloudConnectors = new List<CustomConnectorMetadata>();
        var dataverse = CreateDataverse(sent =>
        {
            var stored = Clone(sent);
            stored.VersionNumber = 7;
            cloudConnectors.Clear();
            cloudConnectors.Add(stored);
            return stored;
        });
        dataverse.As<ICustomConnectorOperations>()
            .Setup(c => c.GetConnectorVersionsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => cloudConnectors.ToArray());
        dataverse
            .Setup(c => c.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => cloudConnectors.Select(Clone).ToArray());
        return dataverse;
    }

    private static CustomConnectorMetadata Clone(CustomConnectorMetadata connector) =>
        (JsonSerializer.Deserialize<CustomConnectorMetadata>(JsonSerializer.Serialize(connector)) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));

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
        WriteOpenApiDefinition(openApiJson);
    }

    private void WriteOpenApiDefinition(string openApiJson) =>
        File.WriteAllText(Path.Combine(_connectorFolder, "openapidefinition.json"), openApiJson);

    private Dictionary<string, string> ReadBaseline(string fileName) =>
        (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(_root, ".mcs", fileName))) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));

    private string? ReadCloudCopy(string fileSuffix = "") => SyncedAssetKind.ReadCloudCopy(new FileAccessorFactory().Create(_workspace), $"Mcs.Connector.{_connectorId}{fileSuffix}");

    private async Task<IReadOnlyList<Change>> LocalConnectorChangesAsync(WorkspaceSynchronizer synchronizer)
    {
        WorkspaceSynchronizer.WriteCloudCache(new FileAccessorFactory().Create(_workspace), ComponentWriterDefensiveTests.CreateAgentDefinition());
        var (_, changes) = await synchronizer.GetLocalChangesAsync(_workspace, ComponentWriterDefensiveTests.CreateAgentDefinition(), CancellationToken.None);
        return changes.Where(change => change.ChangeKind == CustomConnectorLayout.ChangeKind).ToList();
    }

    [Fact]
    public async Task LocalChanges_OpenApiEditedAfterPush_ReportsTheOpenApiFileAndKeepsTheServerCloudCopy()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\"}");
        await synchronizer.PushCustomConnectorsAsync(_workspace, CreateDataverse().Object, CancellationToken.None);
        var cloudCopy = ReadCloudCopy(".openapidefinition");

        WriteOpenApiDefinition("{\"swagger\":\"2.0\",\"host\":\"local.example.com\"}");
        var change = Assert.Single(await LocalConnectorChangesAsync(synchronizer));

        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Equal($"connectors/MyConnector-{_connectorId}/openapidefinition.json", change.Uri);
        Assert.Equal($"Mcs.Connector.{_connectorId}.openapidefinition", change.SchemaName);
        Assert.Equal(cloudCopy, ReadCloudCopy(".openapidefinition"));
    }

    [Fact]
    public async Task LocalChanges_WorkspaceSyncedWithoutCloudCopies_ReportsTheConnectorOnItsMetadataFile()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\"}");
        await synchronizer.PushCustomConnectorsAsync(_workspace, CreateDataverse().Object, CancellationToken.None);
        File.Delete(Path.Combine(_root, ".mcs", ".connectors-cache.json"));

        WriteOpenApiDefinition("{\"swagger\":\"2.0\",\"host\":\"local.example.com\"}");
        var change = Assert.Single(await LocalConnectorChangesAsync(synchronizer));

        Assert.Equal($"connectors/MyConnector-{_connectorId}/metadata.yml", change.Uri);
        Assert.Equal($"Mcs.Connector.{_connectorId}", change.SchemaName);
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_UnchangedSinceLastPush_MakesNoCall()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\n  \"swagger\": \"2.0\"\n}");
        var dataverse = CreateDataverse();

        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        dataverse.As<ICustomConnectorOperations>().Verify(
            c => c.SaveConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        dataverse.As<ICustomConnectorOperations>().Verify(
            c => c.GetConnectorVersionsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_ContentChanged_SavesAgain()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\n  \"swagger\": \"2.0\"\n}");
        var dataverse = CreateCloudDataverse();

        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        WriteOpenApiDefinition("{\n  \"swagger\": \"2.0\",\n  \"host\": \"example.com\"\n}");
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        dataverse.As<ICustomConnectorOperations>().Verify(
            c => c.SaveConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_WhitespaceOnlyJsonChange_SkipsSave()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\",\"host\":\"example.com\"}");
        var dataverse = CreateDataverse();

        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        WriteOpenApiDefinition("{\"swagger\":   \"2.0\",\n\"host\":\"example.com\"}");
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        dataverse.As<ICustomConnectorOperations>().Verify(
            c => c.SaveConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_ServerCopyDiffersFromSent_WorkspaceAndBaselinesFollowTheServer()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\"}");
        var dataverse = CreateDataverse(sent => new CustomConnectorMetadata
        {
            ConnectorId = sent.ConnectorId,
            Name = sent.Name,
            DisplayName = sent.DisplayName,
            ConnectorInternalId = sent.ConnectorInternalId,
            ConnectorType = sent.ConnectorType,
            Description = "Normalized by the server",
            OpenApiDefinition = "{\"swagger\":\"2.0\",\"info\":{\"title\":\"server\"}}",
            VersionNumber = 7,
        });

        var result = await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        var metadata = (JsonNode.Parse(File.ReadAllText(Path.Combine(_connectorFolder, "metadata.yml"))) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        Assert.Equal("Normalized by the server", (metadata["description"] ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).GetValue<string>());
        Assert.Equal(7, (metadata["versionnumber"] ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).GetValue<long>());
        Assert.Contains("\"title\": \"server\"", File.ReadAllText(Path.Combine(_connectorFolder, "openapidefinition.json")), StringComparison.Ordinal);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_connectorFolder, "connectionparameters.json")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_connectorFolder, "policytemplateinstances.json")));
        Assert.Equal(File.ReadAllText(Path.Combine(_connectorFolder, "metadata.yml")), ReadCloudCopy());
        Assert.Equal(File.ReadAllText(Path.Combine(_connectorFolder, "openapidefinition.json")), ReadCloudCopy(".openapidefinition"));
        Assert.Equal("7", ReadBaseline(".connectors-download.json")[_connectorId.ToString("N")]);
        Assert.True(ReadBaseline(".connectors-sync.json").ContainsKey(_connectorId.ToString("N")));
        var created = Assert.Single(result.Created);
        Assert.Equal($"Mcs.Connector.{_connectorId}", created.SchemaName);
        Assert.Empty(result.Updated);
        Assert.Empty(result.Failed);

        WorkspaceSynchronizer.WriteCloudCache(new FileAccessorFactory().Create(_workspace), ComponentWriterDefensiveTests.CreateAgentDefinition());
        var (_, changes) = await synchronizer.GetLocalChangesAsync(_workspace, ComponentWriterDefensiveTests.CreateAgentDefinition(), CancellationToken.None);
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        Assert.Empty(changes);
        dataverse.As<ICustomConnectorOperations>().Verify(
            c => c.SaveConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_WorkspaceWithoutBaselinesHoldsTheCloudContent_SkipsTheSaveAndRecordsTheCloudCopy()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\"}");
        var dataverse = CreateCloudDataverse();
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);
        Directory.Delete(Path.Combine(_root, ".mcs"), recursive: true);

        var result = await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        dataverse.As<ICustomConnectorOperations>().Verify(c => c.SaveConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(result.Created);
        Assert.Empty(result.Updated);
        Assert.Empty(result.Failed);
        Assert.Equal(_connectorId, result.PushedRowIds["shared_myconnector-5f1234567890abcdef"]);
        Assert.Equal("7", ReadBaseline(".connectors-download.json")[_connectorId.ToString("N")]);
        Assert.Equal(File.ReadAllText(Path.Combine(_connectorFolder, "metadata.yml")), ReadCloudCopy());
        Assert.Empty(await LocalConnectorChangesAsync(synchronizer));
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_LocalEditOfAConnectorTheCloudHas_UpdatesIt()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\"}");
        var dataverse = CreateCloudDataverse();
        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);
        WriteOpenApiDefinition("{\"swagger\":\"2.0\",\"host\":\"local.example.com\"}");

        var result = await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        Assert.Equal($"Mcs.Connector.{_connectorId}", Assert.Single(result.Updated).SchemaName);
        dataverse.As<ICustomConnectorOperations>().Verify(c => c.SaveConnectorAsync(It.Is<CustomConnectorMetadata>(connector => connector.OpenApiDefinition != null && connector.OpenApiDefinition.Contains("local.example.com")), true, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_ClientWithoutConnectorOperations_UpsertsAndRecordsTheCopyItReadsBack()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\"}");
        var cloudConnectors = new List<CustomConnectorMetadata>();
        var version = 6;
        var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);
        dataverse
            .Setup(c => c.GetConnectorVersionsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => cloudConnectors.ToArray());
        dataverse
            .Setup(c => c.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => cloudConnectors.Select(Clone).ToArray());
        dataverse
            .Setup(c => c.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CustomConnectorMetadata sent, CancellationToken _) =>
            {
                var stored = Clone(sent);
                stored.Description = "Stored by the server";
                stored.VersionNumber = ++version;
                var isNew = cloudConnectors.Count == 0;
                cloudConnectors.Clear();
                cloudConnectors.Add(stored);
                return isNew;
            });

        var createResult = await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);
        WriteOpenApiDefinition("{\"swagger\":\"2.0\",\"host\":\"local.example.com\"}");
        var updateResult = await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        Assert.Equal($"Mcs.Connector.{_connectorId}", Assert.Single(createResult.Created).SchemaName);
        Assert.Equal($"Mcs.Connector.{_connectorId}", Assert.Single(updateResult.Updated).SchemaName);
        dataverse.Verify(c => c.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Contains("Stored by the server", File.ReadAllText(Path.Combine(_connectorFolder, "metadata.yml")), StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(Path.Combine(_connectorFolder, "metadata.yml")), ReadCloudCopy());
        Assert.Equal("8", ReadBaseline(".connectors-download.json")[_connectorId.ToString("N")]);
        Assert.Empty(await LocalConnectorChangesAsync(synchronizer));
    }

    [Fact]
    public async Task PushCustomConnectorsAsync_ServerRenamesConnector_ReusesTheExistingFolder()
    {
        var synchronizer = CreateSynchronizer();
        WriteConnectorFiles("{\"swagger\":\"2.0\"}");
        var dataverse = CreateDataverse(sent =>
        {
            var stored = Clone(sent);
            stored.Name = "shared_renamed";
            return stored;
        });

        await synchronizer.PushCustomConnectorsAsync(_workspace, dataverse.Object, CancellationToken.None);

        Assert.Equal(new[] { _connectorFolder }, Directory.GetDirectories(Path.Combine(_root, "connectors")));
        Assert.Equal("shared_renamed", ((JsonNode.Parse(File.ReadAllText(Path.Combine(_connectorFolder, "metadata.yml"))) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."))["name"] ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).GetValue<string>());
    }
}
