namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.IO;
    using System.Linq;
    using Xunit;

    public class PackagedSkillLspTests
    {
        private const string Bot = "crf9a_nagentn1_T2U1EY";

        private const string CliSettingsYaml =
            "displayName: NAgent N1\n" +
            "schemaName: crf9a_nagentn1_T2U1EY\n" +
            "authenticationMode: Integrated\n" +
            "configuration:\n" +
            "  recognizer:\n" +
            "    kind: CLICopilotRecognizer\n" +
            "  agentSettings:\n" +
            "    instructions: {}\n" +
            "template: cliagent-1.0.0\n";

        private const string SkillYaml =
            "mcs.metadata:\n" +
            "  componentName: get-us-weather\n" +
            "  description: Get the current weather.\n" +
            "kind: InlineAgentSkill\n" +
            "content: <!-- bic:bundle=crf9a_nagentn1_T2U1EY.file.getusweatherzip_hq06y -->\n";

        [Fact]
        public void CompileFile_PackagedSkillPayloadSidecar_ProducesFileAttachmentComponent()
        {
            var parser = new McsFileParser();
            var context = new ProjectionContext(BotName: Bot);
            var sidecarYaml = "mcs.metadata:\n  componentName: ./SKILL.md\n";
            var document = new McsLspDocument(new FilePath("c:/agent/behaviors/get-us-weather_peu/skillmd_dWNAJ.mcs.yml"), sidecarYaml, new DirectoryPath("c:/agent"));

            Assert.IsType<FileAttachmentComponent>(document.FileModel);

            var result = parser.CompileFile(document, context, AuthoringShape.CliCopilot, null);

            Assert.Null(result.error);
            Assert.IsType<FileAttachmentComponent>(result.component);
            Assert.Equal("crf9a_nagentn1_T2U1EY.file.skillmd_dWNAJ", result.component!.SchemaNameString);
            Assert.Equal("./SKILL.md", result.component.DisplayName);
        }

        [Fact]
        public void CompileFile_PackagedSkillPayloadSidecarUnderSubAgent_NotTreatedAsSkillPayload()
        {
            var parser = new McsFileParser();
            var context = new ProjectionContext(BotName: Bot);
            var sidecarYaml = "mcs.metadata:\n  componentName: ./scripts/Get-UsWeather.ps1\n";
            var document = new McsLspDocument(new FilePath("c:/agent/agents/Agent Child 1/behaviors/get-us-weather_peu/scriptsgetusweatherps1_9GRrm.mcs.yml"), sidecarYaml, new DirectoryPath("c:/agent"));

            Assert.IsType<UnknownBotElement>(document.FileModel);

            var result = parser.CompileFile(document, context, AuthoringShape.CliCopilot, null);

            Assert.IsType<UnsupportedBotElementException>(result.error);
        }

        [Fact]
        public void CompileFile_KindlessMetadataFileNotUnderBehaviorsSkillFolder_RemainsUnsupported()
        {
            var parser = new McsFileParser();
            var context = new ProjectionContext(BotName: Bot);
            var sidecarYaml = "mcs.metadata:\n  componentName: ./SKILL.md\n";
            var document = new McsLspDocument(new FilePath("c:/agent/foo/bar/baz.mcs.yml"), sidecarYaml, new DirectoryPath("c:/agent"));

            Assert.IsType<UnknownBotElement>(document.FileModel);

            var result = parser.CompileFile(document, context, AuthoringShape.CliCopilot, null);

            Assert.IsType<UnsupportedBotElementException>(result.error);
        }

        [Theory]
        [InlineData("skillmd_dWNAJ.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n")]
        [InlineData("scriptsgetusweatherps1_9GRrm.mcs.yml", "mcs.metadata:\n  componentName: ./scripts/Get-UsWeather.ps1\n")]
        public void PackagedSkillWorkspace_OpenedPayloadSidecar_HasNoDiagnostics(string sidecarFileName, string sidecarYaml)
        {
            var world = new World();
            world.AddFile("settings.mcs.yml", CliSettingsYaml, elementCheck: false);
            world.AddFile("behaviors/get-us-weather_peu.mcs.yml", SkillYaml, elementCheck: false);
            var sidecar = world.AddFile("behaviors/get-us-weather_peu/" + sidecarFileName, sidecarYaml, elementCheck: false);
            var workspace = world.GetWorkspace();
            workspace.BuildCompilationModel();

            var requestContext = world.GetRequestContext(sidecar, 0);
            var diagnostics = workspace.GetDiagnostics(requestContext)
                .Where(parameters => parameters.Uri.ToString().EndsWith(sidecarFileName))
                .SelectMany(parameters => parameters.Diagnostics)
                .ToList();

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Projection_InlineAgentSkill_ProjectsToDisplayNameFolder()
        {
            var component = CreateInlineSkillComponent($"{Bot}.skill.get-us-weather_peu", "get-us-weather");

            var path = LspProjection.GetFilePath(typeof(InlineAgentSkill), component.SchemaNameString!, Bot, subAgentFolder: null, pathWithoutExtension: null, AuthoringShape.CliCopilot, component, definition: null);

            Assert.Equal("behaviors/get-us-weather.mcs.yml", path);
        }

        [Fact]
        public void Projection_InlineAgentSkillsWithSameDisplayFolder_ProjectToDistinctSchemaPaths()
        {
            var first = CreateInlineSkillComponent($"{Bot}.skill.get-us-weather", "get-us-weather");
            var second = CreateInlineSkillComponent($"{Bot}.skill.get-us-weather_peu", "get-us-weather");
            var definition = new BotDefinition().WithComponents(new BotComponentBase[] { first, second });

            var firstPath = LspProjection.GetFilePath(typeof(InlineAgentSkill), first.SchemaNameString!, Bot, subAgentFolder: null, pathWithoutExtension: null, AuthoringShape.CliCopilot, first, definition);
            var secondPath = LspProjection.GetFilePath(typeof(InlineAgentSkill), second.SchemaNameString!, Bot, subAgentFolder: null, pathWithoutExtension: null, AuthoringShape.CliCopilot, second, definition);

            Assert.Equal("behaviors/get-us-weather.mcs.yml", firstPath);
            Assert.Equal("behaviors/get-us-weather_peu.mcs.yml", secondPath);
        }

        [Fact]
        public void Projection_DisplayFolderCollidingWithSchemaFallback_ProjectsToDistinctPaths()
        {
            var fallback = CreateInlineSkillComponent($"{Bot}.skill.get-us-weather", "***");
            var display = CreateInlineSkillComponent($"{Bot}.skill.other", "get-us-weather");
            var definition = new BotDefinition().WithComponents(new BotComponentBase[] { fallback, display });

            var fallbackPath = LspProjection.GetFilePath(typeof(InlineAgentSkill), fallback.SchemaNameString!, Bot, subAgentFolder: null, pathWithoutExtension: null, AuthoringShape.CliCopilot, fallback, definition);
            var displayPath = LspProjection.GetFilePath(typeof(InlineAgentSkill), display.SchemaNameString!, Bot, subAgentFolder: null, pathWithoutExtension: null, AuthoringShape.CliCopilot, display, definition);

            Assert.Equal("behaviors/get-us-weather.mcs.yml", fallbackPath);
            Assert.Equal("behaviors/other.mcs.yml", displayPath);
        }

        [Fact]
        public void CompileFile_InlineAgentSkill_SchemaNameOverride_RecoversCloudSchema()
        {
            var parser = new McsFileParser();
            var context = new ProjectionContext(BotName: Bot);
            var skillYaml = "mcs.metadata:\n  componentName: get-us-weather\nkind: InlineAgentSkill\ncontent: placeholder\n";
            var document = new McsLspDocument(new FilePath("c:/agent/behaviors/get-us-weather.mcs.yml"), skillYaml, new DirectoryPath("c:/agent"));

            var derived = parser.CompileFile(document, context, AuthoringShape.CliCopilot, null);
            Assert.Null(derived.error);
            Assert.Equal($"{Bot}.skill.get-us-weather", derived.component!.SchemaNameString);

            var overridden = parser.CompileFile(document, context, AuthoringShape.CliCopilot, $"{Bot}.skill.get-us-weather_peu");
            Assert.Null(overridden.error);
            Assert.Equal($"{Bot}.skill.get-us-weather_peu", overridden.component!.SchemaNameString);
        }

        [Fact]
        public void PackagedSkillWorkspace_CompiledDefinition_FileAttachmentComponentsHaveExpectedSchemaAndParent()
        {
            var world = new World();
            world.AddFile("settings.mcs.yml", CliSettingsYaml, elementCheck: false);
            world.AddFile("behaviors/get-us-weather.mcs.yml", SkillYaml, elementCheck: false);
            world.AddFile("behaviors/get-us-weather/.skill.json", "{ \"schemaName\": \"" + Bot + ".skill.get-us-weather\", \"folderName\": \"get-us-weather\" }", elementCheck: false);
            world.AddFile("behaviors/get-us-weather/skillmd_dWNAJ.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n", elementCheck: false);
            world.AddFile("behaviors/get-us-weather/scriptsgetusweatherps1_9GRrm.mcs.yml", "mcs.metadata:\n  componentName: ./scripts/Get-UsWeather.ps1\n", elementCheck: false);

            var workspace = world.GetWorkspace();
            workspace.BuildCompilationModel();

            var definition = workspace.Definition;
            var skill = Assert.Single(definition.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill));
            var skillMarkdown = Assert.Single(definition.Components.OfType<FileAttachmentComponent>().Where(component => component.DisplayName == "./SKILL.md"));
            var script = Assert.Single(definition.Components.OfType<FileAttachmentComponent>().Where(component => component.DisplayName == "./scripts/Get-UsWeather.ps1"));

            Assert.Equal($"{Bot}.skill.get-us-weather", skill.SchemaNameString);
            Assert.Equal($"{Bot}.file.skillmd_dWNAJ", skillMarkdown.SchemaNameString);
            Assert.Equal($"{Bot}.file.scriptsgetusweatherps1_9GRrm", script.SchemaNameString);
            Assert.True(skillMarkdown.ParentBotComponentId.HasValue);
            Assert.Equal(skill.Id, skillMarkdown.ParentBotComponentId);
            Assert.True(script.ParentBotComponentId.HasValue);
            Assert.Equal(skill.Id, script.ParentBotComponentId);
        }

        [Fact]
        public void PackagedSkillWorkspace_OnDisk_LinkedSchemaHasCollisionSuffix_FileAttachmentComponentsHaveExpectedSchemaAndParent()
        {
            var dir = Path.GetFullPath(Path.Combine("TestData", "Workspace", "PackagedSkillWorkspace"));

            var world = new World(dir);
            var workspace = world.GetWorkspace();
            workspace.BuildCompilationModel();

            var definition = workspace.Definition;
            var skill = Assert.Single(definition.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill));
            var skillMarkdown = Assert.Single(definition.Components.OfType<FileAttachmentComponent>().Where(component => component.DisplayName == "./SKILL.md"));
            var script = Assert.Single(definition.Components.OfType<FileAttachmentComponent>().Where(component => component.DisplayName == "./scripts/Get-UsWeather.ps1"));

            Assert.Equal("crf9a_nagentn1_T2U1EY.skill.get-us-weather_e1W", skill.SchemaNameString);
            Assert.True(skillMarkdown.ParentBotComponentId.HasValue);
            Assert.Equal(skill.Id, skillMarkdown.ParentBotComponentId);
            Assert.True(script.ParentBotComponentId.HasValue);
            Assert.Equal(skill.Id, script.ParentBotComponentId);
        }

        private static DialogComponent CreateInlineSkillComponent(string schemaName, string displayName)
        {
            var dialog = (DialogBase)CodeSerializer.Deserialize<BotElement>("kind: InlineAgentSkill\ncontent: placeholder\n")!;
            return new DialogComponent(
                schemaName: schemaName,
                displayName: displayName,
                description: "Packaged skill",
                id: new BotComponentId(Guid.NewGuid()),
                parentBotComponentId: default,
                dialog: dialog);
        }
    }
}
