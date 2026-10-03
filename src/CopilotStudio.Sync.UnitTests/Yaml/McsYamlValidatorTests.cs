// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class McsYamlValidatorTests
{
    [Theory]
    [InlineData("kind: AdaptiveDialog\nbeginDialog:\n\tkind: OnRecognizedIntent\n", "TabIndentation")]
    [InlineData("kind: AdaptiveDialog\ndisplayName: \"unterminated\n", "UnterminatedQuote")]
    [InlineData("kind: AdaptiveDialog\ndisplayName: one\ndisplayName: two\n", "DuplicateKey")]
    [InlineData("outer:\n  inner: one\n  inner: two\n", "DuplicateKey")]
    [InlineData("kind: AdaptiveDialog\n<<<<<<< ours\ndisplayName: local\n=======\ndisplayName: remote\n>>>>>>> theirs\n", "MergeConflict")]
    public void RejectsShapesThatSilentlyLoseContent(string yaml, string expectedError)
    {
        var failure = Assert.Throws<McsYamlFormatException>(() => McsYamlValidator.ThrowIfMalformed(yaml));

        Assert.Equal(expectedError, failure.Error.ToString());
        Assert.True(failure.Line > 0);
        Assert.True(failure.Column > 0);
    }

    [Fact]
    public void RejectsAConflictThatWouldOtherwiseParseAsTolerableSyntax()
    {
        var conflicted = "instructions: |-\n<<<<<<< \n  local text\n=======\n  remote text\n>>>>>>> \ntemplate: cliagent-1.0.0\n";

        var failure = Assert.Throws<McsYamlFormatException>(() => McsYamlValidator.ThrowIfMalformed(conflicted));

        Assert.Equal("MergeConflict", failure.Error.ToString());
    }

    [Theory]
    [InlineData("kind: AdaptiveDialog\r\n activity: \"{Topic.answer.text}\"\r\n\r\ninputType: {}\r\noutputType: {}")]
    [InlineData("kind: AdaptiveDialog\n activity: \"{Topic.answer.text}\"\n\ninputType: {}\noutputType: {}")]
    [InlineData("kind: AdaptiveDialog\ntext: |-\n  Line one\n  \tLine two\n")]
    [InlineData("kind: AdaptiveDialog\ninputType: {}\noutputType: {}\n")]
    [InlineData("items:\n- name: one\n- name: two\n")]
    [InlineData("description: \"show <<<<<<< in the example\"\n")]
    [InlineData("description: 'show >>>>>>> in the example'\n")]
    [InlineData("description: show <<<<<<< and >>>>>>> in the example\n")]
    [InlineData("content: |\r\n  show <<<<<<< and >>>>>>> in the example\r\n")]
    [InlineData("# <<<<<<< ours\nkind: AdaptiveDialog\n# >>>>>>> theirs\n")]
    [InlineData("kind: InlineAgentSkill\ncontent: |\n  <<<<<<< ours\n  local\n  =======\n  remote\n  >>>>>>> theirs\n")]
    [InlineData("")]
    public void ToleratesShapesTheObjectDeserializerAccepts(string yaml)
    {
        McsYamlValidator.ThrowIfMalformed(yaml);
    }

    [Fact]
    public void DeserializeRejectsDuplicateKeysBeforeReachingTheObjectDeserializer()
    {
        var failure = Assert.Throws<McsYamlFormatException>(() => McsYamlValidator.Deserialize<Agents.ObjectModel.BotEntity>("displayName: one\ndisplayName: two\n"));

        Assert.Equal("DuplicateKey", failure.Error.ToString());
    }

    [Fact]
    public void DeserializeByTypeRejectsDuplicateKeys()
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlValidator.Deserialize("kind: AdaptiveDialog\ndisplayName: one\ndisplayName: two\n", typeof(Agents.ObjectModel.AdaptiveDialog), null));
    }

    [Fact]
    public void DeserializeReturnsTheElementForValidYaml()
    {
        Assert.NotNull(McsYamlValidator.Deserialize("kind: AdaptiveDialog\n", typeof(Agents.ObjectModel.AdaptiveDialog), null));
    }

    [Theory]
    [InlineData("show <<<<<<< in the example")]
    [InlineData("show >>>>>>> in the example")]
    public void DeserializePreservesLiteralMarkerText(string text)
    {
        var entity = McsYamlValidator.Deserialize<Agents.ObjectModel.BotEntity>($"displayName: \"{text}\"\n");

        Assert.Equal(text, entity!.DisplayName);
    }
}
