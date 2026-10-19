// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlCorpusTests
{
    private const string CorpusRootVariable = "MCS_YAML_CORPUS_ROOT";

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void MatchesReferenceValuesForFixture(string name, string content)
    {
        var reference = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(content);

        Assert.True(
            McsYamlParityTests.AreEquivalent(reference, McsYamlReader.Parse(content)),
            $"{name} did not match the reference parser.");
    }

    [Fact]
    public void RewritesCorpusDocumentsWithoutChangingContent()
    {
        var failures = new List<string>();
        var checkedFiles = 0;

        foreach (var (file, raw) in CorpusDocuments())
        {
            var content = raw;
            if (IsJsonDocument(content))
            {
                continue;
            }

            checkedFiles++;

            try
            {
                var rewritten = McsYamlWriter.Write(McsYamlReader.Parse(content));

                if (!LineEndings.ArePlatformNative(rewritten))
                {
                    failures.Add($"{file} :: rewritten using line endings that are not native to this platform.");
                    continue;
                }

                if (string.Equals(LineEndings.ToLf(content), LineEndings.ToLf(rewritten), StringComparison.Ordinal))
                {
                    continue;
                }

                if (!UsesBlockScalar(content))
                {
                    failures.Add($"{file} :: rewrite differs from the original without a block scalar to explain it.");
                    continue;
                }

                if (!McsYamlParityTests.AreEquivalent(McsYamlReader.Parse(content), McsYamlReader.Parse(rewritten)))
                {
                    failures.Add($"{file} :: rewriting changed the values.");
                }

                if (!string.Equals(rewritten, McsYamlWriter.Write(McsYamlReader.Parse(rewritten)), StringComparison.Ordinal))
                {
                    failures.Add($"{file} :: rewriting is not idempotent, so the file would churn on every sync.");
                }
            }
            catch (McsYamlFormatException exception)
            {
                failures.Add($"{file} :: {exception.Message}");
            }
        }

        Assert.Empty(failures);
        Assert.True(checkedFiles > 0, "The corpus contained no block-YAML documents to rewrite.");
    }

    private static bool UsesBlockScalar(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r', ' ');
            if (trimmed.EndsWith("|", StringComparison.Ordinal) || trimmed.EndsWith("|-", StringComparison.Ordinal) || trimmed.EndsWith("|+", StringComparison.Ordinal)
                || trimmed.EndsWith(">", StringComparison.Ordinal) || trimmed.EndsWith(">-", StringComparison.Ordinal) || trimmed.EndsWith(">+", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void MatchesReferenceValuesForCorpusDocuments()
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
        var failures = new List<string>();

        foreach (var (file, content) in CorpusDocuments())
        {
            object? reference;
            try
            {
                reference = deserializer.Deserialize<object>(content);
            }
            catch
            {
                if (TryParse(content, out _))
                {
                    failures.Add($"{file} :: the reference rejects this document but the reader accepts it.");
                }

                continue;
            }

            if (!TryParse(content, out var actual))
            {
                failures.Add($"{file} :: the reference accepts this document but the reader rejects it.");
                continue;
            }

            if (!McsYamlParityTests.AreEquivalent(reference, actual))
            {
                failures.Add(file);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(4)));
    }

    private static bool IsJsonDocument(string content) => content.TrimStart(' ', '\r', '\n').StartsWith("{", StringComparison.Ordinal);

    private static bool TryParse(string content, out Dictionary<string, object?> document)
    {
        try
        {
            document = McsYamlReader.Parse(content);
            return true;
        }
        catch (McsYamlFormatException)
        {
            document = new Dictionary<string, object?>(StringComparer.Ordinal);
            return false;
        }
    }

    [Fact]
    public void MatchesReferenceObjectMapperRoundTripForRealWorkflowMetadata()
    {
        AssertObjectMapperMatchesReference<SyncDataverseClient.WorkflowMetadata>(
            "workflows",
            builder => builder.WithAttributeOverride<SyncDataverseClient.WorkflowMetadata>(metadata => metadata.ClientData!, new YamlDotNet.Serialization.YamlIgnoreAttribute()),
            content => McsYamlObjectMapper.DeserializeStrict<SyncDataverseClient.WorkflowMetadata>(content));
    }

    [Fact]
    public void MatchesReferenceObjectMapperRoundTripForRealPromptMetadata()
    {
        AssertObjectMapperMatchesReference<SyncDataverseClient.AIPromptMetadata>(
            "prompts",
            builder => builder
                .WithAttributeOverride<SyncDataverseClient.AIPromptMetadata>(metadata => metadata.CustomConfiguration!, new YamlDotNet.Serialization.YamlIgnoreAttribute())
                .WithAttributeOverride<SyncDataverseClient.AIPromptMetadata>(metadata => metadata.IsUnreadableReferencePlaceholder, new YamlDotNet.Serialization.YamlIgnoreAttribute()),
            content => McsYamlObjectMapper.Deserialize<SyncDataverseClient.AIPromptMetadata>(content));
    }

    private static void AssertObjectMapperMatchesReference<TMetadata>(
        string folderName,
        Func<YamlDotNet.Serialization.SerializerBuilder, YamlDotNet.Serialization.SerializerBuilder> configureReference,
        Func<string, TMetadata?> deserialize)
        where TMetadata : class, new()
    {
        var referenceDeserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var referenceSerializer = configureReference(new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)).Build();
        var failures = new List<string>();
        var checkedFiles = 0;

        foreach (var (file, content) in CorpusDocuments())
        {
            if (("/" + file.Replace('\\', '/')).IndexOf($"/{folderName}/", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (IsJsonDocument(content))
            {
                continue;
            }

            TMetadata? reference;
            try
            {
                reference = referenceDeserializer.Deserialize<TMetadata>(content);
            }
            catch (Exception exception)
            {
                failures.Add($"{file} :: the reference deserializer rejected this document :: {exception.Message}");
                continue;
            }

            if (reference == null)
            {
                continue;
            }

            checkedFiles++;

            try
            {
                var actual = deserialize(content);
                Assert.NotNull(actual);

                var referenceText = referenceSerializer.Serialize(reference).Replace("\r\n", "\n");
                var actualText = McsYamlObjectMapper.Serialize(actual!).Replace("\r\n", "\n");
                if (string.Equals(referenceText, actualText, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!UsesBlockScalar(referenceText))
                {
                    failures.Add($"{file} :: {DescribeFirstDifference(referenceText, actualText)}");
                    continue;
                }

                var plain = new YamlDotNet.Serialization.DeserializerBuilder().Build();
                if (!McsYamlParityTests.AreEquivalent(plain.Deserialize<object>(referenceText), plain.Deserialize<object>(actualText)))
                {
                    failures.Add($"{file} :: the two serializations do not carry the same values :: {DescribeFirstDifference(referenceText, actualText)}");
                }
            }
            catch (McsYamlFormatException exception)
            {
                failures.Add($"{file} :: {exception.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(4)));
        Assert.True(checkedFiles > 0, $"The corpus contained no '{folderName}' metadata documents.");
    }

    private static string DescribeFirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');

        for (var line = 0; line < Math.Max(expectedLines.Length, actualLines.Length); line++)
        {
            var expectedLine = line < expectedLines.Length ? expectedLines[line] : "<none>";
            var actualLine = line < actualLines.Length ? actualLines[line] : "<none>";
            if (!string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                return $"line {line + 1}: expected=[{expectedLine}] actual=[{actualLine}]";
            }
        }

        return "lengths differ";
    }

    private static IEnumerable<(string Name, string Content)> CorpusDocuments()
    {
        foreach (var fixture in Fixtures())
        {
            yield return ((string)fixture[0]!, LineEndings.ToPlatform((string)fixture[1]!));
        }

        foreach (var file in EnumerateCorpusFiles())
        {
            yield return (file, File.ReadAllText(file).TrimStart('\uFEFF'));
        }
    }

    private static IEnumerable<string> EnumerateCorpusFiles()
    {
        var root = Environment.GetEnvironmentVariable(CorpusRootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Array.Empty<string>();
        }

        Assert.True(Directory.Exists(root), $"{CorpusRootVariable} is set to '{root}' but that folder does not exist.");

        var files = Directory.EnumerateFiles(root, "metadata.yml", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count > 0, $"{CorpusRootVariable} is set to '{root}' but no metadata.yml files were found under it.");

        return files;
    }

    public static TheoryData<string, string> Fixtures() => new()
    {
        {
            "workflows/workflow-typical",
            "jsonFileName: workflows/AgentFlow1-4f66c140-e032-f111-88b4-7ced8d3b6119/workflow.json\r\n" +
            "workflowId: 4f66c140-e032-f111-88b4-7ced8d3b6119\r\n" +
            "name: Agent Flow 1\r\n" +
            "type: 1\r\n" +
            "description: Version 6 - When an agent calls the flow and send back a response.\r\n" +
            "subprocess: false\r\n" +
            "category: 5\r\n" +
            "mode: 0\r\n" +
            "scope: 4\r\n" +
            "onDemand: false\r\n" +
            "triggerOnCreate: false\r\n" +
            "triggerOnDelete: false\r\n" +
            "asyncAutodelete: false\r\n" +
            "syncWorkflowLogOnFailure: false\r\n" +
            "stateCode: 0\r\n" +
            "statusCode: 1\r\n" +
            "runAs: 1\r\n" +
            "isTransacted: true\r\n" +
            "introducedVersion: 1.0\r\n" +
            "isCustomizable:\r\n" +
            "  value: true\r\n" +
            "  canBeChanged: true\r\n" +
            "  managedPropertyLogicalName: iscustomizableanddeletable\r\n" +
            "businessProcessType: 0\r\n" +
            "isCustomProcessingStepAllowedForOtherPublishers:\r\n" +
            "  value: true\r\n" +
            "  canBeChanged: true\r\n" +
            "  managedPropertyLogicalName: canbedeleted\r\n" +
            "modernFlowType: 1\r\n" +
            "primaryEntity: none\r\n" +
            "connectionReferences:\r\n" +
            "- new_sharedsendmail_9800e\r\n" +
            "- cre98_AgentCADStandardsChatbot.shared_sharepointonline.46c36e9a88ee4c3eb4c6406b22672b46\r\n" +
            "- cre98_AgentA1.shared_commondataserviceforapps.787379aebbb2450a96f20e20d71bfd30\r\n" +
            "- cre98_AgentC1.cr.WX3p-EQ4\r\n"
        },
        {
            "workflows/workflow-nulls",
            "jsonFileName: \r\n" +
            "workflowId: 00000000-0000-0000-0000-000000000000\r\n" +
            "name: \r\n" +
            "type: \r\n" +
            "description: \r\n" +
            "subprocess: \r\n" +
            "isCustomizable: \r\n" +
            "primaryEntity: \r\n" +
            "connectionReferences: []\r\n"
        },
        {
            "prompts/prompt",
            "aIModelId: 3b5436b4-d7b4-4389-96e8-107446c9094a\r\n" +
            "name: prompt child 1\r\n" +
            "templateId: edfdb190-3791-45d8-9a6c-8f90a37c278a\r\n"
        },
        {
            "workflows/workflow-quoted",
            "name: 'Name: with colon'\r\n" +
            "jsonFileName: '#hash'\r\n" +
            "primaryEntity: '  padded  '\r\n" +
            "description: ''\r\n" +
            "connectionReferences:\r\n" +
            "- shared_only\r\n"
        },
        {
            "workflows/workflow-multiline",
            "name: Approval Request\r\n" +
            "description: \"Sends an approval.\\nEscalates after 24h.\"\r\n" +
            "stateCode: 1\r\n"
        },
        {
            "documents/deep",
            "name: Deep\r\n" +
            "config:\r\n" +
            "  outer:\r\n" +
            "    inner:\r\n" +
            "      deepest: value\r\n" +
            "    sibling: other\r\n" +
            "  flag: true\r\n"
        },
        {
            "documents/list-of-maps",
            "name: Structured\r\n" +
            "triggers:\r\n" +
            "- kind: Http\r\n" +
            "  method: POST\r\n" +
            "- kind: Timer\r\n" +
            "  interval: 5\r\n"
        },
        {
            "workflows/workflow-unicode",
            "name: Flow é中文\r\n" +
            "description: Ünïcödé désçription\r\n"
        },
        {
            "workflows/workflow-block-scalar",
            "name: Hand Edited\r\n" +
            "description: |-\r\n" +
            "  A single line written as a literal block scalar.\r\n" +
            "stateCode: 1\r\n"
        },
        {
            "workflows/workflow-negative-numbers",
            "name: Negative\r\n" +
            "type: -1\r\n" +
            "category: -5\r\n" +
            "stateCode: 0\r\n"
        },
        {
            "prompts/prompt-null-template",
            "aIModelId: 3b5436b4-d7b4-4389-96e8-107446c9094a\r\n" +
            "name: prompt child 2\r\n" +
            "templateId: \r\n"
        },
    };
}


