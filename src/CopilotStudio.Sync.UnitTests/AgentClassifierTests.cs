// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Classification-contract validation for the structured <see cref="AgentClassification"/>
/// (PRD R1, R2, R3, R8; TDD D1, D4, D9, D15, D19). Exercises only released
/// dependencies, proving the bridge classifier needs no unreleased declared signal.
/// </summary>
public class AgentClassifierTests
{
    private const string CliSettingsYaml = @"
displayName: NAgent A2
schemaName: Default_draft_ECaOPZ
configuration:
  recognizer:
    kind: CLICopilotRecognizer
  agentSettings:
    model:
      series: Sonnet46
    instructions: {}
template: cliagent-1.0.0
language: 1033
";

    private const string ClassicSettingsYaml = @"
kind: Bot
displayName: Agent A1
schemaName: cre98_AgentA1
configuration:
  recognizer:
    kind: GenerativeAIRecognizer
template: default-2.1.0
language: 1033
";

    private static BotDefinition Definition(string yaml)
    {
        var bot = CodeSerializer.Deserialize<BotEntity>(yaml);
        Assert.NotNull(bot);
        return new BotDefinition.Builder { Entity = bot! }.Build();
    }

    // ---- Authoring shape (cloud) : the thing we care about ----

    [Fact]
    public void CliConfigurationShape_IsCliCopilot_Supported()
    {
        var c = AgentClassifier.ClassifyCloud(Definition(CliSettingsYaml));
        Assert.Equal(AuthoringShape.CliCopilot, c.AuthoringShape);
        Assert.Equal(SupportLevel.Supported, c.Support);
        Assert.Equal("cli-agent-settings-shape", c.Evidence);
    }

    [Fact]
    public void ClassicSettings_IsClassic_Supported()
    {
        var c = AgentClassifier.ClassifyCloud(Definition(ClassicSettingsYaml));
        Assert.Equal(AuthoringShape.Classic, c.AuthoringShape);
        Assert.Equal(SupportLevel.Supported, c.Support);
    }

    [Fact]
    public void NullEntity_IsUnknown_Unsupported()
    {
        var c = AgentClassifier.ClassifyCloud((BotEntity?)null);
        Assert.Equal(AuthoringShape.Unknown, c.AuthoringShape);
        Assert.Equal(SupportLevel.Unsupported, c.Support);
    }

    [Fact]
    public void CliagentTemplateOnly_IsCliCopilot()
    {
        var yaml = "kind: Bot\ndisplayName: x\nschemaName: x\ntemplate: cliagent-1.0.0\n";
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(Definition(yaml)));
    }

    // D15: native CLI configuration-shape evidence (agentSettings) wins over a
    // conflicting classic template prefix.
    [Fact]
    public void CliRecognizerWithClassicTemplate_PrefersConfigurationShape()
    {
        var yaml = "kind: Bot\ndisplayName: x\nschemaName: x\n" +
                   "configuration:\n  recognizer:\n    kind: CLICopilotRecognizer\n" +
                   "  agentSettings:\n    model:\n      series: Sonnet46\ntemplate: default-2.1.0\n";
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(Definition(yaml)));
    }

    // D9: legacy CLIAgentRecognizer without native CLI configuration shape is not CliCopilot.
    [Fact]
    public void LegacyCliAgentRecognizerWithClassicTemplate_IsClassic()
    {
        var yaml = "kind: Bot\ndisplayName: x\nschemaName: x\n" +
                   "configuration:\n  recognizer:\n    kind: CLIAgentRecognizer\ntemplate: default-2.1.0\n";
        Assert.Equal(AuthoringShape.Classic, AgentClassifier.DetectAuthoringShape(Definition(yaml)));
    }

    // R3/R8/D19: an unrecognized but well-formed shape is Provisional and preserves its raw value.
    [Fact]
    public void UnrecognizedTemplate_IsUnknown_Provisional_PreservesRawValue()
    {
        var yaml = "kind: Bot\ndisplayName: x\nschemaName: x\ntemplate: sdkagent-1.0.0\n";
        var c = AgentClassifier.ClassifyCloud(Definition(yaml));
        Assert.Equal(AuthoringShape.Unknown, c.AuthoringShape);
        Assert.Equal(SupportLevel.Provisional, c.Support);
        Assert.Equal("sdkagent-1.0.0", c.RawShapeValue);
    }

    // ---- Per-operation gate (the flexible fail-closed, D19) ----

    [Theory]
    [InlineData(SyncOperation.Inspect, true)]
    [InlineData(SyncOperation.Clone, true)]
    [InlineData(SyncOperation.Pull, true)]
    [InlineData(SyncOperation.Push, true)]
    [InlineData(SyncOperation.Reattach, true)]
    public void Supported_AllowsEveryOperation(SyncOperation op, bool expected)
    {
        var c = AgentClassifier.ClassifyCloud(Definition(CliSettingsYaml));
        Assert.Equal(expected, c.Allows(op));
    }

    [Theory]
    [InlineData(SyncOperation.Inspect, true)]
    [InlineData(SyncOperation.Clone, true)]   // bootstrap
    [InlineData(SyncOperation.Pull, true)]    // bootstrap
    [InlineData(SyncOperation.Push, false)]   // fail closed: protect cloud
    [InlineData(SyncOperation.Reattach, false)]
    public void Provisional_AllowsCloneAndPull_BlocksDestructive(SyncOperation op, bool expected)
    {
        var yaml = "kind: Bot\ndisplayName: x\nschemaName: x\ntemplate: sdkagent-1.0.0\n";
        var c = AgentClassifier.ClassifyCloud(Definition(yaml));
        Assert.Equal(SupportLevel.Provisional, c.Support);
        Assert.Equal(expected, c.Allows(op));
    }

    [Theory]
    [InlineData(SyncOperation.Inspect, true)]
    [InlineData(SyncOperation.Clone, false)]
    [InlineData(SyncOperation.Pull, false)]
    [InlineData(SyncOperation.Push, false)]
    [InlineData(SyncOperation.Reattach, false)]
    public void Unsupported_AllowsInspectOnly(SyncOperation op, bool expected)
    {
        var c = AgentClassification.None;
        Assert.Equal(expected, c.Allows(op));
    }

    // ---- Workspace layout (disk) : a separate concept ----

    [Fact]
    public void CliSettings_IsCliLayered_And_InfersCliCopilot()
    {
        // D22/D25: CLI agents store the entity in settings.mcs.yml; the layout is
        // detected by content (the AgentSettings block), not a separate agent.yaml.
        WithFolder(("settings.mcs.yml", CliSettingsYaml), dir =>
        {
            Assert.Equal(WorkspaceLayout.CliLayered, AgentClassifier.DetectWorkspaceLayout(dir));
            Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShapeFromFolder(dir));
        });
    }

    [Fact]
    public void SettingsMcsYml_IsClassicMcs_And_InfersClassic()
    {
        WithFolder(("settings.mcs.yml", ClassicSettingsYaml), dir =>
        {
            Assert.Equal(WorkspaceLayout.ClassicMcs, AgentClassifier.DetectWorkspaceLayout(dir));
            Assert.Equal(AuthoringShape.Classic, AgentClassifier.DetectAuthoringShapeFromFolder(dir));
        });
    }

    [Fact]
    public void EmptyFolder_IsUnknownLayout()
    {
        WithFolder(dir => Assert.Equal(WorkspaceLayout.Unknown, AgentClassifier.DetectWorkspaceLayout(dir)));
    }

    // ---- Combined classify: cloud shape authoritative, layout fills in ----

    [Fact]
    public void Classify_CloudCliWithLayeredFolder_RecordsBoth()
    {
        WithFolder(("settings.mcs.yml", CliSettingsYaml), dir =>
        {
            var bot = CodeSerializer.Deserialize<BotEntity>(CliSettingsYaml);
            var c = AgentClassifier.Classify(bot, dir);
            Assert.Equal(AuthoringShape.CliCopilot, c.AuthoringShape);
            Assert.Equal(WorkspaceLayout.CliLayered, c.WorkspaceLayout);
            Assert.Equal(SupportLevel.Supported, c.Support);
        });
    }

    [Fact]
    public void Classify_NoCloud_FallsBackToLayoutInferredShape()
    {
        WithFolder(("settings.mcs.yml", CliSettingsYaml), dir =>
        {
            var c = AgentClassifier.Classify(cloudEntity: null, agentFolder: dir);
            Assert.Equal(AuthoringShape.CliCopilot, c.AuthoringShape);
            Assert.Equal(WorkspaceLayout.CliLayered, c.WorkspaceLayout);
        });
    }

    // ---- D35 fail-closed: layout must NOT promote an explicitly unrecognized shape ----

    private const string UnrecognizedTemplateYaml =
        "kind: Bot\ndisplayName: x\nschemaName: x\ntemplate: sdkagent-1.0.0\n";

    private const string NoShapeEvidenceClassicYaml =
        "kind: Bot\ndisplayName: x\nschemaName: x\n";

    // An EXPLICITLY unrecognized shape (unknown template) stays Provisional even when the
    // local folder content falls back to a classic layout, so push/reattach fail closed (D35).
    // This is the integrated path the gate-matrix tests bypass.
    [Fact]
    public void Classify_UnrecognizedTemplate_WithClassicLookingFolder_StaysProvisional_BlocksDestructive()
    {
        WithFolder(("settings.mcs.yml", UnrecognizedTemplateYaml), dir =>
        {
            var bot = CodeSerializer.Deserialize<BotEntity>(UnrecognizedTemplateYaml);
            var c = AgentClassifier.Classify(bot, dir);

            Assert.Equal(WorkspaceLayout.ClassicMcs, c.WorkspaceLayout);
            Assert.Equal(AuthoringShape.Unknown, c.AuthoringShape);
            Assert.Equal(SupportLevel.Provisional, c.Support);
            Assert.Equal("sdkagent-1.0.0", c.RawShapeValue);
            Assert.True(c.Allows(SyncOperation.Clone));
            Assert.True(c.Allows(SyncOperation.Pull));
            Assert.False(c.Allows(SyncOperation.Push));
            Assert.False(c.Allows(SyncOperation.Reattach));
        });
    }

    // A legacy classic agent with NO shape signal (no template / AgentSettings) is still
    // Supported via its layout - the case the gate must NOT over-block.
    [Fact]
    public void Classify_NoShapeEvidenceClassic_WithClassicFolder_IsSupported()
    {
        WithFolder(("settings.mcs.yml", NoShapeEvidenceClassicYaml), dir =>
        {
            var bot = CodeSerializer.Deserialize<BotEntity>(NoShapeEvidenceClassicYaml);
            var c = AgentClassifier.Classify(bot, dir);

            Assert.Equal(AuthoringShape.Classic, c.AuthoringShape);
            Assert.Equal(WorkspaceLayout.ClassicMcs, c.WorkspaceLayout);
            Assert.Equal(SupportLevel.Supported, c.Support);
            Assert.True(c.Allows(SyncOperation.Push));
            Assert.True(c.Allows(SyncOperation.Reattach));
        });
    }

    // A component-collection root (no BotEntity, frequently no settings.mcs.yml) is a
    // structurally recognized format and must stay Supported so reattach/push of collection
    // roots are not regressed by the gate.
    [Fact]
    public void Classify_ComponentCollectionDefinition_IsSupported()
    {
        var c = AgentClassifier.Classify(new BotComponentCollectionDefinition(), agentFolder: null);

        Assert.Equal(SupportLevel.Supported, c.Support);
        Assert.Equal("component-collection", c.Evidence);
        Assert.True(c.Allows(SyncOperation.Push));
        Assert.True(c.Allows(SyncOperation.Reattach));
    }

    private static void WithFolder(System.Action<string> body) => WithFolder(((string, string)?)null, body);

    private static void WithFolder((string name, string content)? file, System.Action<string> body)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "acl_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            if (file is { } f)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, f.name), f.content);
            }
            body(dir);
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }
}
