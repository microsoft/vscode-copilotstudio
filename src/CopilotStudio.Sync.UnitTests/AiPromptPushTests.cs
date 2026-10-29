// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Moq.Protected;
using System.Net;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AiPromptPushTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryPath _workspace;
    private readonly Guid _modelId = Guid.NewGuid();
    private readonly string _promptFolder;
    private readonly Dictionary<Guid, AIPromptMetadata> _serverPrompts = new();

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
        var island = new Mock<IIslandControlPlaneService>();
        island
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, null, "token-1"));

        return new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), new FileAccessorFactory(), island.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
    }

    private Mock<ISyncDataverseClient> CreateDataverse(string errorMessage = "", Func<AIPromptMetadata, AIPromptMetadata>? storePrompt = null)
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<AIPromptMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid? _, AIPromptMetadata sent, CancellationToken _) =>
            {
                if (errorMessage.Length == 0)
                {
                    _serverPrompts[sent.AIModelId] = storePrompt?.Invoke(sent) ?? new AIPromptMetadata { AIModelId = sent.AIModelId, Name = sent.Name, CustomConfiguration = sent.CustomConfiguration };
                }

                return new AIPromptResponse { PromptName = sent.Name, ErrorMessage = errorMessage };
            });
        dataverse
            .Setup(c => c.DownloadAIPromptsByModelIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> aiModelIds, CancellationToken _) => aiModelIds
                .Select(aiModelId => _serverPrompts.TryGetValue(aiModelId, out var prompt) ? prompt : new AIPromptMetadata { AIModelId = aiModelId, IsUnreadableReferencePlaceholder = true })
                .ToArray());
        return dataverse;
    }

    private AIPromptMetadata ServerPrompt(string instruction) =>
        new() { AIModelId = _modelId, Name = "My Prompt", CustomConfiguration = AiPromptProjection.BuildCustomConfigurationFromPromptJson("{ \"instruction\": \"" + instruction + "\" }") };

    private void WritePromptFiles(string instruction)
    {
        File.WriteAllText(Path.Combine(_promptFolder, "metadata.yml"), "name: My Prompt\n");
        WritePromptJson(instruction);
    }

    private void WritePromptJson(string instruction) => File.WriteAllText(Path.Combine(_promptFolder, "prompt.json"), "{ \"instruction\": \"" + instruction + "\" }");

    private string? ReadCloudCopy(string fileSuffix = "") => SyncedAssetKind.ReadCloudCopy(new FileAccessorFactory().Create(_workspace), $"Mcs.AIPrompt.{_modelId}{fileSuffix}");

    private void WriteCloudCache() => WorkspaceSynchronizer.WriteCloudCache(new FileAccessorFactory().Create(_workspace), ComponentWriterDefensiveTests.CreateAgentDefinition());

    private async Task<WorkspaceSynchronizer> SyncPromptAsync(Mock<ISyncDataverseClient> dataverse)
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");
        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);
        WriteCloudCache();
        return synchronizer;
    }

    private async Task<IReadOnlyList<Change>> LocalPromptChangesAsync(WorkspaceSynchronizer synchronizer)
    {
        var (_, changes) = await synchronizer.GetLocalChangesAsync(_workspace, ComponentWriterDefensiveTests.CreateAgentDefinition(), CancellationToken.None);
        return changes.Where(change => change.ChangeKind == AiPromptLayout.ChangeKind).ToList();
    }

    private async Task<IReadOnlyList<Change>> RemotePromptChangesAsync(WorkspaceSynchronizer synchronizer, ISyncDataverseClient dataverse)
    {
        var (_, changes) = await synchronizer.GetRemoteChangesAsync(_workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), dataverse, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);
        return changes.Where(change => change.ChangeKind == AiPromptLayout.ChangeKind).ToList();
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_UnchangedSinceLastPush_SkipsUpsert()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");
        var dataverse = CreateDataverse();

        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);
        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        dataverse.Verify(
            c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<AIPromptMetadata>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_UnchangedSinceLastPush_StillReturnsTheServerCopyForTheCache()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");
        var dataverse = CreateDataverse();

        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);
        var (responses, prompts) = await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(responses);
        var prompt = Assert.Single(prompts);
        Assert.Equal(_modelId, prompt.AIModelId);
        Assert.Equal(_serverPrompts[_modelId].CustomConfiguration, prompt.CustomConfiguration);
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_PromptContentChanged_ReUpserts()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");
        var dataverse = CreateDataverse();

        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        WritePromptFiles("Translate the input to French.");
        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        dataverse.Verify(
            c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<AIPromptMetadata>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_PublishFails_DoesNotRecordBaselineOrReturnThePrompt()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");
        var dataverse = CreateDataverse(errorMessage: "boom");

        var (_, prompts) = await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);
        await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(prompts);
        dataverse.Verify(
            c => c.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<AIPromptMetadata>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_ServerCopyDiffersFromSent_FolderBaselineAndResultFollowTheServer()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");
        var dataverse = CreateDataverse(storePrompt: _ => ServerPrompt("Normalized by the server."));

        var (_, prompts) = await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);
        WriteCloudCache();

        Assert.Contains("Normalized by the server.", File.ReadAllText(Path.Combine(_promptFolder, "prompt.json")), StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(Path.Combine(_promptFolder, "prompt.json")), ReadCloudCopy());
        Assert.Equal(File.ReadAllText(Path.Combine(_promptFolder, "metadata.yml")), ReadCloudCopy(".metadata"));
        Assert.Equal(_serverPrompts[_modelId].CustomConfiguration, Assert.Single(prompts).CustomConfiguration);
        Assert.Empty(await LocalPromptChangesAsync(synchronizer));
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_ReadBackFails_ReturnsNoPromptsAndRecordsNoBaseline()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");
        var dataverse = CreateDataverse();
        dataverse
            .Setup(c => c.DownloadAIPromptsByModelIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("offline"));

        var (_, prompts) = await synchronizer.UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(prompts);
        Assert.False(File.Exists(Path.Combine(_root, ".mcs", ".aiprompts-sync.json")));
    }

    [Fact]
    public async Task UpsertAIPromptsForAgentAsync_NoPromptFolders_MakesNoCall()
    {
        Directory.Delete(_promptFolder);
        var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

        var (responses, prompts) = await CreateSynchronizer().UpsertAIPromptsForAgentAsync(_workspace, dataverse.Object, Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(responses);
        Assert.Empty(prompts);
        dataverse.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpsertAIPromptAsync_WithNothingToPublish_ReturnsThePromptId()
    {
        var response = await CreateRefusingClient().UpsertAIPromptAsync(Guid.NewGuid(), new AIPromptMetadata { AIModelId = _modelId, Name = "My Prompt" }, CancellationToken.None);

        Assert.Equal(_modelId, response.AIModelId);
        Assert.Empty(response.ErrorMessage!);
    }

    [Fact]
    public async Task UpsertAIPromptAsync_WhenTheServiceRefusesThePrompt_StillReturnsThePromptId()
    {
        var response = await CreateRefusingClient().UpsertAIPromptAsync(Guid.NewGuid(), ServerPrompt("Summarize the input."), CancellationToken.None);

        Assert.Equal(_modelId, response.AIModelId);
        Assert.False(string.IsNullOrEmpty(response.ErrorMessage));
    }

    private static SyncDataverseClient CreateRefusingClient()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"invalid prompt\"}}") });
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(handler.Object));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");
        return client;
    }

    [Fact]
    public async Task ChangeDetection_SyncedPrompt_ReportsNoChanges()
    {
        var dataverse = CreateDataverse();
        var synchronizer = await SyncPromptAsync(dataverse);

        Assert.Empty(await LocalPromptChangesAsync(synchronizer));
        Assert.Empty(await RemotePromptChangesAsync(synchronizer, dataverse.Object));
    }

    [Fact]
    public async Task ChangeDetection_PromptEditedLocally_ReportsALocalUpdateOnThePromptFileOnly()
    {
        var synchronizer = await SyncPromptAsync(CreateDataverse());
        var cloudCopy = ReadCloudCopy();
        WritePromptJson("Translate the input to French.");

        var change = Assert.Single(await LocalPromptChangesAsync(synchronizer));

        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Equal($"prompts/MyPrompt-{_modelId}/prompt.json", change.Uri);
        Assert.Equal($"Mcs.AIPrompt.{_modelId}", change.SchemaName);
        Assert.Equal(cloudCopy, ReadCloudCopy());
    }

    [Fact]
    public async Task ChangeDetection_PromptMetadataEditedLocally_ReportsALocalUpdateOnTheMetadataFileOnly()
    {
        var synchronizer = await SyncPromptAsync(CreateDataverse());
        var metadataPath = Path.Combine(_promptFolder, "metadata.yml");
        File.WriteAllText(metadataPath, File.ReadAllText(metadataPath).Replace("My Prompt", "Renamed Prompt"));

        var change = Assert.Single(await LocalPromptChangesAsync(synchronizer));

        Assert.Equal($"prompts/MyPrompt-{_modelId}/metadata.yml", change.Uri);
        Assert.Equal($"Mcs.AIPrompt.{_modelId}.metadata", change.SchemaName);
    }

    [Fact]
    public async Task ChangeDetection_PromptEditedInTheCloud_ReportsARemoteUpdateWithTheServerPrompt()
    {
        var dataverse = CreateDataverse();
        var synchronizer = await SyncPromptAsync(dataverse);
        _serverPrompts[_modelId] = ServerPrompt("Edited in the portal.");

        var change = Assert.Single(await RemotePromptChangesAsync(synchronizer, dataverse.Object));

        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Equal($"Mcs.AIPrompt.{_modelId}", change.SchemaName);
        Assert.Contains("Edited in the portal.", change.RemoteContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangeDetection_PromptDeletedInTheCloud_ReportsARemoteDeleteForEachFile()
    {
        var dataverse = CreateDataverse();
        var synchronizer = await SyncPromptAsync(dataverse);
        _serverPrompts.Clear();

        var changes = await RemotePromptChangesAsync(synchronizer, dataverse.Object);

        Assert.Equal(new[] { $"Mcs.AIPrompt.{_modelId}", $"Mcs.AIPrompt.{_modelId}.metadata" }, changes.Select(change => change.SchemaName));
        Assert.All(changes, change => Assert.Equal(ChangeType.Delete, change.ChangeType));
    }

    [Fact]
    public async Task ChangeDetection_NewLocalPrompt_ReportsOnlyLocalCreates()
    {
        var synchronizer = CreateSynchronizer();
        WritePromptFiles("Summarize the input.");
        WriteCloudCache();

        var changes = await LocalPromptChangesAsync(synchronizer);

        Assert.Equal(new[] { "prompt.json", "metadata.yml" }, changes.Select(change => Path.GetFileName(change.Uri)));
        Assert.All(changes, change => Assert.Equal(ChangeType.Create, change.ChangeType));
        Assert.Empty(await RemotePromptChangesAsync(synchronizer, CreateDataverse().Object));
    }

    [Fact]
    public async Task ChangeDetection_NoPromptFolders_DoesNotQueryPrompts()
    {
        var synchronizer = CreateSynchronizer();
        WriteCloudCache();
        var dataverse = CreateDataverse();

        Assert.Empty(await RemotePromptChangesAsync(synchronizer, dataverse.Object));

        dataverse.Verify(
            c => c.DownloadAIPromptsByModelIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
