// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AiPromptPushTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryPath _workspace;
    private readonly Guid _modelId = Guid.NewGuid();
    private readonly string _promptFolder;

    public AiPromptPushTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcs-aiprompt-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new DirectoryPath(_root.Replace('\\', '/') + "/");
        _promptFolder = Path.Combine(_root, "prompts", "MyPrompt-" + _modelId.ToString("D"));
        Directory.CreateDirectory(_promptFolder);
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

    private void WritePromptFiles(string instruction)
    {
        File.WriteAllText(Path.Combine(_promptFolder, "metadata.yml"), "name: My Prompt\n");
        File.WriteAllText(Path.Combine(_promptFolder, "prompt.json"), "{ \"instruction\": \"" + instruction + "\" }");
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_UnchangedSinceLastPush_SkipsUpsert()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<SyncDataverseClient.AIPromptMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncDataverseClient.AIPromptResponse { PromptName = "My Prompt", ErrorMessage = string.Empty });

        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);
        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        dataverse.Verify(
            c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<SyncDataverseClient.AIPromptMetadata>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_UnchangedSinceLastPush_StillReturnsMetadataForCache()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<SyncDataverseClient.AIPromptMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncDataverseClient.AIPromptResponse { PromptName = "My Prompt", ErrorMessage = string.Empty });

        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);
        var (responses, prompts) = await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(responses);
        var metadata = Assert.Single(prompts);
        Assert.Equal(_modelId, metadata.AIModelId);
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_PromptContentChanged_ReUpserts()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<SyncDataverseClient.AIPromptMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncDataverseClient.AIPromptResponse { PromptName = "My Prompt", ErrorMessage = string.Empty });

        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        WritePromptFiles("Translate the input to French.");
        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        dataverse.Verify(
            c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<SyncDataverseClient.AIPromptMetadata>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_PublishFails_DoesNotRecordBaseline()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<SyncDataverseClient.AIPromptMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncDataverseClient.AIPromptResponse { PromptName = "My Prompt", ErrorMessage = "boom" });

        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);
        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        dataverse.Verify(
            c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<SyncDataverseClient.AIPromptMetadata>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
