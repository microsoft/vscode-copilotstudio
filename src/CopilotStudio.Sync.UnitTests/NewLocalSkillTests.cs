// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class NewLocalSkillTests
{
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
        var workspace = new DirectoryPath($"c:/test/new-local-skill-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);
        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        return (synchronizer, accessor, workspace);
    }

    private static void Write(InMemoryFileAccessor accessor, string path, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static DialogComponent SkillOf(DefinitionBase definition) =>
        definition.Components.OfType<DialogComponent>().Single(c => c.Dialog is InlineAgentSkill);

    private static BotDefinition CliCloudDefinition() =>
        new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr123_natest\ntemplate: cliagent-1.0.0\n")!);

    private static DialogComponent CreateCloudSkill(string schemaName, string displayName, Guid? id = null) => new DialogComponent(
        schemaName: schemaName,
        displayName: displayName,
        description: string.Empty,
        id: id ?? Guid.NewGuid(),
        parentBotComponentId: default,
        dialog: new InlineAgentSkill.Builder { Content = "placeholder" }.Build());

    [Fact]
    public async Task GetLocalChanges_BareSkillFolder_SynthesizesInlineAgentSkill()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\nname: whatever\ndescription: Get the temperature.\n---\nBody\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = SkillOf(read);
        Assert.Equal("get-us-temperature", skill.DisplayName);
        Assert.Equal(string.Empty, skill.Description);
        Assert.NotEqual(Guid.Empty, skill.Id.Value);
        Assert.Equal("cr123_natest.skill.get-us-temperature", skill.SchemaNameString);
    }

    [Fact]
    public async Task GetLocalChanges_BareSkillFolder_SchemaIsDeterministic()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");

        var first = SkillOf(await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));
        var second = SkillOf(await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        Assert.Equal(first.SchemaNameString, second.SchemaNameString);
    }

    [Fact]
    public async Task GetLocalChanges_TwoBareSkillsBothHaveSkillMd_PayloadSchemasAreDeterministicAcrossReads()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "behaviors/get-us-weather-2/SKILL.md", "---\ndescription: d\n---\nBody\n");

        var first = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var second = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var firstSchemas = first.Components.OfType<FileAttachmentComponent>().Select(file => file.SchemaNameString).OrderBy(name => name).ToList();
        var secondSchemas = second.Components.OfType<FileAttachmentComponent>().Select(file => file.SchemaNameString).OrderBy(name => name).ToList();
        Assert.Equal(firstSchemas, secondSchemas);
    }

    [Fact]
    public async Task GetLocalChanges_BareSkillWithNestedScriptFolder_PayloadSchemaIsDeterministicAcrossReads()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather-2/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1", "Write-Host hi\n");
        Write(accessor, "behaviors/get-us-weather-2/scriptsGet-UsWeather.ps1_xBn.mcs.yml", "mcs.metadata:\n  componentName: scripts/Get-UsWeather.ps1\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var script = read.Components.OfType<FileAttachmentComponent>().Single(file => file.DisplayName == "scripts/Get-UsWeather.ps1");
        Assert.Equal("cr123_natest.file.scriptsGet-UsWeather.ps1_xBn", script.SchemaNameString);
    }

    [Fact]
    public async Task GetLocalChanges_BareSkill_EveryFileBecomesFileAttachment_ParentedToSkill()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "behaviors/get-us-temperature/text.md", "extra\n");
        Write(accessor, "behaviors/get-us-temperature/docs/instruction.txt", "instructions\n");
        Write(accessor, "behaviors/get-us-temperature/scripts/Get-UsTemperature.ps1", "Write-Host hi\n");
        Write(accessor, "behaviors/get-us-temperature/scripts/Resolve-UsTemperatureLocation.ps1", "Write-Host loc\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = SkillOf(read);
        var files = read.Components.OfType<FileAttachmentComponent>().ToList();
        Assert.Equal(5, files.Count);
        Assert.All(files, file =>
        {
            Assert.True(file.ParentBotComponentId.HasValue);
            Assert.Equal(skill.Id, file.ParentBotComponentId!.Value);
        });
        Assert.Contains(files, file => file.DisplayName == "SKILL.md");
        Assert.Contains(files, file => file.DisplayName == "text.md");
        Assert.Contains(files, file => file.DisplayName == "docs/instruction.txt");
        Assert.Contains(files, file => file.DisplayName == "scripts/Get-UsTemperature.ps1");
        Assert.Contains(files, file => file.DisplayName == "scripts/Resolve-UsTemperatureLocation.ps1");
    }

    [Fact]
    public async Task GetLocalChanges_BareSkill_PayloadFilesNotEmittedAsRootKnowledge()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "behaviors/get-us-temperature/scripts/Get-UsTemperature.ps1", "Write-Host hi\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.All(read.Components.OfType<FileAttachmentComponent>(), file => Assert.True(file.ParentBotComponentId.HasValue));
    }

    [Fact]
    public async Task GetLocalChanges_FolderWithoutSkillMd_NotSynthesized()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/not-a-skill/readme.txt", "hello\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.DoesNotContain(read.Components.OfType<DialogComponent>(), component => component.Dialog is InlineAgentSkill);
    }

    [Fact]
    public async Task GetLocalChanges_FolderWithExistingSkillMcsYml_NotDoubleSynthesized()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-temperature.mcs.yml", "mcs.metadata:\n  componentName: get-us-temperature\nkind: InlineAgentSkill\ncontent: placeholder\n");
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Single(read.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill));
    }

    [Fact]
    public async Task GetLocalChanges_SubAgentBehaviorsSkillFolder_Ignored()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "agents/MyAgent/behaviors/inner/SKILL.md", "---\ndescription: d\n---\nBody\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.DoesNotContain(read.Components.OfType<DialogComponent>(), component => component.Dialog is InlineAgentSkill);
    }

    [Fact]
    public async Task GetLocalChanges_NestedSkillMd_Ignored()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-temperature/docs/SKILL.md", "---\ndescription: d\n---\nBody\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.DoesNotContain(read.Components.OfType<DialogComponent>(), component => component.Dialog is InlineAgentSkill);
    }

    [Fact]
    public async Task GetLocalChanges_CopiedProjectedSkill_PayloadFilesParentedToSkill()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather-2.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather-2\nkind: InlineAgentSkill\ncontent: placeholder\n");
        Write(accessor, "behaviors/get-us-weather-2/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1", "Write-Host hi\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = SkillOf(read);
        Assert.NotEqual(Guid.Empty, skill.Id.Value);
        var files = read.Components.OfType<FileAttachmentComponent>().ToList();
        Assert.NotEmpty(files);
        Assert.All(files, file =>
        {
            Assert.True(file.ParentBotComponentId.HasValue);
            Assert.Equal(skill.Id, file.ParentBotComponentId!.Value);
        });
    }

    [Fact]
    public async Task GetLocalChanges_RealKnowledgeFile_StaysParentlessKnowledge()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "capabilities/knowledge/files/doc.txt", "knowledge content\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var file = Assert.Single(read.Components.OfType<FileAttachmentComponent>());
        Assert.False(file.ParentBotComponentId.HasValue);
        Assert.DoesNotContain(read.Components.OfType<DialogComponent>(), component => component.Dialog is InlineAgentSkill);
    }

    [Fact]
    public async Task GetLocalChanges_SkillFilesAndRealKnowledge_Coexist()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "capabilities/knowledge/files/doc.txt", "knowledge content\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = SkillOf(read);
        var knowledge = Assert.Single(read.Components.OfType<FileAttachmentComponent>().Where(file => file.DisplayName == "doc.txt"));
        Assert.False(knowledge.ParentBotComponentId.HasValue);
        Assert.All(read.Components.OfType<FileAttachmentComponent>().Where(file => file.DisplayName != "doc.txt"), file => Assert.Equal(skill.Id, file.ParentBotComponentId!.Value));
    }

    [Fact]
    public async Task GetLocalChangesAsync_BareSkill_ShownInPreview()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CliCloudDefinition());
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");

        var (_, changes) = await sync.GetLocalChangesAsync(workspace, CliCloudDefinition(), new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == "cr123_natest.skill.get-us-temperature");
    }

    [Fact]
    public async Task GetLocalChangesAsync_NewPackagedSkill_PayloadFilesSurfaceAsCreate()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CliCloudDefinition());

        // A brand-new packaged skill (anchor + payload sidecars + manifest), none of which
        // exist in the cloud yet. Modeled on a real `ms add skill` clone (redlining-content).
        Write(accessor, "behaviors/redlining-content/skill.mcs.yml",
            "mcs.metadata:\n" +
            "  componentName: redlining-content\n" +
            "  description: Redline skill.\n" +
            "  schemaName: cr123_natest.skill.redlining-content\n" +
            "  bundle: cr123_natest.file.redliningcontentzip\n" +
            "  manifestSchemaName: cr123_natest.file.skillmd\n" +
            "kind: InlineAgentSkill\n" +
            "authoringSource: Upload\n");
        Write(accessor, "behaviors/redlining-content/SKILL.md", "---\nname: redlining-content\ndescription: Redline skill.\n---\n# Body\n");
        Write(accessor, "behaviors/redlining-content/assets/template.docx", "docx-bytes\n");
        Write(accessor, "behaviors/redlining-content/assets/template.docx.mcs.yml",
            "mcs.metadata:\n  componentName: ./assets/template.docx\n  schemaName: cr123_natest.file.templatedocx\n");
        Write(accessor, "behaviors/redlining-content/scripts/redline.py", "print('x')\n");
        Write(accessor, "behaviors/redlining-content/scripts/redline.py.mcs.yml",
            "mcs.metadata:\n  componentName: ./scripts/redline.py\n  schemaName: cr123_natest.file.redlinepy\n");

        var workspaceDef = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var (changeSet, changes) = await sync.GetLocalChangesAsync(workspace, workspaceDef, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        // The skill anchor and every payload file the parser found must surface as new local
        // changes (like new knowledge files) even though the parent skill is not yet in the cloud.
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == "cr123_natest.skill.redlining-content");
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == "cr123_natest.file.templatedocx");
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == "cr123_natest.file.redlinepy");

        // The surfaced payload files are display-only: they must NOT be in the returned changeset,
        // because push-capable callers send it in a single SaveChangesAsync that cannot resolve the
        // not-yet-created (fabricated) parent skill id. The skill anchor itself is a real insert.
        var insertedSchemaNames = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Select(insert => insert.Component!.SchemaNameString).ToList();
        Assert.Contains("cr123_natest.skill.redlining-content", insertedSchemaNames);
        Assert.DoesNotContain("cr123_natest.file.templatedocx", insertedSchemaNames);
        Assert.DoesNotContain("cr123_natest.file.redlinepy", insertedSchemaNames);
    }

    [Fact]
    public async Task GetLocalChanges_DefaultOverload_NewSkillPayloads_EmitInserts()
    {
        // Regression: the default GetLocalChanges overload (deferMissingParents == false,
        // surfaceFileChildrenOfNewParents == false) must still emit a BotComponentInsert for every
        // payload file of a new skill that is not yet in the cloud. Only the preview overload
        // (surfaceFileChildrenOfNewParents == true) suppresses those inserts for display.
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/get-us-weather-2.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather-2\nkind: InlineAgentSkill\ncontent: placeholder\n");
        Write(accessor, "behaviors/get-us-weather-2/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1", "Write-Host hi\n");

        var localDefinition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var fileComponents = localDefinition.Components.OfType<FileAttachmentComponent>().ToList();
        Assert.NotEmpty(fileComponents);

        // Default overload: deferMissingParents == false, surfaceFileChildrenOfNewParents == false.
        // The skill is absent from the cloud, so its payload files hit the missing-parent branch.
        var (changeSet, _) = sync.GetLocalChanges(localDefinition, CliCloudDefinition(), accessor, "token-1");

        var fileInserts = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Where(insert => insert.Component is FileAttachmentComponent).ToList();
        Assert.Equal(fileComponents.Count, fileInserts.Count);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_BareSkill_CloudCacheNotModified()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CliCloudDefinition());
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");
        var cacheKey = accessor.Files.Keys.First(key => key.Replace('\\', '/').EndsWith("botdefinition.json", StringComparison.Ordinal));
        var before = accessor.Files[cacheKey];

        await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Equal(before, accessor.Files[cacheKey]);
    }

    [Fact]
    public async Task GetLocalChangesAsync_BareSkillAlreadyInCloud_FilesParentedToCloudSkill_NoDuplicateSkill()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var cloudSkillId = Guid.NewGuid();
        var cloudSkill = CreateCloudSkill("cr123_natest.skill.get-us-temperature", "get-us-temperature", cloudSkillId);
        var cloudDefinition = CliCloudDefinition().WithComponents(new BotComponentBase[] { cloudSkill });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloudDefinition);
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "behaviors/get-us-temperature/scripts/Get-UsTemperature.ps1", "Write-Host hi\n");

        var (changeSet, changes) = await sync.GetLocalChangesAsync(workspace, cloudDefinition, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.DoesNotContain(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == "cr123_natest.skill.get-us-temperature");
        var fileInserts = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Where(insert => insert.Component is FileAttachmentComponent).ToList();
        Assert.NotEmpty(fileInserts);
        Assert.All(fileInserts, insert => Assert.Equal(cloudSkillId, insert.Component!.ParentBotComponentId!.Value));
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_BareSkillMatchesSuffixedCloudSkillWithoutLink_KeepsCloudIdentity()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var cloudSkill = CreateCloudSkill("cr123_natest.skill.get-us-temperature_peu", "get-us-temperature");
        var cloudDefinition = CliCloudDefinition().WithComponents(new BotComponentBase[] { cloudSkill });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloudDefinition);
        Write(accessor, "behaviors/get-us-temperature/SKILL.md", "skill body\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = SkillOf(read);
        Assert.Equal(cloudSkill.SchemaNameString, skill.SchemaNameString);
        Assert.Equal(cloudSkill.Id, skill.Id);
    }

    [Fact]
    public async Task GetLocalChangesAsync_BareSkill_SynthesizedInPreview()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CliCloudDefinition());
        Write(accessor, "behaviors/get-us-weather-2/SKILL.md", "---\ndescription: d\n---\nBody\n");
        Write(accessor, "behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1", "Write-Host hi\n");

        var (_, changes) = await sync.GetLocalChangesAsync(workspace, CliCloudDefinition(), new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == "cr123_natest.skill.get-us-weather-2");
        Assert.DoesNotContain(changes, change => change.Uri.Replace('\\', '/').StartsWith("capabilities/knowledge/files/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetLocalChangesAsync_BareSkillFolderWithPunctuation_AnchorTargetsOriginalFolder()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CliCloudDefinition());
        Write(accessor, "behaviors/weather.v2/SKILL.md", "---\ndescription: d\n---\nBody\n");

        var (_, changes) = await sync.GetLocalChangesAsync(workspace, CliCloudDefinition(), new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.Uri.Replace('\\', '/') == "behaviors/weather.v2/skill.mcs.yml");
        Assert.DoesNotContain(changes, change => change.Uri.Replace('\\', '/').StartsWith("behaviors/weatherv2", StringComparison.Ordinal));
    }

    private static FileAttachmentComponent ParentlessFileAttachment(string schemaName, string displayName)
    {
        var builder = new FileAttachmentComponent().WithSchemaName(schemaName).WithDisplayName(displayName).ToBuilder();
        builder.Id = Guid.NewGuid();
        return builder.Build();
    }

    [Fact]
    public async Task GetLocalChangesAsync_TwoSkillsSharePayloadName_ParentlessPayloadNotMisattributed()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CliCloudDefinition());
        Write(accessor, "behaviors/skill-a/SKILL.md", "---\ndescription: a\n---\nBody\n");
        Write(accessor, "behaviors/skill-b/SKILL.md", "---\ndescription: b\n---\nBody\n");
        var lspModel = CliCloudDefinition().WithComponents(new BotComponentBase[]
        {
            ParentlessFileAttachment("cr123_natest.file.ambiguous", "SKILL.md"),
        });

        var (changeSet, _) = await sync.GetLocalChangesAsync(workspace, lspModel, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var ambiguous = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Select(insert => insert.Component).FirstOrDefault(component => component!.SchemaNameString == "cr123_natest.file.ambiguous");
        Assert.True(ambiguous is null || !ambiguous.ParentBotComponentId.HasValue);
    }

    [Fact]
    public async Task GetLocalChangesAsync_RootKnowledgeSharesNameWithSkillPayload_KnowledgeNotReparented()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CliCloudDefinition());
        Write(accessor, "capabilities/knowledge/files/SKILL.md", "root knowledge\n");
        Write(accessor, "behaviors/get-weather/SKILL.md", "---\ndescription: d\n---\nBody\n");
        var lspModel = CliCloudDefinition().WithComponents(new BotComponentBase[]
        {
            ParentlessFileAttachment("cr123_natest.file.rootknowledge", "SKILL.md"),
        });

        var (changeSet, _) = await sync.GetLocalChangesAsync(workspace, lspModel, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var rootKnowledge = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Select(insert => insert.Component).FirstOrDefault(component => component!.SchemaNameString == "cr123_natest.file.rootknowledge");
        Assert.True(rootKnowledge is null || !rootKnowledge.ParentBotComponentId.HasValue);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_RootKnowledgeMetadataWithoutContent_DoesNotReplaceSkillPayload()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var rootKnowledge = ParentlessFileAttachment("cr123_natest.file.rootknowledge", "SKILL.md");
        var cloudDefinition = CliCloudDefinition().WithComponents(new BotComponentBase[] { rootKnowledge });
        WorkspaceSynchronizer.WriteCloudCache(accessor, cloudDefinition);
        Write(accessor, new LspComponentPathResolver().GetComponentPath(rootKnowledge, cloudDefinition), "mcs.metadata:\n  componentName: SKILL.md\n");
        Write(accessor, "behaviors/get-weather/SKILL.md", "skill body\n");
        Write(accessor, "behaviors/get-weather/scripts/run.ps1", "script\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var skill = SkillOf(read);
        var skillMarkdownFiles = read.Components.OfType<FileAttachmentComponent>().Where(component => component.DisplayName == "SKILL.md").ToList();
        Assert.Equal(2, skillMarkdownFiles.Count);
        Assert.Contains(skillMarkdownFiles, component => !component.ParentBotComponentId.HasValue && component.SchemaNameString == rootKnowledge.SchemaNameString);
        Assert.Contains(skillMarkdownFiles, component => component.ParentBotComponentId == skill.Id);
    }

    [Fact]
    public void SynthesizedSkill_FileAttachmentProjectsUnderSkillFolder_NotKnowledge()
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr123_natest\ntemplate: cliagent-1.0.0\n")!;
        var dialog = (DialogBase)CodeSerializer.Deserialize<BotElement>("kind: InlineAgentSkill\ncontent: <!-- bic:bundle=cr123_natest.file.getusweather2zip -->\n")!;
        var skillId = Guid.NewGuid();
        var skill = new DialogComponent(
            schemaName: "cr123_natest.skill.get-us-weather-2",
            displayName: "get-us-weather-2",
            description: string.Empty,
            id: skillId,
            parentBotComponentId: default,
            dialog: dialog);
        var fileBuilder = new FileAttachmentComponent().WithSchemaName("cr123_natest.file.SKILLmd").WithDisplayName("SKILL.md").ToBuilder();
        fileBuilder.Id = Guid.NewGuid();
        fileBuilder.ParentBotComponentId = new BotComponentId(skillId);
        var definition = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { skill, fileBuilder.Build() });

        var path = new LspComponentPathResolver().GetComponentPath(definition.Components.OfType<FileAttachmentComponent>().Single(), definition).Replace('\\', '/');

        Assert.StartsWith("behaviors/get-us-weather-2/", path);
    }
}
