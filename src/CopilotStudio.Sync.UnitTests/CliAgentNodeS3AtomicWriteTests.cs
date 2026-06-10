// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Node S3 (TDD D33) - the CLI connection-reference write is ordered so the per-file
// infrastructure/connections/*.sync.yaml set is written+committed BEFORE the stale flat
// connectionreferences.mcs.yml is removed. A cancellation/IO failure must leave a
// recoverable state (flat intact OR per-file complete), never both-gone - which would let
// the next push read zero connection references on disk and synthesize a delete for every
// cloud connection reference. CLI-only; the classic flat-file write is untouched.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentNodeS3AtomicWriteTests
{
    private static readonly AgentFilePath FlatPath = new AgentFilePath("connectionreferences.mcs.yml");

    private static AgentFilePath PerFilePath(string logicalName) =>
        new AgentFilePath($"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{logicalName}{CliAgentConnectionsWriter.FileExtension}");

    [Fact]
    public async Task CliWrite_FlatRemovalFails_PerFileSetAlreadyComplete_AndFlatIntact()
    {
        // Inject an IO failure on the stale-flat Delete. With the D33 ordering (per-file
        // WriteAll FIRST, stale-flat Delete AFTER), the per-file set is already complete when
        // the failure hits, and the flat file is still present - a recoverable state.
        // (With the pre-D33 ordering the Delete ran first and threw before WriteAll, leaving
        // the per-file set absent AND the flat gone - the destructive both-gone state.)
        var (entity, definition) = CliAgentRoundTripReadTests.LoadFixtureBotAndDefinition("FoodLogger");

        var faultFactory = new FaultOnDeleteFileAccessorFactory(FlatPath.ToString());
        var mockIsland = new Mock<IIslandControlPlaneService>();
        var synchronizer = new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            faultFactory,
            mockIsland.Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

        var workspace = new DirectoryPath($"c:/test/s3-fault-{Guid.NewGuid():N}/");
        var inner = ((FaultOnDeleteFileAccessor)faultFactory.Create(workspace)).Inner;

        // Seed: empty cloud cache + changetoken + a STALE flat file (as if previously cloned
        // via the classic-shape path).
        WorkspaceSynchronizer.WriteCloudCache(inner, new BotDefinition());
        await inner.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "seed", CancellationToken.None);
        await inner.WriteAsync(FlatPath,
            Encoding.UTF8.GetBytes("# stale classic-shape file\nconnectionReferences:\n  []\n"),
            CancellationToken.None);

        var crChanges = definition.ConnectionReferences
            .Select(cr => (ConnectionReferenceChange)new ConnectionReferenceInsert(cr))
            .ToList();
        var changeset = new PvaComponentChangeSet(
            botComponentChanges: new List<BotComponentChange>(),
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: crChanges,
            aIPluginOperationChanges: null,
            componentCollectionChanges: null,
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: entity,
            changeToken: "after-write");
        mockIsland.Setup(x => x.SaveChangesAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<PvaComponentChangeSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(changeset);

        await Assert.ThrowsAsync<IOException>(() => synchronizer.PushChangesetAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            changeset,
            new Mock<ISyncDataverseClient>().Object,
            Guid.NewGuid(),
            null,
            default,
            CancellationToken.None));

        // Per-file set is complete (WriteAll committed before the failing stale-flat Delete).
        Assert.NotEmpty(definition.ConnectionReferences);
        foreach (var cr in definition.ConnectionReferences)
        {
            var p = PerFilePath(cr.ConnectionReferenceLogicalName.Value!);
            Assert.True(inner.Exists(p),
                $"Per-file CR '{p}' must be written before the stale-flat removal (D33). " +
                "Present files:\n" + string.Join("\n", inner.Files.Keys.OrderBy(k => k)));
        }

        // The flat file is still present (its Delete threw) - recoverable, never both-gone.
        Assert.True(inner.Exists(FlatPath), "Stale flat file should remain after the failed Delete.");
    }

    [Fact]
    public async Task RecoverableIntermediateState_PerFileCompletePlusFlatPresent_NoPhantomDeletesOnNextPush()
    {
        // The interruption window the D33 ordering leaves recoverable: the per-file set is
        // complete AND a stale flat file is still present. The next push must NOT synthesize
        // a delete for any cloud connection reference (the reader prefers the per-file set).
        var (_, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        // Sanity: the per-file set is present from the clone.
        foreach (var cr in definition.ConnectionReferences)
        {
            Assert.True(accessor.Exists(PerFilePath(cr.ConnectionReferenceLogicalName.Value!)));
        }

        // Re-introduce a stale flat file to simulate the post-WriteAll / pre-flat-removal state.
        await accessor.WriteAsync(FlatPath,
            Encoding.UTF8.GetBytes("# stale classic-shape file\nconnectionReferences:\n  []\n"),
            CancellationToken.None);

        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        Assert.DoesNotContain(changeset.ConnectionReferenceChanges,
            c => c is ConnectionReferenceDelete);
    }

    // --- Fault-injecting file accessor: throws IOException on Delete of one path ----------

    private sealed class FaultOnDeleteFileAccessorFactory : IFileAccessorFactory
    {
        private readonly string _faultDeletePath;
        private readonly Dictionary<string, FaultOnDeleteFileAccessor> _accessors = new();

        public FaultOnDeleteFileAccessorFactory(string faultDeletePath) => _faultDeletePath = faultDeletePath;

        public IFileAccessor Create(DirectoryPath workspaceFolder)
        {
            var key = workspaceFolder.ToString();
            if (!_accessors.TryGetValue(key, out var accessor))
            {
                accessor = new FaultOnDeleteFileAccessor(new InMemoryFileAccessor(workspaceFolder), _faultDeletePath);
                _accessors[key] = accessor;
            }
            return accessor;
        }
    }

    private sealed class FaultOnDeleteFileAccessor : IFileAccessor
    {
        private readonly string _faultDeletePath;

        public FaultOnDeleteFileAccessor(InMemoryFileAccessor inner, string faultDeletePath)
        {
            Inner = inner;
            _faultDeletePath = faultDeletePath;
        }

        public InMemoryFileAccessor Inner { get; }

        public IReadOnlyDictionary<string, byte[]> Files => Inner.Files;

        public bool Exists(AgentFilePath path) => Inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => Inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => Inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => Inner.OpenRead(path);

        public void Delete(AgentFilePath path)
        {
            if (string.Equals(path.ToString(), _faultDeletePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Injected IO failure deleting '{path}'.");
            }
            Inner.Delete(path);
        }

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => Inner.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
            => Inner.ListFiles(relativeFolder, filePattern);
    }
}
