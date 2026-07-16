// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Utilities;
    using Moq;
    using Xunit;

    public class AiPromptLocalReadTests : IDisposable
    {
        private readonly string _rootSystemPath;
        private readonly string _rootForwardPath;
        private readonly PhysicalFileProvider _physicalProvider;
        private readonly List<string> _requestedFilePaths = new();
        private readonly AgentFilesAnalyzer _analyzer;

        public AiPromptLocalReadTests()
        {
            _rootSystemPath = Path.Combine(Path.GetTempPath(), "AiPromptLocalReadTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootSystemPath);
            _rootForwardPath = Path.GetFullPath(_rootSystemPath).Replace('\\', '/');
            _physicalProvider = new PhysicalFileProvider(_rootSystemPath);

            var fileProvider = new Mock<IClientWorkspaceFileProvider>();
            fileProvider.Setup(provider => provider.GetDirectoryContents(It.IsAny<DirectoryPath>()))
                .Returns((DirectoryPath path) => _physicalProvider.GetDirectoryContents(ToRelative(path.ToString())));
            fileProvider.Setup(provider => provider.GetFileInfo(It.IsAny<FilePath>()))
                .Returns((FilePath path) =>
                {
                    _requestedFilePaths.Add(ToRelative(path.ToString()));
                    return _physicalProvider.GetFileInfo(ToRelative(path.ToString()));
                });

            _analyzer = new AgentFilesAnalyzer(fileProvider.Object);
        }

        [Fact]
        public void NewPromptNotInCache_IsReturnedWithParsedIO()
        {
            var modelId = Guid.NewGuid();
            WritePromptFolder("promptE2", modelId, withPromptJson: true);

            var definitions = _analyzer.ReadNewLocalAiModelDefinitions(AgentRoot(), new HashSet<Guid>());

            var definition = Assert.Single(definitions);
            Assert.True(definition.Id.HasValue);
            Assert.Equal(modelId, definition.Id.Value);
            Assert.NotNull(definition.InputType);
            Assert.NotNull(definition.OutputType);
        }

        [Fact]
        public void PromptAlreadyInCache_IsSkippedAndNeverRead()
        {
            var cachedModelId = Guid.NewGuid();
            WritePromptFolder("cached", cachedModelId, withPromptJson: true);

            var definitions = _analyzer.ReadNewLocalAiModelDefinitions(AgentRoot(), new HashSet<Guid> { cachedModelId });

            Assert.Empty(definitions);
            Assert.DoesNotContain(_requestedFilePaths, path => path.Contains($"cached-{cachedModelId}"));
        }

        [Fact]
        public void OnlyNewPromptsAreReadWhenCacheHasSome()
        {
            var cachedModelId = Guid.NewGuid();
            var newModelId = Guid.NewGuid();
            WritePromptFolder("cached", cachedModelId, withPromptJson: true);
            WritePromptFolder("fresh", newModelId, withPromptJson: true);

            var definitions = _analyzer.ReadNewLocalAiModelDefinitions(AgentRoot(), new HashSet<Guid> { cachedModelId });

            var definition = Assert.Single(definitions);
            Assert.Equal(newModelId, definition.Id!.Value);
            Assert.Contains(_requestedFilePaths, path => path.Contains($"fresh-{newModelId}"));
            Assert.DoesNotContain(_requestedFilePaths, path => path.Contains($"cached-{cachedModelId}"));
        }

        [Fact]
        public void FolderWithoutTrailingGuid_IsSkipped()
        {
            Directory.CreateDirectory(Path.Combine(_rootSystemPath, "prompts", "not-a-prompt"));

            var definitions = _analyzer.ReadNewLocalAiModelDefinitions(AgentRoot(), new HashSet<Guid>());

            Assert.Empty(definitions);
        }

        [Fact]
        public void PromptMissingPromptJson_IsReturnedWithNullIO()
        {
            var modelId = Guid.NewGuid();
            WritePromptFolder("metadataonly", modelId, withPromptJson: false);

            var definitions = _analyzer.ReadNewLocalAiModelDefinitions(AgentRoot(), new HashSet<Guid>());

            var definition = Assert.Single(definitions);
            Assert.Equal(modelId, definition.Id!.Value);
            Assert.Null(definition.InputType);
            Assert.Null(definition.OutputType);
        }

        [Fact]
        public void PromptMissingMetadataYml_IsSkipped()
        {
            var modelId = Guid.NewGuid();
            var folder = Path.Combine(_rootSystemPath, "prompts", $"nometadata-{modelId}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "prompt.json"), "{\"name\":\"x\",\"instruction\":\"y\",\"model\":\"gpt-41-mini\"}");

            var definitions = _analyzer.ReadNewLocalAiModelDefinitions(AgentRoot(), new HashSet<Guid>());

            Assert.Empty(definitions);
        }

        [Fact]
        public void NoPromptsFolder_ReturnsEmpty()
        {
            var definitions = _analyzer.ReadNewLocalAiModelDefinitions(AgentRoot(), new HashSet<Guid>());

            Assert.True(definitions.IsEmpty);
        }

        private DirectoryPath AgentRoot() => new DirectoryPath(_rootForwardPath);

        private void WritePromptFolder(string name, Guid modelId, bool withPromptJson)
        {
            var folder = Path.Combine(_rootSystemPath, "prompts", $"{name}-{modelId}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "metadata.yml"), $"aIModelId: {modelId}\nname: {name}\ntemplateId: {Guid.Empty}\n");

            if (withPromptJson)
            {
                File.WriteAllText(Path.Combine(folder, "prompt.json"),
                    "{\"name\":\"" + name + "\",\"instruction\":\"answer {{question}}\",\"model\":\"gpt-41-mini\",\"inputs\":[{\"id\":\"question\",\"type\":\"text\"}],\"output\":{\"formats\":[\"text\"]}}");
            }
        }

        private string ToRelative(string fullPath)
        {
            var normalizedRoot = _rootForwardPath.TrimEnd('/') + "/";
            return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(normalizedRoot.Length)
                : fullPath;
        }

        public void Dispose()
        {
            _physicalProvider.Dispose();
            if (Directory.Exists(_rootSystemPath))
            {
                Directory.Delete(_rootSystemPath, recursive: true);
            }
        }
    }
}
