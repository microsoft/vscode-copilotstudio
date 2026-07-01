// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AiPromptWriteGateTests
{
    [Fact]
    public async Task GetAIPrompts_SkipsRewrite_WhenContentUnchanged_AndRewritesOnlyTheChangedFile()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "prompt-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");

        try
        {
            var modelId = Guid.NewGuid();
            var customConfig = "{\"version\":\"1\"}";
            var mockDataverse = new Mock<ISyncDataverseClient>();
            mockDataverse
                .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new[] { new AIPromptMetadata { AIModelId = modelId, Name = "P1", CustomConfiguration = customConfig } });

            var accessor = new CountingFileAccessor();
            var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
            var promptJsonKey = $"prompts/P1-{modelId}/prompt.json";
            var metadataKey = $"prompts/P1-{modelId}/metadata.yml";

            await synchronizer.GetAIPromptsAsync(workspace, mockDataverse.Object, syncInfo, accessor, CancellationToken.None);
            Assert.Equal(1, accessor.ReplaceCount(promptJsonKey));
            Assert.Equal(1, accessor.ReplaceCount(metadataKey));

            await synchronizer.GetAIPromptsAsync(workspace, mockDataverse.Object, syncInfo, accessor, CancellationToken.None);
            Assert.Equal(1, accessor.ReplaceCount(promptJsonKey));
            Assert.Equal(1, accessor.ReplaceCount(metadataKey));

            customConfig = "{\"version\":\"2\"}";
            await synchronizer.GetAIPromptsAsync(workspace, mockDataverse.Object, syncInfo, accessor, CancellationToken.None);
            Assert.Equal(2, accessor.ReplaceCount(promptJsonKey));
            Assert.Equal(1, accessor.ReplaceCount(metadataKey));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, true);
            }
        }
    }

    [Fact]
    public async Task GetAIPrompts_DeletesStalePromptJson_WhenRemoteConfigurationCleared()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "prompt-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");

        try
        {
            var modelId = Guid.NewGuid();
            string? customConfig = "{\"version\":\"1\"}";
            var mockDataverse = new Mock<ISyncDataverseClient>();
            mockDataverse
                .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new[] { new AIPromptMetadata { AIModelId = modelId, Name = "P1", CustomConfiguration = customConfig } });

            var accessor = new CountingFileAccessor();
            var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
            var promptJsonPath = new AgentFilePath($"prompts/P1-{modelId}/prompt.json");
            var metadataPath = new AgentFilePath($"prompts/P1-{modelId}/metadata.yml");

            await synchronizer.GetAIPromptsAsync(workspace, mockDataverse.Object, syncInfo, accessor, CancellationToken.None);
            Assert.True(accessor.Exists(promptJsonPath));
            Assert.True(accessor.Exists(metadataPath));

            customConfig = null;
            await synchronizer.GetAIPromptsAsync(workspace, mockDataverse.Object, syncInfo, accessor, CancellationToken.None);

            Assert.False(accessor.Exists(promptJsonPath));
            Assert.True(accessor.Exists(metadataPath));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, true);
            }
        }
    }

    private sealed class CountingFileAccessor : IFileAccessor
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _replaceCounts = new(StringComparer.Ordinal);

        public int ReplaceCount(string path) => _replaceCounts.TryGetValue(path, out var count) ? count : 0;

        public bool Exists(AgentFilePath path) => _files.ContainsKey(path.ToString());

        public Stream OpenRead(AgentFilePath path)
            => _files.TryGetValue(path.ToString(), out var data) ? new MemoryStream(data, writable: false) : throw new FileNotFoundException(path.ToString());

        public Stream OpenWrite(AgentFilePath path) => new CapturingStream(path.ToString(), _files);

        public void Delete(AgentFilePath path) => _files.Remove(path.ToString());

        public void CreateHiddenDirectory(AgentFilePath path) { }

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
        {
            var target = targetPath.ToString();
            if (_files.TryGetValue(sourcePath.ToString(), out var data))
            {
                _files[target] = data;
                _files.Remove(sourcePath.ToString());
            }

            _replaceCounts[target] = ReplaceCount(target) + 1;
        }

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
        {
            foreach (var key in _files.Keys)
            {
                yield return new AgentFilePath(key);
            }
        }

        private sealed class CapturingStream : MemoryStream
        {
            private readonly string _key;
            private readonly Dictionary<string, byte[]> _store;

            public CapturingStream(string key, Dictionary<string, byte[]> store)
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
    }
}
