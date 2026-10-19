// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlObjectMapperTests
{
    [Theory]
    [InlineData("WorkflowId", "workflowId")]
    [InlineData("JsonFileName", "jsonFileName")]
    [InlineData("AIModelId", "aIModelId")]
    [InlineData("IsCustomProcessingStepAllowedForOtherPublishers", "isCustomProcessingStepAllowedForOtherPublishers")]
    [InlineData("Name", "name")]
    public void MatchesHistoricalKeyCasing(string propertyName, string expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.SerializeName(propertyName));
    }

    [Fact]
    public void WritesPromptKeysPacExpects()
    {
        var yaml = McsYamlObjectMapper.Serialize(new SyncDataverseClient.AIPromptMetadata
        {
            AIModelId = Guid.Parse("3b5436b4-d7b4-4389-96e8-107446c9094a"),
            Name = "prompt child 1",
            TemplateId = Guid.Parse("edfdb190-3791-45d8-9a6c-8f90a37c278a"),
        });

        Assert.Equal(
            LineEndings.ToPlatform("aIModelId: 3b5436b4-d7b4-4389-96e8-107446c9094a\r\nname: prompt child 1\r\ntemplateId: edfdb190-3791-45d8-9a6c-8f90a37c278a\r\n"),
            yaml);
    }

    [Fact]
    public void ExcludesIgnoredPropertiesFromOutput()
    {
        var yaml = McsYamlObjectMapper.Serialize(new SyncDataverseClient.AIPromptMetadata
        {
            AIModelId = Guid.NewGuid(),
            Name = "prompt",
            CustomConfiguration = "{\"large\":\"payload\"}",
            IsUnreadableReferencePlaceholder = true,
        });

        Assert.DoesNotContain("customConfiguration", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("isUnreadableReferencePlaceholder", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludesClientDataFromWorkflowOutput()
    {
        var yaml = McsYamlObjectMapper.Serialize(new SyncDataverseClient.WorkflowMetadata
        {
            Name = "Flow",
            ClientData = "{\"definition\":\"large\"}",
        });

        Assert.DoesNotContain("clientData", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("definition", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripsWorkflowMetadata()
    {
        var original = new SyncDataverseClient.WorkflowMetadata
        {
            JsonFileName = "workflows/F-abc/workflow.json",
            WorkflowId = Guid.Parse("4f66c140-e032-f111-88b4-7ced8d3b6119"),
            Name = "Agent Flow 1",
            Type = 1,
            Description = "Version 6 - response.",
            Subprocess = false,
            StateCode = 0,
            StatusCode = 1,
            IsTransacted = true,
            IntroducedVersion = "1.0",
            PrimaryEntity = "none",
            IsCustomizable = new SyncDataverseClient.ManagedProperty { Value = true, CanBeChanged = true, ManagedPropertyLogicalName = "iscustomizableanddeletable" },
            ConnectionReferences = new List<string> { "new_sharedsendmail_9800e", "cre98_AgentC1.cr.WX3p-EQ4" },
        };

        var restored = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(McsYamlObjectMapper.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.JsonFileName, restored!.JsonFileName);
        Assert.Equal(original.WorkflowId, restored.WorkflowId);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Type, restored.Type);
        Assert.Equal(original.Description, restored.Description);
        Assert.Equal(original.Subprocess, restored.Subprocess);
        Assert.Equal(original.IsTransacted, restored.IsTransacted);
        Assert.Equal(original.IntroducedVersion, restored.IntroducedVersion);
        Assert.Equal(original.ConnectionReferences, restored.ConnectionReferences);
        Assert.Equal(original.IsCustomizable!.Value, restored.IsCustomizable!.Value);
        Assert.Equal(original.IsCustomizable.ManagedPropertyLogicalName, restored.IsCustomizable.ManagedPropertyLogicalName);
    }

    [Fact]
    public void KeepsVersionLikeStringAsText()
    {
        var restored = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>("introducedVersion: 1.0\r\nname: Flow\r\n");

        Assert.Equal("1.0", restored!.IntroducedVersion);
    }

    [Fact]
    public void IgnoresUnknownKeysWhenBindingToPoco()
    {
        var restored = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>("name: Flow\r\nsomeFutureField: value\r\n");

        Assert.Equal("Flow", restored!.Name);
    }

    [Theory]
    [InlineData("name: Flow\r\nsomeFutureField: value\r\n")]
    [InlineData("name: Flow\r\nstatecode: 0\r\n")]
    [InlineData("workflowID: 11111111-1111-1111-1111-111111111111\r\n")]
    [InlineData("name: Flow\r\nisCustomizable:\r\n  Value: true\r\n")]
    [InlineData("name: Flow\r\nclientData: '{}'\r\n")]
    public void StrictModeRejectsUnknownKeys(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.DeserializeStrict<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Fact]
    public void StrictModeAcceptsKnownKeys()
    {
        var restored = McsYamlObjectMapper.DeserializeStrict<SyncDataverseClient.WorkflowMetadata>("name: Flow\r\nstateCode: 0\r\nisCustomizable:\r\n  value: true\r\n  canBeChanged: false\r\n");

        Assert.Equal("Flow", restored!.Name);
        Assert.Equal(0, restored.StateCode);
        Assert.True(restored.IsCustomizable!.Value);
    }

    [Fact]
    public void StrictModeRoundTripsMapperOutput()
    {
        var original = new SyncDataverseClient.WorkflowMetadata
        {
            Name = "Agent Flow 1",
            WorkflowId = Guid.Parse("4f66c140-e032-f111-88b4-7ced8d3b6119"),
            StateCode = 1,
            StatusCode = 2,
            IsCustomizable = new SyncDataverseClient.ManagedProperty { Value = true, CanBeChanged = true },
            ConnectionReferences = new List<string> { "shared_a" },
        };

        Assert.Equal("Agent Flow 1", McsYamlObjectMapper.DeserializeStrict<SyncDataverseClient.WorkflowMetadata>(McsYamlObjectMapper.Serialize(original))!.Name);
    }

    [Theory]
    [InlineData("workflowId: nope\r\n")]
    [InlineData("type: 2147483648\r\n")]
    [InlineData("subprocess: treu\r\n")]
    [InlineData("isCustomizable: true\r\n")]
    [InlineData("connectionReferences: {}\r\n")]
    [InlineData("name:\r\n  nested: value\r\n")]
    [InlineData("connectionReferences: single\r\n")]
    public void ThrowsOnValuesThatDoNotMatchTheTargetProperty(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Fact]
    public void ThrowsOnNestedValueThatDoesNotMatchTheTargetProperty()
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>("isCustomizable:\r\n  value: maybe\r\n"));
    }

    [Theory]
    [InlineData("subprocess: yes\r\n", true)]
    [InlineData("subprocess: on\r\n", true)]
    [InlineData("subprocess: True\r\n", true)]
    [InlineData("subprocess: y\r\n", true)]
    [InlineData("subprocess: Y\r\n", true)]
    [InlineData("subprocess: no\r\n", false)]
    [InlineData("subprocess: off\r\n", false)]
    [InlineData("subprocess: FALSE\r\n", false)]
    [InlineData("subprocess: n\r\n", false)]
    [InlineData("subprocess: N\r\n", false)]
    public void ReadsEveryBooleanSpelling(string yaml, bool expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Subprocess);
    }

    [Theory]
    [InlineData("type: 0x10\r\n", 16)]
    [InlineData("type: 1_000\r\n", 1000)]
    [InlineData("type: 010\r\n", 8)]
    [InlineData("type: -5\r\n", -5)]
    [InlineData("type: 42\r\n", 42)]
    [InlineData("type: 0x7FFFFFFF\r\n", 2147483647)]
    [InlineData("type: 1:20\r\n", 80)]
    public void ReadsEveryIntegerSpelling(string yaml, int expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Type);
    }

    [Theory]
    [InlineData("type: 0xFFFFFFFFFFFFFFFF\r\n")]
    [InlineData("type: 0x100000000\r\n")]
    [InlineData("type: \"\"\r\n")]
    [InlineData("subprocess: \"\"\r\n")]
    [InlineData("workflowId: ''\r\n")]
    public void ThrowsOnValuesThatOverflowOrAreExplicitlyEmpty(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Fact]
    public void KeepsNullForAbsentValues()
    {
        var restored = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>("type:\r\nsubprocess:\r\nname: Flow\r\n")!;

        Assert.Null(restored.Type);
        Assert.Null(restored.Subprocess);
        Assert.Equal("Flow", restored.Name);
    }

    [Theory]
    [InlineData("workflowId: null\r\n")]
    [InlineData("workflowId: ~\r\n")]
    [InlineData("workflowId:\r\n")]
    public void ThrowsWhenAnIdentifierHasNoValue(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Theory]
    [InlineData("isCustomizable:\r\n  value:\r\n")]
    [InlineData("isCustomizable:\r\n  value: null\r\n")]
    [InlineData("isCustomizable:\r\n  value: ~\r\n")]
    public void KeepsTheDefaultForNonNullableValuesWithNoValue(string yaml)
    {
        Assert.False(McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.IsCustomizable!.Value);
    }

    [Theory]
    [InlineData("stateCode: \"True\"\r\n")]
    [InlineData("type: True\r\n")]
    [InlineData("stateCode: \"False\"\r\n")]
    [InlineData("type: true\r\n")]
    public void ThrowsWhenAnUntaggedBooleanIsBoundToAnInteger(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Fact]
    public void KeepsNullForAbsentNullableIdentifiers()
    {
        Assert.Null(McsYamlObjectMapper.Deserialize<SyncDataverseClient.AIPromptMetadata>("aIModelId: b823f6a0-c344-482f-9cd5-dc3c2e6aa959\r\ntemplateId:\r\n")!.TemplateId);
    }

    [Theory]
    [InlineData("name: !!int 0x10\r\n", "16")]
    [InlineData("name: !!bool yes\r\n", "True")]
    [InlineData("name: !!float 1.0\r\n", "1")]
    [InlineData("name: !!str plain\r\n", "plain")]
    public void BindsTaggedScalarsToStringProperties(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Name);
    }

    [Theory]
    [InlineData("type: !!int 5\r\n", 5)]
    [InlineData("type: !!float 1.0\r\n", 1)]
    [InlineData("type: !!float 1.5\r\n", 2)]
    [InlineData("type: !!float 2.5\r\n", 2)]
    [InlineData("type: !!int 0x10\r\n", 16)]
    [InlineData("type: !!bool true\r\n", 1)]
    [InlineData("type: !!bool false\r\n", 0)]
    public void BindsTaggedScalarsToIntegerProperties(string yaml, int expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Type);
    }

    [Theory]
    [InlineData("subprocess: !!int 1\r\n", true)]
    [InlineData("subprocess: !!int 0\r\n", false)]
    [InlineData("subprocess: !!bool yes\r\n", true)]
    public void BindsTaggedScalarsToBooleanProperties(string yaml, bool expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Subprocess);
    }

    [Fact]
    public void RewritingADocumentKeepsTaggedValuesReadable()
    {
        var document = McsYamlReader.Parse("type: !!bool true\r\nsubprocess: !!int 1\r\nstateCode: 1\r\n");
        document["stateCode"] = "0";

        var restored = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(McsYamlWriter.Write(document))!;

        Assert.Equal(1, restored.Type);
        Assert.True(restored.Subprocess);
        Assert.Equal(0, restored.StateCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# nothing\r\n")]
    public void ReturnsNullForEmptyDocuments(string yaml)
    {
        Assert.Null(McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Theory]
    [InlineData("- one\r\n")]
    [InlineData("plain\r\n")]
    public void ThrowsWhenTheDocumentIsNotAMapping(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Fact]
    public void ReadsNullPropertyAsNull()
    {
        Assert.Null(McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>("name: null\r\ntype: 1\r\n")!.Name);
    }

    [Fact]
    public void ReadsQuotedNullPropertyAsText()
    {
        Assert.Equal("null", McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>("name: 'null'\r\n")!.Name);
    }

    [Fact]
    public void RoundTripsNullLikeTextThroughTheWriter()
    {
        var original = new SyncDataverseClient.WorkflowMetadata { Name = "null", Description = "~" };
        var restored = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(McsYamlObjectMapper.Serialize(original));

        Assert.Equal("null", restored!.Name);
        Assert.Equal("~", restored.Description);
    }

    [Fact]
    public void WritesNullPropertiesAsEmptyValues()
    {
        Assert.Contains(LineEndings.ToPlatform("description: \r\n"), McsYamlObjectMapper.Serialize(new SyncDataverseClient.WorkflowMetadata { Name = "Flow" }), StringComparison.Ordinal);
    }

    [Fact]
    public void WritesEmptyConnectionReferencesInFlowStyle()
    {
        Assert.Contains(LineEndings.ToPlatform("connectionReferences: []\r\n"), McsYamlObjectMapper.Serialize(new SyncDataverseClient.WorkflowMetadata { Name = "Flow" }), StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceParserReadsMapperOutput()
    {
        var yaml = McsYamlObjectMapper.Serialize(new SyncDataverseClient.WorkflowMetadata
        {
            Name = "Approval Request (v2)",
            Description = "Sends an approval.\nEscalates after 24h.",
            Subprocess = false,
            IsCustomizable = new SyncDataverseClient.ManagedProperty { Value = true, CanBeChanged = false },
            ConnectionReferences = new List<string> { "shared_office365users" },
        });

        var reference = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<object, object?>>(yaml)!;

        Assert.Equal("Approval Request (v2)", reference["name"]?.ToString());
        Assert.Equal("Sends an approval.\nEscalates after 24h.", reference["description"]?.ToString());
        Assert.True(reference["isCustomizable"] is Dictionary<object, object?>);
        Assert.True(reference["connectionReferences"] is System.Collections.IList);
        Assert.True(bool.TryParse(reference["subprocess"]?.ToString(), out _));
    }
}
