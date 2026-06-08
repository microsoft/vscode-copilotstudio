// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentFormatDetectorTests
{
    private const string CliSettingsYaml = @"
displayName: NAgent A2
schemaName: Default_draft_ECaOPZ
accessControlPolicy: GroupMembership
authenticationMode: Integrated
authenticationTrigger: Always
configuration:
  recognizer:
    kind: CLICopilotRecognizer

  agentSettings:
    model:
      series: Sonnet46

    instructions: {}

publishedOn: 2026-06-03T22:07:58.0000000Z
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

    [Fact]
    public void Detect_CliSettings_ReturnsCli()
    {
        var bot = CodeSerializer.Deserialize<BotEntity>(CliSettingsYaml);
        Assert.NotNull(bot);
        Assert.IsType<CLICopilotRecognizer>(bot!.Configuration?.Recognizer);

        var def = new BotDefinition.Builder { Entity = bot }.Build();
        Assert.Equal(AgentFormat.Cli, AgentFormatDetector.Detect(def));
    }

    [Fact]
    public void Detect_ClassicSettings_ReturnsClassic()
    {
        var bot = CodeSerializer.Deserialize<BotEntity>(ClassicSettingsYaml);
        Assert.NotNull(bot);

        var def = new BotDefinition.Builder { Entity = bot }.Build();
        Assert.Equal(AgentFormat.Classic, AgentFormatDetector.Detect(def));
    }

    [Fact]
    public void Detect_NullDefinition_ReturnsUnknown()
    {
        Assert.Equal(AgentFormat.Unknown, AgentFormatDetector.Detect(null));
    }

    [Fact]
    public void Detect_TemplateFallback_RecognizesCliagentPrefix()
    {
        var yaml = @"
kind: Bot
displayName: x
schemaName: x
template: cliagent-1.0.0
";
        var bot = CodeSerializer.Deserialize<BotEntity>(yaml);
        Assert.NotNull(bot);
        var def = new BotDefinition.Builder { Entity = bot! }.Build();
        Assert.Equal(AgentFormat.Cli, AgentFormatDetector.Detect(def));
    }

    [Fact]
    public void Detect_SdkAgentTemplate_ReturnsUnknown()
    {
        var yaml = @"
kind: Bot
displayName: x
schemaName: x
template: sdkagent-1.0.0
";
        var bot = CodeSerializer.Deserialize<BotEntity>(yaml);
        Assert.NotNull(bot);
        var def = new BotDefinition.Builder { Entity = bot! }.Build();
        Assert.Equal(AgentFormat.Unknown, AgentFormatDetector.Detect(def));
    }

    [Fact]
    public void Detect_NoTemplateNoRecognizer_ReturnsUnknown()
    {
        var yaml = @"
kind: Bot
displayName: x
schemaName: x
";
        var bot = CodeSerializer.Deserialize<BotEntity>(yaml);
        Assert.NotNull(bot);
        var def = new BotDefinition.Builder { Entity = bot! }.Build();
        Assert.Equal(AgentFormat.Unknown, AgentFormatDetector.Detect(def));
    }

    [Fact]
    public void DetectFromFolder_CliSettingsOnDisk_ReturnsCli()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afd_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "settings.mcs.yml"), CliSettingsYaml);
            Assert.Equal(AgentFormat.Cli, AgentFormatDetector.DetectFromFolder(dir));
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Fact]
    public void DetectFromFolder_ClassicSettingsOnDisk_ReturnsClassic()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afd_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "settings.mcs.yml"), ClassicSettingsYaml);
            Assert.Equal(AgentFormat.Classic, AgentFormatDetector.DetectFromFolder(dir));
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Fact]
    public void DetectFromFolder_MissingFile_ReturnsUnknown()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afd_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try { Assert.Equal(AgentFormat.Unknown, AgentFormatDetector.DetectFromFolder(dir)); }
        finally { System.IO.Directory.Delete(dir, true); }
    }
}
