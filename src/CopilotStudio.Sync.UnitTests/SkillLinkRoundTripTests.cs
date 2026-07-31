// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SkillLinkRoundTripTests
{
    [Fact]
    public void SkillLinkFile_DeleteLink_RemovesHiddenSchemaLink()
    {
        var (_, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-skill-link-delete/"));
        var skillPath = new AgentFilePath("behaviors/get-us-weather.mcs.yml");
        var linkPath = new AgentFilePath("behaviors/get-us-weather/.skill.json");
        SkillLinkFile.WriteLink(fileAccessor, skillPath, "crd1c_agent.skill.get-us-weather_peu");

        SkillLinkFile.DeleteLink(fileAccessor, skillPath);

        Assert.False(fileAccessor.Exists(linkPath));
    }

    [Fact]
    public void GetLocalChanges_PackagedSkillWithLink_RemapsSchema_NoChurn()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-skill-remap/"));

        WriteText(fileAccessor, "behaviors/get-us-weather.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: placeholder\n");
        WriteText(fileAccessor, "behaviors/get-us-weather/.skill.json", "{ \"schemaName\": \"crd1c_agent.skill.get-us-weather_peu\", \"folderName\": \"get-us-weather\" }");

        var cloud = CreateDefinitionWithSkill("crd1c_agent.skill.get-us-weather_peu", "get-us-weather");
        var local = CreateDefinitionWithSkill("crd1c_agent.skill.get-us-weather", "get-us-weather");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Create && c.SchemaName.Contains(".skill."));
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Delete && c.SchemaName.Contains(".skill."));
    }

    [Fact]
    public void GetLocalChanges_LegacySchemaNameSkillFolder_StaysInExistingFolder_NoSplit()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-skill-legacy/"));

        WriteText(fileAccessor, "behaviors/get-us-weather_peu.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: placeholder\n");

        var cloud = CreateDefinitionWithSkill("crd1c_agent.skill.get-us-weather_peu", "get-us-weather", "cloud");
        var local = CreateDefinitionWithSkill("crd1c_agent.skill.get-us-weather_peu", "get-us-weather", "local");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        var skillChange = Assert.Single(changes, c => c.SchemaName.Contains(".skill."));
        Assert.Equal("behaviors/get-us-weather_peu.mcs.yml", skillChange.Uri.Replace('\\', '/'));
    }

    [Fact]
    public void GetRemoteChanges_SkillDisplayNameChanged_PreviewUsesExistingFolder()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-skill-remote-rename/"));
        WriteText(fileAccessor, "behaviors/get-us-weather.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: placeholder\n");
        WriteText(fileAccessor, "behaviors/get-us-weather/.skill.json", "{ \"schemaName\": \"crd1c_agent.skill.get-us-weather_peu\", \"folderName\": \"get-us-weather\" }");
        var cloud = CreateDefinitionWithSkill("crd1c_agent.skill.get-us-weather_peu", "get-us-weather");
        var remote = CreateDefinitionWithSkill("crd1c_agent.skill.get-us-weather_peu", "get-us-weather-v2");

        var (_, changes) = synchronizer.GetLocalChanges(remote, cloud, fileAccessor, "token-1", isRemoteChange: true);

        var skillChange = Assert.Single(changes, change => change.SchemaName.Contains(".skill."));
        Assert.Equal("behaviors/get-us-weather.mcs.yml", skillChange.Uri.Replace('\\', '/'));
    }

    [Fact]
    public void GetRemoteChanges_SkillDisplayNameBecomesUnusable_PreviewUsesExistingFolder()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-skill-remote-unusable-name/"));
        WriteText(fileAccessor, "behaviors/get-us-weather.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: placeholder\n");
        WriteText(fileAccessor, "behaviors/get-us-weather/.skill.json", "{ \"schemaName\": \"crd1c_agent.skill.get-us-weather_peu\", \"folderName\": \"get-us-weather\" }");
        var cloud = CreateDefinitionWithSkill("crd1c_agent.skill.get-us-weather_peu", "get-us-weather");
        var remote = CreateDefinitionWithSkill("crd1c_agent.skill.get-us-weather_peu", "***");

        var (_, changes) = synchronizer.GetLocalChanges(remote, cloud, fileAccessor, "token-1", isRemoteChange: true);

        var skillChange = Assert.Single(changes, change => change.SchemaName.Contains(".skill."));
        Assert.Equal("behaviors/get-us-weather.mcs.yml", skillChange.Uri.Replace('\\', '/'));
    }

    private static BotDefinition CreateDefinitionWithSkill(string skillSchema, string displayName, string description = "")
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent\ntemplate: cliagent-1.0.0\n")!;
        var dialog = (DialogBase)CodeSerializer.Deserialize<BotElement>("kind: InlineAgentSkill\ncontent: placeholder\n")!;
        var component = new DialogComponent(
            schemaName: skillSchema,
            displayName: displayName,
            description: description,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: dialog);
        return new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { component });
    }

    [Fact]
    public void GetLocalChanges_CompiledSkillPayloadFilesWithFabricatedIdsAndNoParentId_NoPhantomCreateRightAfterClone()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-skill-clone-phantom/"));

        WriteText(fileAccessor, "behaviors/get-us-weather.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: placeholder\n");
        WriteText(fileAccessor, "behaviors/get-us-weather/skillmd_49zNm.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n");
        WriteText(fileAccessor, "behaviors/get-us-weather/scriptsgetusweatherps1_GNNvS.mcs.yml", "mcs.metadata:\n  componentName: ./scripts/Get-UsWeather.ps1\n");

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crf9a_nagentn3_5pJH_2\ntemplate: cliagent-1.0.0\n")!;
        var dialog = (DialogBase)CodeSerializer.Deserialize<BotElement>("kind: InlineAgentSkill\ncontent: placeholder\n")!;

        var cloudSkillId = Guid.Parse("840d995d-dc92-4260-b4a6-6c295e4fe474");
        var cloudSkill = new DialogComponent(schemaName: "crf9a_nagentn3_5pJH_2.skill.get-us-weather_e1W", displayName: "get-us-weather", description: "Get the current weather.", id: cloudSkillId, parentBotComponentId: default, dialog: dialog);
        var cloudParentId = new BotComponentId(cloudSkillId);
        var cloudSkillMarkdown = CreateFileAttachment("crf9a_nagentn3_5pJH_2.file.skillmd_49zNm", "./SKILL.md", cloudParentId);
        var cloudScript = CreateFileAttachment("crf9a_nagentn3_5pJH_2.file.scriptsgetusweatherps1_GNNvS", "./scripts/Get-UsWeather.ps1", cloudParentId);
        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { cloudSkill, cloudSkillMarkdown, cloudScript });

        var localSkill = new DialogComponent(schemaName: "crf9a_nagentn3_5pJH_2.skill.get-us-weather_e1W", displayName: "get-us-weather", description: "Get the current weather.", id: Guid.NewGuid(), parentBotComponentId: default, dialog: dialog);
        var localSkillMarkdown = CreateFileAttachment("crf9a_nagentn3_5pJH_2.file.skillmd_49zNm", "./SKILL.md", default);
        var localScript = CreateFileAttachment("crf9a_nagentn3_5pJH_2.file.scriptsgetusweatherps1_GNNvS", "./scripts/Get-UsWeather.ps1", default);
        var local = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { localSkill, localSkillMarkdown, localScript });

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.Empty(changes);
    }

    [Fact]
    public void GetLocalChanges_SkillPayloadSidecarOnDiskMissingFromLocalDefinition_NoSpuriousDelete()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-skill-payload/"));

        WriteText(fileAccessor, "behaviors/get-us-weather-1.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather-1\nkind: InlineAgentSkill\ncontent: placeholder\n");
        WriteText(fileAccessor, "behaviors/get-us-weather-1/.skill.json", "{ \"schemaName\": \"crd1c_agent.skill.get-us-weather_peu\", \"folderName\": \"get-us-weather-1\" }");
        WriteText(fileAccessor, "behaviors/get-us-weather-1/skillmd_dWNAJ.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n");
        WriteText(fileAccessor, "behaviors/get-us-weather-1/scriptsgetusweatherps1_9GRrm.mcs.yml", "mcs.metadata:\n  componentName: ./scripts/Get-UsWeather.ps1\n");

        var (cloud, local) = CreateSkillWithPayloadDefinitions();

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Delete);
    }

    [Fact]
    public void GetLocalChanges_LegacySchemaNameFolder_PayloadSidecarOnDisk_NoSpuriousDelete()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-skill-legacy-payload/"));

        WriteText(fileAccessor, "behaviors/get-us-weather_peu.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather-1\nkind: InlineAgentSkill\ncontent: placeholder\n");
        WriteText(fileAccessor, "behaviors/get-us-weather_peu/skillmd_dWNAJ.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n");
        WriteText(fileAccessor, "behaviors/get-us-weather_peu/scriptsgetusweatherps1_9GRrm.mcs.yml", "mcs.metadata:\n  componentName: ./scripts/Get-UsWeather.ps1\n");

        var (cloud, local) = CreateSkillWithPayloadDefinitions();

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Delete);
    }

    private static (BotDefinition Cloud, BotDefinition Local) CreateSkillWithPayloadDefinitions()
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent\ntemplate: cliagent-1.0.0\n")!;
        var dialog = (DialogBase)CodeSerializer.Deserialize<BotElement>("kind: InlineAgentSkill\ncontent: placeholder\n")!;
        var skillId = Guid.NewGuid();
        var skill = new DialogComponent(
            schemaName: "crd1c_agent.skill.get-us-weather_peu",
            displayName: "get-us-weather-1",
            description: string.Empty,
            id: skillId,
            parentBotComponentId: default,
            dialog: dialog);
        var parentId = new BotComponentId(skillId);
        var skillMarkdown = CreateFileAttachment("crd1c_agent.file.skillmd_dWNAJ", "./SKILL.md", parentId);
        var script = CreateFileAttachment("crd1c_agent.file.scriptsgetusweatherps1_9GRrm", "./scripts/Get-UsWeather.ps1", parentId);

        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { skill, skillMarkdown, script });
        var local = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { skill });
        return (cloud, local);
    }

    private static FileAttachmentComponent CreateFileAttachment(string schemaName, string displayName, BotComponentId parentId)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .ToBuilder();
        builder.Id = Guid.NewGuid();
        builder.ParentBotComponentId = parentId;
        return builder.Build();
    }

    private static void WriteText(IFileAccessor accessor, string path, string contents)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(contents);
    }
}
