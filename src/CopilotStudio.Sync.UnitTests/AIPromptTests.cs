// Copyright (C) Microsoft Corporation. All rights reserved.

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.Sync;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AIPromptTests
{
    private static readonly string TestDataResourcePrefix =
        typeof(AIPromptTests).Assembly.GetName().Name + ".TestData.AIPrompts.";

    private static string ReadFixture(string fileName)
    {
        var resourceName = TestDataResourcePrefix + fileName;
        using var stream = typeof(AIPromptTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("text")]
    [InlineData("finishReason")]
    [InlineData("structuredOutput")]
    [InlineData("current_date")]
    [InlineData("TenCharacters_abc")]
    [InlineData("MixedNameAnd1234_ab23")]
    [InlineData("ThisisverylongnameandlongasWewant")]
    public void ProcessAIPromptOutputName_ValidIdentifier_ReturnsUnchanged(string name)
    {
        Assert.Equal(name, WorkspaceSynchronizer.ProcessAIPromptOutputName(name));
    }

    [Theory]
    [InlineData("Container 1 Registration Number", "Containe2e5985a0512b7b685a45b21ef6350c0d")]
    [InlineData("Due-Date", "Due_002Dda5f9e59e67c82f09c296caa2bfca354")]
    [InlineData("date description", "date_002da27979fcb9686b5b8261c3d2a79ec84")]
    [InlineData("shiping method", "shiping_588f41922a4dc30d2d1c4654fd7b6fd7")]
    public void ProcessAIPromptOutputName_InvalidIdentifier_MatchesCloudMangling(string raw, string expected)
    {
        Assert.Equal(expected, WorkspaceSynchronizer.ProcessAIPromptOutputName(raw));
    }

    [Fact]
    public void ProcessAIPromptOutputName_OutputAlwaysPrefix8PlusHash32()
    {
        var result = WorkspaceSynchronizer.ProcessAIPromptOutputName("hello world");
        Assert.Equal(40, result.Length);
    }

    [Fact]
    public void ProcessAIPromptOutputName_NonAlphanumericEncodedAsXmlStyle()
    {
        var result = WorkspaceSynchronizer.ProcessAIPromptOutputName("A-B");
        Assert.StartsWith("A_002DB_", result);
        Assert.Equal(40, result.Length);
    }

    [Fact]
    public void ProcessAIPromptOutputName_LeadingDigit_GetsMangled()
    {
        var result = WorkspaceSynchronizer.ProcessAIPromptOutputName("1foo");
        Assert.NotEqual("1foo", result);
        Assert.Equal(40, result.Length);
    }

    [Fact]
    public void ProcessAIPromptOutputName_EmptyOrNull_ReturnsAsIs()
    {
        Assert.Equal(string.Empty, WorkspaceSynchronizer.ProcessAIPromptOutputName(string.Empty));
    }

    [Fact]
    public void ProcessAIPromptOutputName_PrefixUsesUppercaseHexForCharCodes()
    {
        var result = WorkspaceSynchronizer.ProcessAIPromptOutputName("shiping method");
        Assert.StartsWith("shiping_", result);
    }

    [Fact]
    public void ProcessAIPromptOutputName_HashIsLowercase()
    {
        var result = WorkspaceSynchronizer.ProcessAIPromptOutputName("date description");
        var hashPart = result.Substring(8);
        Assert.Matches("^[0-9a-f]{32}$", hashPart);
    }

    [Fact]
    public void ExtractTrailingGuidFromFileName_ValidGuidSuffix_Parses()
    {
        var id = WorkspaceSynchronizer.ExtractTrailingGuidFromFileName(
            "Prompt1-2fcc84f9-b852-496a-8b53-f571ccbf74c0");
        Assert.NotNull(id);
        Assert.Equal(Guid.Parse("2fcc84f9-b852-496a-8b53-f571ccbf74c0"), id);
    }

    [Fact]
    public void ExtractTrailingGuidFromFileName_NoGuid_ReturnsNull()
    {
        Assert.Null(WorkspaceSynchronizer.ExtractTrailingGuidFromFileName("Prompt1"));
    }

    [Fact]
    public void ExtractTrailingGuidFromFileName_GuidNotAtEnd_ReturnsNull()
    {
        Assert.Null(WorkspaceSynchronizer.ExtractTrailingGuidFromFileName(
            "2fcc84f9-b852-496a-8b53-f571ccbf74c0-Prompt1"));
    }

    [Fact]
    public void TryReadPromptName_ReadsTopLevelName()
    {
        var json = ReadFixture("SummarizeDocument1.prompt.json");
        Assert.Equal("summarize document 1", WorkspaceSynchronizer.TryReadPromptName(json));
    }

    [Fact]
    public void TryReadPromptName_MissingNameReturnsNull()
    {
        Assert.Null(WorkspaceSynchronizer.TryReadPromptName("{}"));
    }

    [Fact]
    public void TryReadPromptName_InvalidJsonReturnsNull()
    {
        Assert.Null(WorkspaceSynchronizer.TryReadPromptName("not json"));
    }

    [Fact]
    public void BuildCustomConfigurationFromPromptJson_PortalShape_ProducesRawShape()
    {
        var portal = ReadFixture("SummarizeDocument1.prompt.json");
        var raw = WorkspaceSynchronizer.BuildCustomConfigurationFromPromptJson(portal);

        using var doc = JsonDocument.Parse(raw);
        Assert.True(doc.RootElement.TryGetProperty("prompt", out var promptArr));
        Assert.Equal(JsonValueKind.Array, promptArr.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("definitions", out var defs));
        Assert.Equal(JsonValueKind.Object, defs.ValueKind);
        Assert.True(defs.TryGetProperty("inputs", out _));
        Assert.True(defs.TryGetProperty("output", out _));
    }

    [Fact]
    public void BuildCustomConfigurationFromPromptJson_AlreadyRawShape_PassesThrough()
    {
        var raw = "{\"prompt\":[{\"type\":\"literal\",\"text\":\"hi\"}],\"definitions\":{}}";
        var result = WorkspaceSynchronizer.BuildCustomConfigurationFromPromptJson(raw);
        Assert.Equal(raw, result);
    }

    [Fact]
    public void BuildCustomConfigurationFromPromptJson_InstructionPlaceholders_BecomeInputVariables()
    {
        var portal = "{\"instruction\":\"hi {{name}}\",\"model\":\"gpt-41-mini\"}";
        var raw = WorkspaceSynchronizer.BuildCustomConfigurationFromPromptJson(portal);

        using var doc = JsonDocument.Parse(raw);
        var prompt = doc.RootElement.GetProperty("prompt");
        Assert.Equal(2, prompt.GetArrayLength());

        var literal = prompt[0];
        Assert.Equal("literal", literal.GetProperty("type").GetString());
        Assert.Equal("hi ", literal.GetProperty("text").GetString());

        var input = prompt[1];
        Assert.Equal("inputVariable", input.GetProperty("type").GetString());
        Assert.Equal("name", input.GetProperty("id").GetString());
    }

    [Fact]
    public void BuildPromptJson_RoundTripFromRawConfig_ProducesPortalShape()
    {
        var portal = ReadFixture("Prompt1.prompt.json");
        var raw = WorkspaceSynchronizer.BuildCustomConfigurationFromPromptJson(portal);
        var portal2 = WorkspaceSynchronizer.BuildPromptJson("Prompt 1", raw);

        using var doc = JsonDocument.Parse(portal2);
        var root = doc.RootElement;
        Assert.Equal("Prompt 1", root.GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("instruction", out _));
        Assert.True(root.TryGetProperty("inputs", out _));
        Assert.True(root.TryGetProperty("output", out _));
        Assert.True(root.TryGetProperty("model", out _));
    }

    [Fact]
    public void BuildPromptJson_InvalidJson_ReturnsAsIs()
    {
        var result = WorkspaceSynchronizer.BuildPromptJson("name", "not json");
        Assert.Equal("not json", result);
    }

    [Fact]
    public void ExtractAIPromptIO_Prompt1Fixture()
    {
        var portal = ReadFixture("Prompt1.prompt.json");
        var raw = WorkspaceSynchronizer.BuildCustomConfigurationFromPromptJson(portal);

        Assert.Equal(
            "Containe2e5985a0512b7b685a45b21ef6350c0d",
            WorkspaceSynchronizer.ProcessAIPromptOutputName("Container 1 Registration Number"));

        Assert.Contains("Container 1 Registration Number", raw);
    }

    [Fact]
    public void ExtractAIPromptIO_Prompt1FixtureWithReferences()
    {
        var portal = ReadFixture("Prompt1.prompt.json");
        var raw = WorkspaceSynchronizer.BuildCustomConfigurationFromPromptJson(portal);
        var (_, outputType) = WorkspaceSynchronizer.ExtractAIPromptIO(raw);
        Assert.NotNull(outputType);

        AssertPathExists(outputType!,
            "predictionOutput", "finishReason");
        AssertPathExists(outputType!,
            "predictionOutput", "structuredOutput", "current_date");
        AssertPathExists(outputType!,
            "predictionOutput", "structuredOutput", "extracted_data",
            "Containe2e5985a0512b7b685a45b21ef6350c0d");
        AssertPathExists(outputType!,
            "predictionOutput", "structuredOutput", "extracted_data",
            "Containe2e5985a0512b7b685a45b21ef6350c0d", "type");
        AssertPathExists(outputType!,
            "predictionOutput", "structuredOutput", "extracted_data",
            "Containe2030b08f6814c27546e77f3d00838c46", "value");
        AssertPathExists(outputType!,
            "predictionOutput", "structuredOutput", "extracted_data",
            "Containedb50d462a3a8737c30046a70698b7708", "value");
        AssertPathExists(outputType!,
            "predictionOutput", "structuredOutput", "extracted_data",
            "Containec5acaadababe6c22161a0f793cbf7dfc", "value");
    }

    [Fact]
    public void ExtractAIPromptIO_SummarizeFixture()
    {
        var portal = ReadFixture("SummarizeDocument1.prompt.json");
        var raw = WorkspaceSynchronizer.BuildCustomConfigurationFromPromptJson(portal);

        Assert.Contains("shiping method", raw);
        Assert.Contains("date description", raw);

        Assert.Equal("Quality", WorkspaceSynchronizer.ProcessAIPromptOutputName("Quality"));
        Assert.Equal("Unit_Price", WorkspaceSynchronizer.ProcessAIPromptOutputName("Unit_Price"));
    }

    [Fact]
    public void ExtractAIPromptIO_SummarizeFixtureWithReferences()
    {
        var portal = ReadFixture("SummarizeDocument1.prompt.json");
        var raw = WorkspaceSynchronizer.BuildCustomConfigurationFromPromptJson(portal);
        var (_, outputType) = WorkspaceSynchronizer.ExtractAIPromptIO(raw);
        Assert.NotNull(outputType);

        AssertPathExists(outputType!,
            "predictionOutput", "structuredOutput", "success");

        var summaryResult = WalkPath(outputType!,
            "predictionOutput", "structuredOutput", "summaryResult");
        var table = Assert.IsType<TableDataType>(summaryResult);

        Assert.True(table.Properties.ContainsKey("shiping_588f41922a4dc30d2d1c4654fd7b6fd7"));
        Assert.True(table.Properties.ContainsKey("date_002da27979fcb9686b5b8261c3d2a79ec84"));
    }

    private static void AssertPathExists(RecordDataType root, params string[] path)
    {
        var node = WalkPath(root, path);
        Assert.NotNull(node);
    }

    private static DataType WalkPath(RecordDataType root, params string[] path)
    {
        DataType current = root;
        for (int i = 0; i < path.Length; i++)
        {
            var segment = path[i];
            if (current is RecordDataType record)
            {
                Assert.True(record.Properties.ContainsKey(segment),
                    $"Missing property '{segment}' at path '{string.Join(".", path, 0, i + 1)}'");
                current = record.Properties[segment].Type!;
            }
            else if (current is TableDataType table)
            {
                Assert.True(table.Properties.ContainsKey(segment),
                    $"Missing column '{segment}' at path '{string.Join(".", path, 0, i + 1)}'");
                current = table.Properties[segment].Type!;
            }
            else
            {
                Assert.Fail($"Cannot descend into non-record/table at path '{string.Join(".", path, 0, i)}'");
            }
        }
        return current!;
    }
}
