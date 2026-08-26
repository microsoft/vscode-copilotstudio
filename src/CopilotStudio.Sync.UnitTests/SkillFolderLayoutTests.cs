// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SkillFolderLayoutTests
{
    private const string Bot = "cr123_natest";

    private const string CliSettings =
        "displayName: NA Test\n" +
        "schemaName: cr123_natest\n" +
        "configuration:\n" +
        "  recognizer:\n" +
        "    kind: CLICopilotRecognizer\n" +
        "  agentSettings:\n" +
        "    model:\n" +
        "      series: Sonnet46\n" +
        "    instructions:\n" +
        "      segments:\n" +
        "        - kind: StaticSegment\n" +
        "          value: Test instructions.\n" +
        "template: cliagent-1.0.0\n" +
        "language: 1033\n";

    private static async Task<(WorkspaceSynchronizer Sync, InMemoryFileAccessor Accessor, DirectoryPath Workspace)> CreateWorkspaceAsync()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-layout-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);
        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        return (synchronizer, accessor, workspace);
    }

    private static BotDefinition CloudDefinition()
        => new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!);

    private static void Write(InMemoryFileAccessor accessor, string path, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Read(InMemoryFileAccessor accessor, string path)
    {
        using var stream = accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static DialogComponent CreateSkill(string schemaName, string displayName, Guid id, string? content, string description = "")
    {
        var dialog = new InlineAgentSkill.Builder { Content = content }.Build();
        return new DialogComponent(schemaName: schemaName, displayName: displayName, description: description, id: id, parentBotComponentId: default, dialog: dialog);
    }

    private static FileAttachmentComponent CreateAsset(string schemaName, string displayName, BotComponentId parentId)
    {
        var builder = new FileAttachmentComponent().WithSchemaName(schemaName).WithDisplayName(displayName).ToBuilder();
        builder.Id = Guid.NewGuid();
        builder.ParentBotComponentId = parentId;
        return builder.Build();
    }

    private static Task<(PvaComponentChangeSet, System.Collections.Immutable.ImmutableArray<Change>)> GetChangesAsync(WorkspaceSynchronizer synchronizer, DirectoryPath workspace, DefinitionBase definition)
        => synchronizer.GetLocalChangesAsync(workspace, definition, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

    [Fact]
    public void Projection_SkillAnchor_IsFixedNameInsideFolder()
    {
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_x9Z", "get-us-weather", Guid.NewGuid(), null);

        var path = LspProjection.GetFilePath(typeof(InlineAgentSkill), skill.SchemaNameString!, Bot, null, null, AuthoringShape.CliCopilot, skill, null);

        Assert.Equal("behaviors/get-us-weather/skill.mcs.yml", path);
    }

    [Fact]
    public void Projection_SkillAnchorPath_RoundTripsToSameSchema()
    {
        var schema = LspProjection.GetSchemaName("behaviors/get-us-weather/skill", Bot, typeof(InlineAgentSkill), AuthoringShape.CliCopilot);

        Assert.Equal($"{Bot}.skill.get-us-weather", schema);
    }

    [Fact]
    public void Projection_FolderRootAssetNamedSkill_DoesNotCollideWithAnchor()
    {
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), null);
        var asset = CreateAsset($"{Bot}.file.skill_a1B", "skill", skill.Id);
        var definition = new BotDefinition().WithComponents(new BotComponentBase[] { skill, asset });

        var anchorPath = LspProjection.GetFilePath(typeof(InlineAgentSkill), skill.SchemaNameString!, Bot, null, null, AuthoringShape.CliCopilot, skill, definition);
        var assetPath = LspProjection.GetFilePath(typeof(FileAttachmentComponentMetadata), asset.SchemaNameString!, Bot, null, null, AuthoringShape.CliCopilot, asset, definition);

        Assert.Equal("behaviors/get-us-weather/skill.mcs.yml", anchorPath);
        Assert.Equal("behaviors/get-us-weather/skill_a1B.mcs.yml", assetPath);
    }

    [Fact]
    public void Projection_NestedAsset_SidecarSitsBesideContentFile()
    {
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), null);
        var asset = CreateAsset($"{Bot}.file.docsinstruction.txt", "docs/instruction.txt", skill.Id);
        var definition = new BotDefinition().WithComponents(new BotComponentBase[] { skill, asset });

        var assetPath = LspProjection.GetFilePath(typeof(FileAttachmentComponentMetadata), asset.SchemaNameString!, Bot, null, null, AuthoringShape.CliCopilot, asset, definition);

        Assert.Equal("behaviors/get-us-weather/docs/instruction.txt.mcs.yml", assetPath);
    }

    [Fact]
    public void Projection_SameStemDifferentExtensions_ProduceDistinctSidecars()
    {
        var skill = CreateSkill($"{Bot}.skill.reports", "reports", Guid.NewGuid(), null);
        var csv = CreateAsset($"{Bot}.file.reportcsv", "report.csv", skill.Id);
        var pdf = CreateAsset($"{Bot}.file.reportpdf", "report.pdf", skill.Id);
        var definition = new BotDefinition().WithComponents(new BotComponentBase[] { skill, csv, pdf });

        var csvPath = LspProjection.GetFilePath(typeof(FileAttachmentComponentMetadata), csv.SchemaNameString!, Bot, null, null, AuthoringShape.CliCopilot, csv, definition);
        var pdfPath = LspProjection.GetFilePath(typeof(FileAttachmentComponentMetadata), pdf.SchemaNameString!, Bot, null, null, AuthoringShape.CliCopilot, pdf, definition);

        Assert.Equal("behaviors/reports/report.csv.mcs.yml", csvPath);
        Assert.Equal("behaviors/reports/report.pdf.mcs.yml", pdfPath);
    }

    [Fact]
    public void Projection_RootKnowledgeFile_KeepsFilenameEncodedSchema()
    {
        var knowledge = CreateAsset($"{Bot}.file.Handbook", "Handbook.pdf", default);
        var definition = new BotDefinition().WithComponents(new BotComponentBase[] { knowledge });

        var path = LspProjection.GetFilePath(typeof(FileAttachmentComponentMetadata), knowledge.SchemaNameString!, Bot, null, null, AuthoringShape.CliCopilot, knowledge, definition);

        Assert.Equal("capabilities/knowledge/files/Handbook.mcs.yml", path);
    }

    [Fact]
    public async Task Anchor_CarriesSchemaNameAndSurvivesRepeatedReads()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather_x9Z\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "body\n");

        var first = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var second = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var firstSkill = first.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        var secondSkill = second.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.Equal($"{Bot}.skill.get-us-weather_x9Z", firstSkill.SchemaNameString);
        Assert.Equal(firstSkill.SchemaNameString, secondSkill.SchemaNameString);
    }

    [Fact]
    public async Task BareSkill_ManifestIsContent_AndHasNoFileComponent()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "---\nname: get-us-weather\n---\nInstructions.\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = read.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.Equal("---\nname: get-us-weather\n---\nInstructions.\n", ((InlineAgentSkill)skill.Dialog!).Content);
        Assert.Empty(read.Components.OfType<FileAttachmentComponent>());
    }

    [Fact]
    public async Task PackagedSkill_ContentIsBundleMarker_AndManifestIsAComponent()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\n  bundle: {Bot}.file.getusweatherzip\n  manifestSchemaName: {Bot}.file.getusweather.SKILL.md\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = read.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.Equal($"<!-- bic:bundle={Bot}.file.getusweatherzip -->", ((InlineAgentSkill)skill.Dialog!).Content);
        var manifest = Assert.Single(read.Components.OfType<FileAttachmentComponent>(), component => SkillLayout.IsManifest(component.DisplayName));
        Assert.Equal($"{Bot}.file.getusweather.SKILL.md", manifest.SchemaNameString);
    }

    [Fact]
    public async Task Promotion_NewSkillWithAsset_CreatesBundleManifestAndAssetComponents()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");

        var (_, changes) = await GetChangesAsync(sync, workspace, CloudDefinition());

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == $"{Bot}.skill.get-us-weather");
        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var skill = read.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.StartsWith(SkillLayout.BundleMarkerPrefix, ((InlineAgentSkill)skill.Dialog!).Content, StringComparison.Ordinal);
        Assert.Single(read.Components.OfType<FileAttachmentComponent>(), component => SkillLayout.IsManifest(component.DisplayName));
        Assert.Single(read.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "scripts/run.ps1");
    }

    [Fact]
    public async Task PackagedSkillLosesLastAsset_KeepsBundleAndManifest()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", skillId, $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.getusweather.SKILL.md", "SKILL.md", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\n  bundle: {Bot}.file.getusweatherzip\n  manifestSchemaName: {Bot}.file.getusweather.SKILL.md\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.DoesNotContain(changes, change => change.ChangeType == ChangeType.Delete);
        Assert.Contains(read.Components.OfType<FileAttachmentComponent>(), component => component.SchemaNameString == manifest.SchemaNameString);
    }

    [Fact]
    public async Task UnchangedPackagedSkill_ProducesNoChanges()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.getusweather.SKILL.md", "SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.scriptsrunps1_a1B", "scripts/run.ps1", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest, script });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\n  bundle: {Bot}.file.getusweatherzip\n  manifestSchemaName: {Bot}.file.getusweather.SKILL.md\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1.mcs.yml", $"mcs.metadata:\n  componentName: scripts/run.ps1\n  schemaName: {Bot}.file.scriptsrunps1_a1B\nkind: FileAttachmentComponentMetadata\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task NestedAssetSchema_SurvivesFolderRename()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/renamed-folder/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather_x9Z\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/renamed-folder/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/renamed-folder/scripts/run.ps1", "script\n");
        Write(accessor, "behaviors/renamed-folder/scripts/run.ps1.mcs.yml", $"mcs.metadata:\n  componentName: scripts/run.ps1\n  schemaName: {Bot}.file.scriptsrunps1_a1B\nkind: FileAttachmentComponentMetadata\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = read.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.Equal($"{Bot}.skill.get-us-weather_x9Z", skill.SchemaNameString);
        Assert.Contains(read.Components.OfType<FileAttachmentComponent>(), component => component.SchemaNameString == $"{Bot}.file.scriptsrunps1_a1B");
    }

    [Fact]
    public async Task TwoSkillsWithManifests_DoNotCollide()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/skill-a/skill.mcs.yml", $"mcs.metadata:\n  componentName: skill-a\n  schemaName: {Bot}.skill.skill-a\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/skill-a/SKILL.md", "a\n");
        Write(accessor, "behaviors/skill-a/notes.txt", "a notes\n");
        Write(accessor, "behaviors/skill-b/skill.mcs.yml", $"mcs.metadata:\n  componentName: skill-b\n  schemaName: {Bot}.skill.skill-b\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/skill-b/SKILL.md", "b\n");
        Write(accessor, "behaviors/skill-b/notes.txt", "b notes\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var manifests = read.Components.OfType<FileAttachmentComponent>().Where(component => SkillLayout.IsManifest(component.DisplayName)).ToList();
        Assert.Equal(2, manifests.Count);
        Assert.Equal(2, manifests.Select(component => component.SchemaNameString).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task LegacyLayout_ReadsWithoutRewritingDisk()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_x9Z", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_dWN", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.scriptsrunps1_9GR", "./scripts/run.ps1", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest, script });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: <!-- bic:bundle={Bot}.file.getusweatherzip -->\n");
        Write(accessor, "behaviors/get-us-weather/.skill.json", $"{{ \"schemaName\": \"{Bot}.skill.get-us-weather_x9Z\", \"folderName\": \"get-us-weather\" }}");
        Write(accessor, "behaviors/get-us-weather/skillmd_dWN.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n");
        Write(accessor, "behaviors/get-us-weather/scriptsrunps1_9GR.mcs.yml", "mcs.metadata:\n  componentName: ./scripts/run.ps1\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");
        var cacheBefore = Read(accessor, ".mcs/botdefinition.json");
        var keysBefore = accessor.Files.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.Empty(changes);
        Assert.Equal(keysBefore, accessor.Files.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList());
        Assert.Equal(cacheBefore, Read(accessor, ".mcs/botdefinition.json"));
    }

    [Fact]
    public async Task LegacyBareSkillFolderWithOnlyLinkFile_ProducesNoChanges()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_x9Z", "get-us-weather", Guid.NewGuid(), "Instructions.\n");
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: |\n  Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/.skill.json", $"{{ \"schemaName\": \"{Bot}.skill.get-us-weather_x9Z\", \"folderName\": \"get-us-weather\" }}");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task ReadPath_LeavesCloudCacheByteIdentical()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");
        var cacheBefore = Read(accessor, ".mcs/botdefinition.json");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        await GetChangesAsync(sync, workspace, read);

        Assert.Equal(cacheBefore, Read(accessor, ".mcs/botdefinition.json"));
    }

    [Fact]
    public async Task ManifestFrontmatter_RoundTripsVerbatim()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        const string manifestText = "---\nname: get-us-weather\ndescription: 'Answers \"weather\" questions'\nargument-hint: <city>\n---\nBody line.\n";
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), manifestText);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", manifestText);

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.Empty(changes);
        Assert.Equal(manifestText, Read(accessor, "behaviors/get-us-weather/SKILL.md"));
    }

    [Fact]
    public async Task StaleManifestSidecar_IsIgnoredAndRemoved()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", skillId, $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.getusweather.SKILL.md", "SKILL.md", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\n  bundle: {Bot}.file.getusweatherzip\n  manifestSchemaName: {Bot}.file.getusweather.SKILL.md\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md.mcs.yml", $"mcs.metadata:\n  componentName: SKILL.md\n  schemaName: {Bot}.file.getusweather.SKILL.md\nkind: FileAttachmentComponentMetadata\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Single(read.Components.OfType<FileAttachmentComponent>(), component => SkillLayout.IsManifest(component.DisplayName));
    }

    [Fact]
    public void PacSeam_GetComponentProjection_PinsAnchorNestedAssetAndManifestPaths()
    {
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), null);
        var manifest = CreateAsset($"{Bot}.file.getusweather.SKILL.md", "SKILL.md", skill.Id);
        var nested = CreateAsset($"{Bot}.file.docsinstruction.txt", "docs/instruction.txt", skill.Id);
        var definition = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest, nested });

        Assert.Equal("behaviors/get-us-weather/skill.mcs.yml", CliCopilotProjection.GetComponentProjection(skill, definition).BodyPath);
        Assert.Equal("behaviors/get-us-weather/SKILL.md", CliCopilotProjection.GetComponentProjection(manifest, definition).PayloadPath);
        Assert.Equal("behaviors/get-us-weather/docs/instruction.txt", CliCopilotProjection.GetComponentProjection(nested, definition).PayloadPath);
    }
    // Migration deletes flattened sidecars by matching their componentName against each asset's
    // relative path. The new anchor sits at the skill folder root and carries the skill's own
    // componentName, so an asset whose path equals the skill display name used to match it and
    // delete the skill's identity file.
    [Fact]
    public async Task Migration_AssetNamedLikeSkill_DoesNotDeleteAnchor()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-migration-collide-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_x9Z", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_dWN", "./SKILL.md", skill.Id);
        // Asset whose relative path equals the skill's display name.
        var collide = CreateAsset($"{Bot}.file.collide_9GR", "./get-us-weather", skill.Id);
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { skill, manifest, collide });

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: <!-- bic:bundle={Bot}.file.getusweatherzip -->\n");
        Write(accessor, "behaviors/get-us-weather/.skill.json", $"{{ \"schemaName\": \"{Bot}.skill.get-us-weather_x9Z\", \"folderName\": \"get-us-weather\" }}");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/get-us-weather", "payload\n");

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), entity, "token-2"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), cloud, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var keys = accessor.Files.Keys.Select(k => k.Replace('\\', '/')).OrderBy(k => k).ToList();
        Assert.True(
            accessor.Exists(new AgentFilePath("behaviors/get-us-weather/skill.mcs.yml")),
            "ANCHOR WAS DELETED. Files present:\n" + string.Join("\n", keys));
    }

    [Fact]
    public async Task Migration_OnPull_MovesAnchorNestsSidecarsAndDropsLinkFiles()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-migration-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_x9Z", "get-us-weather", skillId, $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_dWN", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.scriptsrunps1_9GR", "./scripts/run.ps1", skill.Id);
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { skill, manifest, script });

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: <!-- bic:bundle={Bot}.file.getusweatherzip -->\n");
        Write(accessor, "behaviors/get-us-weather/.skill.json", $"{{ \"schemaName\": \"{Bot}.skill.get-us-weather_x9Z\", \"folderName\": \"get-us-weather\" }}");
        Write(accessor, "behaviors/get-us-weather/skillmd_dWN.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n");
        Write(accessor, "behaviors/get-us-weather/scriptsrunps1_9GR.mcs.yml", "mcs.metadata:\n  componentName: ./scripts/run.ps1\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), entity, "token-2"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), cloud, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var keys = accessor.Files.Keys.Select(key => key.Replace('\\', '/')).ToList();
        Assert.Contains("behaviors/get-us-weather/skill.mcs.yml", keys);
        Assert.DoesNotContain("behaviors/get-us-weather.mcs.yml", keys);
        Assert.DoesNotContain("behaviors/get-us-weather/.skill.json", keys);
        Assert.DoesNotContain("behaviors/get-us-weather/skillmd_dWN.mcs.yml", keys);
        Assert.DoesNotContain("behaviors/get-us-weather/scriptsrunps1_9GR.mcs.yml", keys);
        Assert.Contains("behaviors/get-us-weather/scripts/run.ps1.mcs.yml", keys);
        Assert.Contains("behaviors/get-us-weather/SKILL.md", keys);

        var anchor = SkillLayout.ReadAnchorMetadata(accessor, "get-us-weather");
        Assert.Equal($"{Bot}.skill.get-us-weather_x9Z", anchor.SchemaName);
        Assert.Equal($"{Bot}.file.getusweatherzip", anchor.Bundle);
        Assert.Equal($"{Bot}.file.skillmd_dWN", anchor.ManifestSchemaName);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(synchronizer, workspace, read);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task Push_ModifiedAnchor_KeepsExactlyOneMetadataBlock()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-push-metadata-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_x9Z", "get-us-weather", Guid.NewGuid(), "Original instructions.\n");
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { skill });

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  description: Edited description\n  schemaName: {Bot}.skill.get-us-weather_x9Z\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Edited instructions.\n");

        mockIsland.Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) => Task.FromResult(new PvaComponentChangeSet(incoming.BotComponentChanges, incoming.Bot, Guid.NewGuid().ToString("N"))));

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        await synchronizer.PushLocalChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), read, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, cloudFlowMetadata: null, System.Collections.Immutable.ImmutableArray<SyncDataverseClient.AIPromptMetadata>.Empty, CancellationToken.None);

        var anchor = Read(accessor, "behaviors/get-us-weather/skill.mcs.yml");
        Assert.Equal(1, anchor.Split('\n').Count(line => line.StartsWith(McsMetadata.PropertyName + ":", StringComparison.Ordinal)));
        Assert.Equal($"{Bot}.skill.get-us-weather_x9Z", SkillLayout.ReadAnchorMetadata(accessor, "get-us-weather").SchemaName);
        Assert.Equal("Edited description", SkillLayout.ReadAnchorMetadata(accessor, "get-us-weather").Description);
    }

    [Fact]
    public async Task Preview_BareSkillAnchor_RendersSameShapeAsFileOnDisk()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        const string manifestText = "---\nname: get-us-weather\n---\nInstructions.\n";
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_x9Z", "get-us-weather", Guid.NewGuid(), manifestText, "Skill description");
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        var anchorText = $"mcs.metadata:\n  componentName: get-us-weather\n  description: Skill description\n  schemaName: {Bot}.skill.get-us-weather_x9Z\nkind: InlineAgentSkill\n";
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", anchorText);
        Write(accessor, "behaviors/get-us-weather/SKILL.md", manifestText);

        var preview = McsComponentBodyWriter.SerializeComponent(skill, cloud, new AgentFilePath("behaviors/get-us-weather/skill.mcs.yml"));

        Assert.DoesNotContain("content:", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Instructions.", preview, StringComparison.Ordinal);
        Assert.StartsWith("mcs.metadata:", preview, StringComparison.Ordinal);
        Assert.Contains($"schemaName: {Bot}.skill.get-us-weather_x9Z", preview, StringComparison.Ordinal);
        Assert.Equal(1, preview.Split('\n').Count(line => line.StartsWith(McsMetadata.PropertyName + ":", StringComparison.Ordinal)));
        Assert.Equal(NormalizeKeys(anchorText), NormalizeKeys(preview));
    }

    [Fact]
    public async Task Preview_PackagedSkillAnchor_RendersBundleMetadataNotContent()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.getusweather.SKILL.md", "SKILL.md", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest });

        var preview = McsComponentBodyWriter.SerializeComponent(skill, cloud, new AgentFilePath("behaviors/get-us-weather/skill.mcs.yml"));

        Assert.DoesNotContain("content:", preview, StringComparison.Ordinal);
        Assert.Contains($"bundle: {Bot}.file.getusweatherzip", preview, StringComparison.Ordinal);
        Assert.Contains($"manifestSchemaName: {Bot}.file.getusweather.SKILL.md", preview, StringComparison.Ordinal);
    }

    private static string NormalizeKeys(string body)
        => string.Join("\n", body.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).OrderBy(line => line, StringComparer.Ordinal));

    [Fact]
    public async Task Migration_AlreadyNestedWorkspace_DoesNotRewriteAnyFile()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-migration-noop-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", skillId, $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.getusweather.SKILL.md", "SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.scriptsrunps1_a1B", "scripts/run.ps1", skill.Id);
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { skill, manifest, script });

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\n  bundle: {Bot}.file.getusweatherzip\n  manifestSchemaName: {Bot}.file.getusweather.SKILL.md\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1.mcs.yml", $"mcs.metadata:\n  componentName: scripts/run.ps1\n  schemaName: {Bot}.file.scriptsrunps1_a1B\nkind: FileAttachmentComponentMetadata\n");

        var before = accessor.Files.Keys.ToDictionary(key => key, key => Read(accessor, key.Replace('\\', '/')), StringComparer.Ordinal);

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), entity, "token-2"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), cloud, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        foreach (var key in before.Keys.Where(key => key.Replace('\\', '/').StartsWith("behaviors/", StringComparison.Ordinal)))
        {
            Assert.Equal(before[key], Read(accessor, key.Replace('\\', '/')));
        }
    }

    [Fact]
    public async Task PackagedSkill_AssetContentMissingButSidecarPresent_IsNotDemoted()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_grI", "get-us-weather", skillId, $"<!-- bic:bundle={Bot}.file.getusweatherzip_sDWAj -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_50nzF", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.getusweatherps1_PYHUJ", "./scripts/Get-UsWeather.ps1", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest, script });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather_grI\n  bundle: {Bot}.file.getusweatherzip_sDWAj\n  manifestSchemaName: {Bot}.file.skillmd_50nzF\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/scripts/Get-UsWeather.ps1.mcs.yml", $"mcs.metadata:\n  componentName: ./scripts/Get-UsWeather.ps1\n  schemaName: {Bot}.file.getusweatherps1_PYHUJ\nkind: FileAttachmentComponentMetadata\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.DoesNotContain(changes, change => change.ChangeType == ChangeType.Delete);
        Assert.Contains(read.Components.OfType<FileAttachmentComponent>(), component => component.SchemaNameString == $"{Bot}.file.skillmd_50nzF");
    }

    [Fact]
    public async Task PackagedSkillWithoutManifestFile_DoesNotSynthesizeManifestComponent()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.hardware_9Ut", "hardware", skillId, "Inline instructions.\n");
        var doc = CreateAsset($"{Bot}.file.docFile1.txt", "doc/File 1.txt", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, doc });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/hardware/skill.mcs.yml", $"mcs.metadata:\n  componentName: hardware\n  schemaName: {Bot}.skill.hardware_9Ut\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/hardware/doc/File 1.txt", "doc content\n");
        Write(accessor, "behaviors/hardware/doc/File 1.txt.mcs.yml", $"mcs.metadata:\n  componentName: doc/File 1.txt\n  schemaName: {Bot}.file.docFile1.txt\nkind: FileAttachmentComponentMetadata\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.DoesNotContain(read.Components.OfType<FileAttachmentComponent>(), component => SkillLayout.IsManifest(component.DisplayName));
        Assert.DoesNotContain(changes, change => change.ChangeType == ChangeType.Create && change.ChangeKind == BotElementKind.FileAttachmentComponent.ToString());
    }

    [Fact]
    public async Task SkillWithAssetButNoBundle_DoesNotInventBundleMarker()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skill = CreateSkill($"{Bot}.skill.hardware_9Ut", "hardware", Guid.NewGuid(), null, "Skill description");
        var doc = CreateAsset($"{Bot}.file.docFile1.txt", "doc/File 1.txt", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, doc });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/hardware/skill.mcs.yml", $"mcs.metadata:\n  componentName: hardware\n  description: Skill description\n  schemaName: {Bot}.skill.hardware_9Ut\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/hardware/doc/File 1.txt", "doc content\n");
        Write(accessor, "behaviors/hardware/doc/File 1.txt.mcs.yml", $"mcs.metadata:\n  componentName: doc/File 1.txt\n  schemaName: {Bot}.file.docFile1.txt\nkind: FileAttachmentComponentMetadata\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        var readSkill = read.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.Null(((InlineAgentSkill)readSkill.Dialog!).Content);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task SkillRenamedInCloud_StickyFolder_ProducesNoChangesAndNoDuplicate()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_grI", "renamed-weather", skillId, $"<!-- bic:bundle={Bot}.file.getusweatherzip -->", "Skill description");
        var manifest = CreateAsset($"{Bot}.file.skillmd_50nzF", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.scriptsrunps1_a1B", "./scripts/run.ps1", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest, script });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);

        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: renamed-weather\n  description: Skill description\n  schemaName: {Bot}.skill.get-us-weather_grI\n  bundle: {Bot}.file.getusweatherzip\n  manifestSchemaName: {Bot}.file.skillmd_50nzF\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");
        Write(accessor, "behaviors/get-us-weather/scripts/run.ps1.mcs.yml", $"mcs.metadata:\n  componentName: ./scripts/run.ps1\n  schemaName: {Bot}.file.scriptsrunps1_a1B\nkind: FileAttachmentComponentMetadata\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.Single(read.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill));
        Assert.Empty(changes);
    }

    [Fact]
    public async Task ExistingSkillWithManifestAndAssetButNoBundle_IsPromotedOnNextPush()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), null);
        var script = CreateAsset($"{Bot}.file.script_a1B", "scripts/Get-UsWeather.ps1", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, script });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/scripts/Get-UsWeather.ps1", "script\n");
        Write(accessor, "behaviors/get-us-weather/scripts/Get-UsWeather.ps1.mcs.yml", $"mcs.metadata:\n  componentName: scripts/Get-UsWeather.ps1\n  schemaName: {Bot}.file.script_a1B\nkind: FileAttachmentComponentMetadata\n");

        Assert.True(SkillLayout.HasAssets(accessor, "get-us-weather"));
        Assert.True(SkillLayout.HasManifestFile(accessor, "get-us-weather"));

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        var promotedSkill = read.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.StartsWith(SkillLayout.BundleMarkerPrefix, ((InlineAgentSkill)promotedSkill.Dialog!).Content, StringComparison.Ordinal);
        Assert.Contains(read.Components.OfType<FileAttachmentComponent>(), component => SkillLayout.IsManifest(component.DisplayName));
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Update && change.SchemaName == skill.SchemaNameString);
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.ChangeKind == BotElementKind.FileAttachmentComponent.ToString());
    }

    [Fact]
    public async Task SkillWithManifestComponentButNoBundle_ProducesNoChanges()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_grI", "get-us-weather-5", skillId, null, "Skill description");
        var manifest = CreateAsset($"{Bot}.file.skillmd_50nzF", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.getusweatherps1_PYHUJ", "scripts/Get-UsWeather.ps1", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, manifest, script });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);

        Write(accessor, "behaviors/get-us-weather-5/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather-5\n  description: Skill description\n  schemaName: {Bot}.skill.get-us-weather_grI\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather-5/SKILL.md", "# Weather\nInstructions.\n");
        Write(accessor, "behaviors/get-us-weather-5/scripts/Get-UsWeather.ps1", "script\n");
        Write(accessor, "behaviors/get-us-weather-5/scripts/Get-UsWeather.ps1.mcs.yml", $"mcs.metadata:\n  componentName: scripts/Get-UsWeather.ps1\n  schemaName: {Bot}.file.getusweatherps1_PYHUJ\nkind: FileAttachmentComponentMetadata\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        var readSkill = read.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.Null(((InlineAgentSkill)readSkill.Dialog!).Content);
        Assert.Contains(read.Components.OfType<FileAttachmentComponent>(), component => component.SchemaNameString == manifest.SchemaNameString);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task SkillFolderDeletedLocally_EmitsDeleteForSkillAndAssets()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var skillId = Guid.NewGuid();
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_grI", "get-us-weather-6", skillId, null, "Skill description");
        var script = CreateAsset($"{Bot}.file.getusweatherps1_PYHUJ", "scripts/Get-UsWeather.ps1", skill.Id);
        var cloud = CloudDefinition().WithComponents(new BotComponentBase[] { skill, script });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(sync, workspace, read);

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Delete && change.SchemaName == skill.SchemaNameString);
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Delete && change.SchemaName == script.SchemaNameString);
    }

    [Fact]
    public async Task SkillDeletedInCloud_PullRemovesWholeFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-cloud-delete-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var skill = CreateSkill($"{Bot}.skill.my-skill-1_eWv", "my-skill-1", Guid.NewGuid(), "Instructions.\n", "Skill description");
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { skill });

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/my-skill-1/skill.mcs.yml", $"mcs.metadata:\n  componentName: my-skill-1\n  description: Skill description\n  schemaName: {Bot}.skill.my-skill-1_eWv\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/my-skill-1/SKILL.md", "Instructions.\n");

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentDelete(skill.Id, skill.Version) }, entity, "token-2"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());

        var pulledDefinition = await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), cloud, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.DoesNotContain(accessor.Files.Keys, key => key.Replace('\\', '/').StartsWith("behaviors/my-skill-1/", StringComparison.Ordinal));

        var (_, immediateChanges) = await GetChangesAsync(synchronizer, workspace, pulledDefinition);
        Assert.Empty(immediateChanges);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(synchronizer, workspace, read);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task SkillAddedInCloud_PullDownloadsNewManifestAndAssetPayloads()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-cloud-add-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var previous = new BotDefinition().WithEntity(entity);
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_a1B", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.script_a1B", "./scripts/Get-UsWeather.ps1", skill.Id);

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, previous);

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(manifest), new BotComponentInsert(script) }, entity, "token-2"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());
        mockDataverse.Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, BotComponentId, string, CancellationToken>((_, _, fileName, cancellationToken) =>
                accessor.WriteAsync(SkillLayout.GetAssetPath("get-us-weather", fileName), $"payload:{fileName}", cancellationToken));

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previous, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.True(accessor.Exists(new AgentFilePath("behaviors/get-us-weather/SKILL.md")));
        Assert.True(accessor.Exists(new AgentFilePath("behaviors/get-us-weather/scripts/Get-UsWeather.ps1")));
        Assert.True(accessor.Exists(new AgentFilePath("behaviors/get-us-weather/scripts/Get-UsWeather.ps1.mcs.yml")));
        Assert.False(accessor.Exists(new AgentFilePath("behaviors/get-us-weather/SKILL.md.mcs.yml")));
    }

    [Fact]
    public async Task SkillAddedInCloud_MissingAssetPayload_PullStillCommitsCloudCacheAndToken()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-cloud-add-missing-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var previous = new BotDefinition().WithEntity(entity);
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_a1B", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.script_a1B", "./scripts/Get-UsWeather.ps1", skill.Id);

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, previous);

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(manifest), new BotComponentInsert(script) }, entity, "token-2"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());
        mockDataverse.Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, BotComponentId, string, CancellationToken>((_, _, fileName, cancellationToken) =>
                fileName.EndsWith("Get-UsWeather.ps1", StringComparison.Ordinal)
                    ? throw new DataverseRequestException(System.Net.HttpStatusCode.NotFound, "{\"error\":{\"message\":\"No file attachment found for attribute: filedata\"}}")
                    : accessor.WriteAsync(SkillLayout.GetAssetPath("get-us-weather", fileName), $"payload:{fileName}", cancellationToken));

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previous, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.True(accessor.Exists(new AgentFilePath("behaviors/get-us-weather/SKILL.md")));
        Assert.False(accessor.Exists(new AgentFilePath("behaviors/get-us-weather/scripts/Get-UsWeather.ps1")));
        Assert.True(accessor.Exists(new AgentFilePath("behaviors/get-us-weather/scripts/Get-UsWeather.ps1.mcs.yml")));

        var committedCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        Assert.Contains(committedCache.Components, component => component.SchemaNameString == script.SchemaNameString);
        Assert.Equal("token-2", Read(accessor, ".mcs/changetoken.txt"));
    }

    [Fact]
    public async Task PullFailsDownloadingPayload_LeavesCloudCacheAndTokenUnadvanced()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-cloud-add-failed-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var previous = new BotDefinition().WithEntity(entity);
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_a1B", "./SKILL.md", skill.Id);

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, previous);

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(manifest) }, entity, "token-2"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());
        mockDataverse.Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"transient failure\"}}"));

        await Assert.ThrowsAsync<DataverseRequestException>(() => synchronizer.PullExistingChangesAsync(
            workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previous, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None));

        // The delta was not consumed, so the next pull replays it instead of the workspace being
        // permanently short of a component the cache already claims to hold.
        var cache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        Assert.DoesNotContain(cache.Components, component => component.SchemaNameString == skill.SchemaNameString);
        Assert.False(accessor.Exists(new AgentFilePath(".mcs/changetoken.txt")));
    }

    // ---- SyncStorageMode.InMemory coverage -------------------------------------------------
    //
    // The rest of this suite builds on ComponentWriterDefensiveTests.CreateSyncInfrastructure,
    // whose accessor factory reports IsMemoryBacked = false - so it pins the physical-storage
    // contract. A memory-backed host has no disk to stage payloads on: transfers must go over
    // IStreamingKnowledgeFileClient, and WorkspaceSynchronizer refuses the non-streaming path
    // outright. These tests pin the skill paths against that contract using the product
    // McsCore.InMemoryFileAccessorFactory (IsMemoryBacked = true).

    private static (WorkspaceSynchronizer Sync, McsCore.InMemoryFileAccessorFactory Factory, Mock<IIslandControlPlaneService> Island) CreateMemoryBackedInfrastructure()
    {
        var factory = new McsCore.InMemoryFileAccessorFactory();
        var island = new Mock<IIslandControlPlaneService>();
        var synchronizer = new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            factory,
            island.Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());
        return (synchronizer, factory, island);
    }

    private static void WriteTo(IFileAccessor accessor, string path, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ReadFrom(IFileAccessor accessor, string path)
    {
        using var stream = accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IFileAccessor CreateMemoryBackedWorkspace(McsCore.InMemoryFileAccessorFactory factory, DirectoryPath workspace, DefinitionBase cloudCache)
    {
        var accessor = factory.Create(workspace);
        WriteTo(accessor, AgentClassifier.WorkspaceLayoutMarkerFileName, "layoutVersion: 1\n");
        WriteTo(accessor, "settings.mcs.yml", CliSettings);
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloudCache);
        return accessor;
    }

    private static Mock<ISyncDataverseClient> CreateDataverseMock()
    {
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());
        return mockDataverse;
    }

    [Fact]
    public async Task InMemory_SkillAddedInCloud_PullStreamsPayloadsIntoNestedSkillFolder()
    {
        var (synchronizer, factory, mockIsland) = CreateMemoryBackedInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-inmem-pull-{Guid.NewGuid():N}/");
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var previous = new BotDefinition().WithEntity(entity);
        var accessor = CreateMemoryBackedWorkspace(factory, workspace, previous);

        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_a1B", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.script_a1B", "./scripts/Get-UsWeather.ps1", skill.Id);

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(manifest), new BotComponentInsert(script) }, entity, "token-2"));

        var mockDataverse = CreateDataverseMock();
        mockDataverse.As<IStreamingKnowledgeFileClient>()
            .Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, BotComponentId, CancellationToken>(async (destination, componentId, cancellationToken) =>
            {
                var payload = Encoding.UTF8.GetBytes($"payload:{componentId.Value:N}");
                await destination.WriteAsync(payload, 0, payload.Length, cancellationToken);
            });

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previous, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        // Streamed straight into the memory-backed workspace, promoted to the nested skill paths.
        Assert.Equal($"payload:{manifest.Id.Value:N}", ReadFrom(accessor, "behaviors/get-us-weather/SKILL.md"));
        Assert.Equal($"payload:{script.Id.Value:N}", ReadFrom(accessor, "behaviors/get-us-weather/scripts/Get-UsWeather.ps1"));
        Assert.True(accessor.Exists(new AgentFilePath("behaviors/get-us-weather/scripts/Get-UsWeather.ps1.mcs.yml")));

        // The non-streaming (disk-staging) overload must never be reached in memory mode, and no
        // staging artifact may survive in the workspace.
        mockDataverse.Verify(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain(accessor.ListFiles("behaviors", "*.*"), path => path.ToString().Contains(".download.tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InMemory_PulledSkill_ReReadsWithNoSpuriousChanges()
    {
        var (synchronizer, factory, mockIsland) = CreateMemoryBackedInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-inmem-roundtrip-{Guid.NewGuid():N}/");
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var previous = new BotDefinition().WithEntity(entity);
        CreateMemoryBackedWorkspace(factory, workspace, previous);

        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_a1B", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.script_a1B", "./scripts/Get-UsWeather.ps1", skill.Id);

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(manifest), new BotComponentInsert(script) }, entity, "token-2"));

        var mockDataverse = CreateDataverseMock();
        mockDataverse.As<IStreamingKnowledgeFileClient>()
            .Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, BotComponentId, CancellationToken>(async (destination, componentId, cancellationToken) =>
            {
                var payload = Encoding.UTF8.GetBytes($"payload:{componentId.Value:N}");
                await destination.WriteAsync(payload, 0, payload.Length, cancellationToken);
            });

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previous, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(synchronizer, workspace, read);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task InMemory_SkillAssetPayloads_AreUploadedThroughStreamingClient()
    {
        var (synchronizer, factory, _) = CreateMemoryBackedInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-inmem-push-{Guid.NewGuid():N}/");
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_a1B", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.script_a1B", "./scripts/Get-UsWeather.ps1", skill.Id);
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { skill, manifest, script });

        var accessor = CreateMemoryBackedWorkspace(factory, workspace, cloud);
        WriteTo(accessor, "behaviors/get-us-weather/SKILL.md", "manifest bytes");
        WriteTo(accessor, "behaviors/get-us-weather/scripts/Get-UsWeather.ps1", "script bytes");

        var uploaded = new System.Collections.Concurrent.ConcurrentBag<string>();
        var mockDataverse = CreateDataverseMock();
        mockDataverse.As<IStreamingKnowledgeFileClient>()
            .Setup(x => x.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, Guid, string, CancellationToken>(async (content, _, fileName, _) =>
            {
                using var reader = new StreamReader(content);
                uploaded.Add($"{fileName}|{await reader.ReadToEndAsync()}");
            });

        await synchronizer.UploadKnowledgeFilesAsync(workspace, mockDataverse.Object, CancellationToken.None);

        // Both payloads were read out of the memory-backed workspace and streamed up verbatim.
        Assert.Contains("./SKILL.md|manifest bytes", uploaded);
        Assert.Contains("./scripts/Get-UsWeather.ps1|script bytes", uploaded);
        mockDataverse.Verify(x => x.UploadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InMemory_NonStreamingClient_IsRefusedForSkillPayloads()
    {
        var (synchronizer, factory, _) = CreateMemoryBackedInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-inmem-guard-{Guid.NewGuid():N}/");
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var skill = CreateSkill($"{Bot}.skill.get-us-weather", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_a1B", "./SKILL.md", skill.Id);
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { skill, manifest });

        CreateMemoryBackedWorkspace(factory, workspace, cloud);

        // A plain ISyncDataverseClient mock does not implement IStreamingKnowledgeFileClient, so the
        // only remaining route is disk staging - which a memory-backed workspace must reject rather
        // than silently spilling agent content onto the host.
        var mockDataverse = CreateDataverseMock();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.DownloadKnowledgeFilesAsync(
            workspace, mockDataverse.Object, new[] { manifest.SchemaNameString! }, CancellationToken.None));

        Assert.Contains("IStreamingKnowledgeFileClient", error.Message, StringComparison.Ordinal);
        mockDataverse.Verify(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InMemory_NewLocalSkillWithAsset_IsPromotedToBundledCloudShape()
    {
        var (synchronizer, factory, _) = CreateMemoryBackedInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-inmem-promote-{Guid.NewGuid():N}/");
        var accessor = CreateMemoryBackedWorkspace(factory, workspace, CloudDefinition());

        WriteTo(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\nkind: InlineAgentSkill\n");
        WriteTo(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        WriteTo(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");

        var (_, changes) = await GetChangesAsync(synchronizer, workspace, CloudDefinition());

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == $"{Bot}.skill.get-us-weather");
        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var skill = read.Components.OfType<DialogComponent>().Single(component => component.Dialog is InlineAgentSkill);
        Assert.StartsWith(SkillLayout.BundleMarkerPrefix, ((InlineAgentSkill)skill.Dialog!).Content, StringComparison.Ordinal);
        Assert.Single(read.Components.OfType<FileAttachmentComponent>(), component => SkillLayout.IsManifest(component.DisplayName));
        Assert.Single(read.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "scripts/run.ps1");
    }

    [Fact]
    public async Task InMemory_LegacyFlatWorkspace_MigratesToNestedLayoutOnPull()
    {
        var (synchronizer, factory, mockIsland) = CreateMemoryBackedInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-inmem-migrate-{Guid.NewGuid():N}/");
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var skill = CreateSkill($"{Bot}.skill.get-us-weather_x9Z", "get-us-weather", Guid.NewGuid(), $"<!-- bic:bundle={Bot}.file.getusweatherzip -->");
        var manifest = CreateAsset($"{Bot}.file.skillmd_dWN", "./SKILL.md", skill.Id);
        var script = CreateAsset($"{Bot}.file.scriptsrunps1_9GR", "./scripts/run.ps1", skill.Id);
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { skill, manifest, script });

        var accessor = CreateMemoryBackedWorkspace(factory, workspace, cloud);
        WriteTo(accessor, "behaviors/get-us-weather.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: <!-- bic:bundle={Bot}.file.getusweatherzip -->\n");
        WriteTo(accessor, "behaviors/get-us-weather/.skill.json", $"{{ \"schemaName\": \"{Bot}.skill.get-us-weather_x9Z\", \"folderName\": \"get-us-weather\" }}");
        WriteTo(accessor, "behaviors/get-us-weather/skillmd_dWN.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n");
        WriteTo(accessor, "behaviors/get-us-weather/scriptsrunps1_9GR.mcs.yml", "mcs.metadata:\n  componentName: ./scripts/run.ps1\n");
        WriteTo(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        WriteTo(accessor, "behaviors/get-us-weather/scripts/run.ps1", "script\n");

        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), entity, "token-2"));

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), cloud, CreateDataverseMock().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var keys = accessor.ListFiles("behaviors", "*.*").Select(path => path.ToString().Replace('\\', '/')).ToList();
        Assert.Contains("behaviors/get-us-weather/skill.mcs.yml", keys);
        Assert.DoesNotContain("behaviors/get-us-weather.mcs.yml", keys);
        Assert.DoesNotContain("behaviors/get-us-weather/.skill.json", keys);
        Assert.Contains("behaviors/get-us-weather/scripts/run.ps1.mcs.yml", keys);

        var anchor = SkillLayout.ReadAnchorMetadata(accessor, "get-us-weather");
        Assert.Equal($"{Bot}.skill.get-us-weather_x9Z", anchor.SchemaName);
        Assert.Equal($"{Bot}.file.skillmd_dWN", anchor.ManifestSchemaName);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (_, changes) = await GetChangesAsync(synchronizer, workspace, read);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task Pull_WithLocalOnlySkillPresent_CommittedCloudCacheContainsOnlyCloudState()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-cache-purity-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!;
        var cloudSkill = CreateSkill($"{Bot}.skill.cloud-skill", "cloud-skill", Guid.NewGuid(), "Cloud instructions.\n", "Cloud skill");
        var cloud = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { cloudSkill });

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloud);
        Write(accessor, "behaviors/cloud-skill/skill.mcs.yml", $"mcs.metadata:\n  componentName: cloud-skill\n  description: Cloud skill\n  schemaName: {Bot}.skill.cloud-skill\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/cloud-skill/SKILL.md", "Cloud instructions.\n");

        // Authored locally and never pushed: it must stay out of the cloud cache.
        Write(accessor, "behaviors/local-only-skill/skill.mcs.yml", "mcs.metadata:\n  componentName: local-only-skill\n  description: Local only\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/local-only-skill/SKILL.md", "Local instructions.\n");

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        Assert.Contains(localDefinition.Components, component => component.DisplayName == "local-only-skill");

        var updatedCloudSkill = CreateSkill($"{Bot}.skill.cloud-skill", "cloud-skill", cloudSkill.Id.Value, "Updated cloud instructions.\n", "Cloud skill");
        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentUpdate(updatedCloudSkill) }, entity, "token-2"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), localDefinition, mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var committedCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        Assert.DoesNotContain(committedCache.Components, component => component.DisplayName == "local-only-skill");
        Assert.Single(committedCache.Components.OfType<DialogComponent>(), component => component.SchemaNameString == $"{Bot}.skill.cloud-skill");
        Assert.True(accessor.Exists(new AgentFilePath("behaviors/local-only-skill/skill.mcs.yml")));
    }

    [Fact]
    public async Task NewAssetWhoseNameIsPrefixOfAnother_DoesNotStealItsSchema()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {Bot}.skill.get-us-weather\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");
        Write(accessor, "behaviors/get-us-weather/foo_bar", "bar\n");
        Write(accessor, "behaviors/get-us-weather/foo_bar.mcs.yml", $"mcs.metadata:\n  componentName: foo_bar\n  schemaName: {Bot}.file.foo_bar_a1B\nkind: FileAttachmentComponentMetadata\n");
        Write(accessor, "behaviors/get-us-weather/foo", "foo\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var foo = Assert.Single(read.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "foo");
        var fooBar = Assert.Single(read.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "foo_bar");
        Assert.Equal($"{Bot}.file.foo_bar_a1B", fooBar.SchemaNameString);
        Assert.NotEqual(fooBar.SchemaNameString, foo.SchemaNameString);
    }

    [Fact]
    public async Task MissingAnchorMetadata_DegradesGracefully()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather/skill.mcs.yml", "not: valid: yaml: at: all\n");
        Write(accessor, "behaviors/get-us-weather/SKILL.md", "Instructions.\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Null(await Record.ExceptionAsync(() => GetChangesAsync(sync, workspace, read)));
    }
}
