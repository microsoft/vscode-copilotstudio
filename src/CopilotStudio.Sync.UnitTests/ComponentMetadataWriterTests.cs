// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.McsCore.Yaml;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ComponentMetadataWriterTests
{
    private const string Bot = "Default_draft_ECaOPZ";

    private const string MultiLineDescription = "Intro\nURL: https://example.org\nMore details";

    private static readonly AgentFilePath TopicPath = new AgentFilePath("topics/weather.mcs.yml");

    [Fact]
    public void SerializeComponent_MultiLineDescriptionWithAColon_EmitsABlockScalar()
    {
        var serialized = McsComponentBodyWriter.SerializeComponent(CreateComponent(MultiLineDescription), Definition(), TopicPath);

        Assert.Contains("description: |-", serialized, StringComparison.Ordinal);
        Assert.Contains("URL: https://example.org", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceBodyPreservingMetadata_MultiLineDescriptionWithAColon_KeepsEveryContinuationLine()
    {
        var rewritten = Rewrite(MultiLineDescription);

        Assert.Contains("Intro", rewritten, StringComparison.Ordinal);
        Assert.Contains("URL: https://example.org", rewritten, StringComparison.Ordinal);
        Assert.Contains("More details", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceBodyPreservingMetadata_MultiLineDescriptionWithAColon_RoundTripsThroughTheReader()
    {
        Assert.Equal(MultiLineDescription, ReadMetadata(Rewrite(MultiLineDescription))[McsMetadata.DescriptionKey]);
    }

    [Fact]
    public void ReplaceBodyPreservingMetadata_MultiLineDescriptionWithAColon_DoesNotLeakAContinuationLineAsItsOwnKey()
    {
        Assert.DoesNotContain("URL", ReadMetadata(Rewrite(MultiLineDescription)).Keys);
    }

    [Fact]
    public void ReplaceBodyPreservingMetadata_DescriptionWithABlankLine_KeepsBothParagraphs()
    {
        const string description = "First paragraph\n\nSecond paragraph";

        Assert.Equal(description, ReadMetadata(Rewrite(description))[McsMetadata.DescriptionKey]);
    }

    [Fact]
    public void ReplaceBodyPreservingMetadata_MultiLineDescription_KeepsTheDisplayNameAndReplacementBody()
    {
        var rewritten = Rewrite(MultiLineDescription);

        Assert.Equal("Weather", ReadMetadata(rewritten)[McsMetadata.ComponentNameKey]);
        Assert.EndsWith("kind: ReplacementBody\n", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceBodyPreservingMetadata_SingleLineDescription_IsUnchanged()
    {
        Assert.Equal("an ordinary description", ReadMetadata(Rewrite("an ordinary description"))[McsMetadata.DescriptionKey]);
    }

    [Fact]
    public void ReplaceBodyPreservingMetadata_ConflictedDescription_KeepsBothSidesAndTheDisplayName()
    {
        var rewritten = Rewrite(McsConflictMarkers.Build("LOCAL text", "REMOTE text"));

        Assert.Contains("componentName: Weather", rewritten, StringComparison.Ordinal);
        Assert.Contains("  description: LOCAL text", rewritten, StringComparison.Ordinal);
        Assert.Contains("  description: REMOTE text", rewritten, StringComparison.Ordinal);
    }

    private static string Rewrite(string description)
        => McsComponentBodyWriter.ReplaceBodyPreservingMetadata(CreateComponent(description), Definition(), TopicPath, "kind: ReplacementBody\n");

    private static IDictionary<string, object?> ReadMetadata(string yaml)
        => (IDictionary<string, object?>)McsYamlReader.Parse(yaml)[McsMetadata.PropertyName]!;

    private static DefinitionBase Definition() => new BotDefinition();

    private static BotComponentBase CreateComponent(string description)
    {
        var dialog = CodeSerializer.Deserialize<BotElement>("kind: ConnectedAgentTool\nbotSchemaName: cre98_AgentC4\nhistoryType:\n  kind: ConversationHistory\n");
        return new DialogComponent.Builder
        {
            SchemaName = new DialogSchemaName($"{Bot}.topic.weather"),
            Id = new BotComponentId(Guid.NewGuid()),
            DisplayName = "Weather",
            Description = description,
        }.Build().WithDialog((DialogBase)dialog!);
    }
}
