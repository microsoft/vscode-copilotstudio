// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Text;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Tests that the push flow correctly writes component files via ComponentWriter
/// and the defensive TryGetBotComponentById path (batch push crash fix).
/// </summary>
public class ComponentWriterDefensiveTests
{
    [Fact]
    public async Task PushChangeset_WithEmptyConfirmation_DoesNotCrash()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = CreateSyncInfrastructure();

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: testbot");
        var emptyDef = new BotDefinition();

        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, emptyDef);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "initial-token", CancellationToken.None);

        var emptyChangeset = new PvaComponentChangeSet(
            null,
            botEntity,
            "token-2");

        mockIsland
            .Setup(x => x.SaveChangesAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<PvaComponentChangeSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyChangeset);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var result = await synchronizer.PushChangesetAsync(
            workspace,
            CreateMockOperationContext(),
            emptyChangeset,
            mockDataverse.Object,
            Guid.NewGuid(),
            null,
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task PushChangeset_WritesConfirmedComponentFiles()
    {
        // Arrange: server confirmation returns a changeset with a BotEntity.
        // Verify settings.mcs.yml is written (proves the write path executes).
        var (synchronizer, fileAccessorFactory, mockIsland) = CreateSyncInfrastructure();

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: testbot")!;
        var baseDef = new BotDefinition();

        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, baseDef);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        var confirmationChangeset = new PvaComponentChangeSet(
            null,
            botEntity,
            "token-2");

        mockIsland
            .Setup(x => x.SaveChangesAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<PvaComponentChangeSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(confirmationChangeset);

        // Act
        var mockDataverse = new Mock<ISyncDataverseClient>();
        await synchronizer.PushChangesetAsync(
            workspace,
            CreateMockOperationContext(),
            confirmationChangeset,
            mockDataverse.Object,
            Guid.NewGuid(),
            null,
            CancellationToken.None);

        // Assert: settings file was written by the write path
        Assert.True(fileAccessor.Exists(new AgentFilePath("settings.mcs.yml")),
            "settings.mcs.yml should be written from the BotEntity in the confirmation changeset");
    }

    internal static AuthoringOperationContext CreateMockOperationContext()
    {
        var principal = new PrincipalObjectReference(Guid.NewGuid(), Guid.Empty);
        var orgInfo = new CdsOrganizationInfo(
            Guid.NewGuid(),
            new Uri("https://test.crm.dynamics.com"),
            pvaSolutionVersion: new Version(1, 0),
            dvTableSearchGlossaryAndSynonymsSolutionVersion: new Version(1, 0),
            dvTableSearchSolutionVersion: new Version(1, 0));
        var botRef = new BotReference("testEnv", Guid.NewGuid());

        return new AuthoringOperationContext(principal, orgInfo, botRef, null, false);
    }

    internal static (WorkspaceSynchronizer sync, InMemoryFileAccessorFactory factory, Mock<IIslandControlPlaneService> island) CreateSyncInfrastructure()
    {
        var progress = new TestSyncProgress(new List<string>());
        var fileParser = new SyncMcsFileParser(LspProjectorService.Instance);
        var fileAccessorFactory = new InMemoryFileAccessorFactory();
        var pathResolver = new LspComponentPathResolver();
        var mockIsland = new Mock<IIslandControlPlaneService>();

        var synchronizer = new WorkspaceSynchronizer(
            fileParser,
            fileAccessorFactory,
            mockIsland.Object,
            progress,
            pathResolver);

        return (synchronizer, fileAccessorFactory, mockIsland);
    }
}

internal class TestSyncProgress : ISyncProgress
{
    private readonly List<string> _messages;

    public TestSyncProgress(List<string> messages)
    {
        _messages = messages;
    }

    public IReadOnlyList<string> Messages => _messages;

    public void Report(string message)
    {
        _messages.Add(message);
    }
}

internal class InMemoryFileAccessorFactory : IFileAccessorFactory
{
    private readonly Dictionary<string, InMemoryFileAccessor> _accessors = new();

    public IFileAccessor Create(DirectoryPath workspaceFolder)
    {
        var key = workspaceFolder.ToString();
        if (!_accessors.TryGetValue(key, out var accessor))
        {
            accessor = new InMemoryFileAccessor(workspaceFolder);
            _accessors[key] = accessor;
        }
        return accessor;
    }
}

internal class InMemoryFileAccessor : IFileAccessor
{
    private readonly DirectoryPath _root;
    private readonly Dictionary<string, byte[]> _files = new();

    public InMemoryFileAccessor(DirectoryPath root)
    {
        _root = root;
    }

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public bool Exists(AgentFilePath path) => _files.ContainsKey(path.ToString());

    public Stream OpenRead(AgentFilePath path)
    {
        if (_files.TryGetValue(path.ToString(), out var data))
        {
            return new MemoryStream(data, writable: false);
        }
        throw new FileNotFoundException($"File not found: {path}");
    }

    public Stream OpenWrite(AgentFilePath path)
    {
        return new WriteCapturingStream(path.ToString(), _files);
    }

    public void Delete(AgentFilePath path)
    {
        _files.Remove(path.ToString());
    }

    public void CreateHiddenDirectory(AgentFilePath path) { }

    public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
    {
        if (_files.TryGetValue(sourcePath.ToString(), out var data))
        {
            _files[targetPath.ToString()] = data;
            _files.Remove(sourcePath.ToString());
        }
    }

    public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
    {
        foreach (var key in _files.Keys)
        {
            if (relativeFolder != null && !key.StartsWith(relativeFolder, StringComparison.OrdinalIgnoreCase))
                continue;

            if (filePattern != "*.*")
            {
                var ext = filePattern.Replace("*", "");
                if (!key.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            yield return new AgentFilePath(key);
        }
    }
}

internal class WriteCapturingStream : MemoryStream
{
    private readonly string _key;
    private readonly Dictionary<string, byte[]> _store;

    public WriteCapturingStream(string key, Dictionary<string, byte[]> store)
    {
        _key = key;
        _store = store;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _store[_key] = ToArray();
        }
        base.Dispose(disposing);
    }
}
