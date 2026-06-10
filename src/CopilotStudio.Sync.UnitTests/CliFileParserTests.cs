// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Recovered from PR #265 (deferred from Node N), adjusted for Node Q:
//   - Three-layer .mcs.yml paths (D21): tools -> capabilities/tools/, skills ->
//     behaviors/, connected agents -> capabilities/tools/ (D10, NOT
//     capabilities/agents/).
//   - CompileFile is called with the CliCopilot shape so the CLI projection rules
//     derive the component schema name (the rules are shape-gated, D20).
//
// The maker-safety net: SerializeAsMcsYml_CliComponent_AfterJsonRoundTrip_* exercises
// the JSON round-trip that exposed the Node-L $kind instruction-wipe; it must still
// carry mcs.metadata and a discriminatable body after a structured round-trip.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliFileParserTests
{
    private const string Bot = "Default_draft_ECaOPZ";

    private static readonly LspProjectorService Service = LspProjectorService.Instance;

    [Theory]
    [InlineData("kind: WorkflowTool\nworkflowId: 4f66c140-e032-f111-88b4-7ced8d3b6119\n",
                "capabilities/tools/AgentFlow1.mcs.yml",
                "Default_draft_ECaOPZ.tool.AgentFlow1")]
    [InlineData("kind: ConnectorTool\nconnectionReference: shared_x\noperationId: GetRow\n",
                "capabilities/tools/Getarow.mcs.yml",
                "Default_draft_ECaOPZ.tool.Getarow")]
    [InlineData("kind: InlineAgentSkill\ncontent: |\n  ---\n  name: w\n  ---\n  Skill body\n",
                "behaviors/weather.mcs.yml",
                "Default_draft_ECaOPZ.skill.weather")]
    [InlineData("kind: ConnectedAgentTool\nbotSchemaName: cre98_AgentC4\n",
                "capabilities/tools/cre98_AgentC4.mcs.yml",
                "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4")]
    [InlineData("kind: McpTool\nserverUrl: https://example/mcp\n",
                "capabilities/tools/WorkIQCopilotPreview.mcs.yml",
                "Default_draft_ECaOPZ.tool.WorkIQCopilotPreview")]
    public void CompileFile_CliKind_RoutesToExpectedComponent(string yaml, string filePath, string expectedSchemaName)
    {
        var model = CodeSerializer.Deserialize<BotElement>(yaml);
        Assert.NotNull(model);

        var parser = new SyncMcsFileParser(Service);
        var ctx = new ProjectionContext(BotName: Bot);
        var relativePath = new AgentFilePath(filePath);

        var (component, error) = parser.CompileFile(relativePath, model!, ctx, AuthoringShape.CliCopilot);

        Assert.Null(error);
        Assert.NotNull(component);
        Assert.Equal(expectedSchemaName, component!.SchemaNameString);
    }

    [Theory]
    [InlineData(
        "kind: InlineAgentSkill\ncontent: |\n  ---\n  name: weatherskill\n  description: skill body header\n  ---\n  Hello\n",
        "weatherskill",
        "Fetch and report weather conditions for a given location.")]
    [InlineData(
        "kind: ConnectorTool\nconnectorId: /providers/Microsoft.PowerApps/apis/shared_excelonlinebusiness\nauthMode: Invoker\nconnectionReference: Default_draft_GiPqWk.cr.shared_excelonlinebusiness\noperationId: GetItem\n",
        "Get a row",
        "Get a row using a key column.")]
    [InlineData(
        "kind: ConnectedAgentTool\nbotSchemaName: cre98_AgentC4\nhistoryType:\n  kind: ConversationHistory\n",
        "Agent C4",
        "Connect to agent c4")]
    public void SerializeAsMcsYml_CliComponent_EmitsMcsMetadata(string dialogYaml, string displayName, string description)
    {
        var dialog = CodeSerializer.Deserialize<BotElement>(dialogYaml);
        Assert.NotNull(dialog);

        var component = new DialogComponent.Builder
        {
            SchemaName = new DialogSchemaName($"{Bot}.skill.weather"),
            Id = new BotComponentId(Guid.NewGuid()),
            DisplayName = displayName,
            Description = description,
        }.Build().WithDialog((DialogBase)dialog!);

        using var sw = new StringWriter();
        CodeSerializer.SerializeAsMcsYml(sw, component);
        var text = sw.ToString();

        Assert.Contains("mcs.metadata:", text);
        Assert.Contains(displayName, text);
    }

    [Theory]
    [InlineData(
        "kind: InlineAgentSkill\ncontent: |\n  ---\n  name: weatherskill\n  description: skill body header\n  ---\n  Hello\n",
        "weatherskill",
        "Fetch and report weather conditions for a given location.")]
    [InlineData(
        "kind: ConnectorTool\nconnectorId: /providers/Microsoft.PowerApps/apis/shared_excelonlinebusiness\nauthMode: Invoker\nconnectionReference: Default_draft_GiPqWk.cr.shared_excelonlinebusiness\noperationId: GetItem\n",
        "Get a row",
        "Get a row using a key column.")]
    [InlineData(
        "kind: ConnectedAgentTool\nbotSchemaName: cre98_AgentC4\nhistoryType:\n  kind: ConversationHistory\n",
        "Agent C4",
        "Connect to agent c4")]
    public void SerializeAsMcsYml_CliComponent_AfterJsonRoundTrip_EmitsMcsMetadata(string dialogYaml, string displayName, string description)
    {
        var dialog = CodeSerializer.Deserialize<BotElement>(dialogYaml);
        Assert.NotNull(dialog);

        var component = new DialogComponent.Builder
        {
            SchemaName = new DialogSchemaName($"{Bot}.skill.weather"),
            Id = new BotComponentId(Guid.NewGuid()),
            DisplayName = displayName,
            Description = description,
        }.Build().WithDialog((DialogBase)dialog!);

        var bot = new BotDefinition().WithComponents(System.Collections.Immutable.ImmutableArray.Create((BotComponentBase)component));
        var plainJson = System.Text.Json.JsonSerializer.Serialize<DefinitionBase>(bot, ElementSerializer.CreateOptions());
        var plainRoundTripped = System.Text.Json.JsonSerializer.Deserialize<DefinitionBase>(plainJson, ElementSerializer.CreateOptions());
        using (var swPlain = new StringWriter())
        {
            CodeSerializer.SerializeAsMcsYml(swPlain, plainRoundTripped!.Components.Single());
            Assert.DoesNotContain("mcs.metadata:", swPlain.ToString());
        }

        string json;
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            json = System.Text.Json.JsonSerializer.Serialize<DefinitionBase>(bot, ElementSerializer.CreateOptions());
        }
        DefinitionBase? roundTripped;
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            roundTripped = System.Text.Json.JsonSerializer.Deserialize<DefinitionBase>(json, ElementSerializer.CreateOptions());
        }
        Assert.NotNull(roundTripped);
        var rtComponent = roundTripped!.Components.Single();

        using var sw = new StringWriter();
        CodeSerializer.SerializeAsMcsYml(sw, rtComponent);
        var text = sw.ToString();

        Assert.Contains("mcs.metadata:", text);
        Assert.Contains(displayName, text);
    }

    [Fact]
    public async System.Threading.Tasks.Task CloneChangesAsync_InlineAgentSkillFromCloud_WritesMcsMetadata()
    {
        var resourceName = typeof(CliFileParserTests).Assembly.GetName().Name + ".TestData.botdefinition.json";
        using var stream = typeof(CliFileParserTests).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);

        DefinitionBase? definition;
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            definition = System.Text.Json.JsonSerializer.Deserialize<DefinitionBase>(stream!, ElementSerializer.CreateOptions());
        }
        Assert.NotNull(definition);

        var skill = definition!.Components
            .OfType<DialogComponent>()
            .Single(dc => dc.Dialog is InlineAgentSkill);

        Assert.False(string.IsNullOrWhiteSpace(skill.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(skill.Description));

        var structuredJson = System.Text.Json.JsonSerializer.Serialize<DefinitionBase>(
            new BotDefinition().WithComponents(System.Collections.Immutable.ImmutableArray.Create((BotComponentBase)skill)),
            ElementSerializer.CreateOptions());
        var cloudSkill = System.Text.Json.JsonSerializer
            .Deserialize<DefinitionBase>(structuredJson, ElementSerializer.CreateOptions())!
            .Components.OfType<DialogComponent>().Single();

        // The structured (non-pass-through) round-trip strips mcs.metadata: this probe
        // documents why the cloud cache + clone path stay in the pass-through context.
        using (var probe = new StringWriter())
        {
            CodeSerializer.SerializeAsMcsYml(probe, cloudSkill);
            Assert.DoesNotContain("mcs.metadata:", probe.ToString());
        }

        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/clone-skill/");
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();

        // Use the fixture's genuine CLI entity (carries AgentSettings) so the writer
        // derives the CliCopilot shape and routes the skill to behaviors/ (D20/D30).
        // The cloud component is the pass-through-loaded skill (carries mcs.metadata in
        // ExtensionData, exactly as a real Island clone delivers it), so the converged
        // SerializeAsMcsYml writer re-emits mcs.metadata at the behaviors/ path.
        var botEntity = ((BotDefinition)definition!).Entity!;
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(botEntity));
        var changeset = new PvaComponentChangeSet(
            new System.Collections.Generic.List<BotComponentChange> { new BotComponentInsert(skill) },
            botEntity,
            "token-1");

        mockIsland
            .Setup(x => x.GetComponentsAsync(
                Moq.It.IsAny<AuthoringOperationContextBase>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(changeset);

        var mockDataverse = new Moq.Mock<ISyncDataverseClient>();
        await synchronizer.CloneChangesAsync(
            workspace,
            new ReferenceTracker(),
            operationContext,
            mockDataverse.Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            System.Threading.CancellationToken.None);

        var fileAccessor = fileAccessorFactory.Create(workspace);
        var allFiles = fileAccessor.ListFiles().Select(p => p.ToString()).OrderBy(s => s).ToList();
        var skillFile = allFiles.FirstOrDefault(f => f.StartsWith("behaviors/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".mcs.yml", StringComparison.OrdinalIgnoreCase));
        Assert.True(skillFile is not null, "Expected a behaviors/*.mcs.yml file. Written files:\n" + string.Join("\n", allFiles));

        var written = await fileAccessor.ReadStringAsync(new AgentFilePath(skillFile!), System.Threading.CancellationToken.None);
        Assert.True(written.Contains("mcs.metadata:"), "WRITTEN FILE (" + skillFile + "):\n" + written);
        Assert.Contains(skill.DisplayName!, written);
        Assert.Contains("kind: InlineAgentSkill", written);
    }
}
