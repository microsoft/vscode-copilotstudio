// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections;
using Microsoft.CopilotStudio.McsCore.Yaml;
using Xunit;
using YamlDotNet.Serialization;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlParityTests
{
    private static readonly IDeserializer Reference = new DeserializerBuilder().Build();

    [Theory]
    [InlineData("  name: test\ntype: test\n")]
    [InlineData("a:\n\tb: 1\n")]
    [InlineData("a: 'unterminated\n")]
    [InlineData("a: \"unterminated\n")]
    [InlineData("a: [1, 2\n")]
    [InlineData("a: {x: 1\n")]
    [InlineData("a: b: c\n")]
    [InlineData("a: ]\n")]
    [InlineData("name: \"\n")]
    [InlineData("list: [\n")]
    public void RejectsEverythingTheReferenceRejects(string yaml)
    {
        Assert.False(ReferenceAccepts(yaml));
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("a: 1\nb: two\n")]
    [InlineData("a: \nb: 1\n")]
    [InlineData("a:\n  b: 1\n")]
    [InlineData("a:\n- x\n- y\n")]
    [InlineData("a:\n  - x\n")]
    [InlineData("a: []\nb: {}\n")]
    [InlineData("a: [1, 2]\n")]
    [InlineData("a: {x: 1}\n")]
    [InlineData("a: 'x: y'\n")]
    [InlineData("a: \"line\\nbreak\"\n")]
    [InlineData("a: >-\n  one\n\n  two\nb: 1\n")]
    [InlineData("a: |-\n  one\n  two\nb: 1\n")]
    [InlineData("# c\na: 1 # trailing\n")]
    [InlineData("---\na: 1\n...\n")]
    [InlineData("a:\n- k: 1\n  j: 2\n")]
    [InlineData("a: 1\r\nb: 2\r\n")]
    [InlineData("a:\n  b:\n    c:\n      d: 1\n")]
    [InlineData("a:\tvalue\n")]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("# nothing\n")]
    public void AcceptsEverythingTheReferenceAccepts(string yaml)
    {
        Assert.True(ReferenceAccepts(yaml));
        McsYamlReader.Parse(yaml);
    }

    [Theory]
    [MemberData(nameof(TagKindMismatches))]
    public void RejectsTagsTheReferenceRejectsForTheNodeKind(string yaml)
    {
        Assert.False(ReferenceAccepts(yaml));
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [MemberData(nameof(TagKindMatches))]
    public void AcceptsTagsTheReferenceAcceptsForTheNodeKind(string yaml)
    {
        Assert.True(ReferenceAccepts(yaml));
        Assert.Equal(Normalize(Reference.Deserialize<object>(yaml)), Normalize(McsYamlReader.Parse(yaml)));
    }

    public static TheoryData<string> TagKindMismatches()
    {
        var data = new TheoryData<string>();
        foreach (var yaml in TagShapes())
        {
            if (!ReferenceAccepts(yaml))
            {
                data.Add(yaml);
            }
        }

        return data;
    }

    public static TheoryData<string> TagKindMatches()
    {
        var data = new TheoryData<string>();
        foreach (var yaml in TagShapes())
        {
            if (ReferenceAccepts(yaml))
            {
                data.Add(yaml);
            }
        }

        return data;
    }

    private static IEnumerable<string> TagShapes()
    {
        foreach (var tag in new[] { "!!str", "!!int", "!!bool", "!!float", "!!map", "!!seq" })
        {
            yield return $"a: {tag} text\n";
            yield return $"a: {tag} 5\n";
            yield return $"a: {tag} [x]\n";
            yield return $"a: {tag} {{k: v}}\n";
            yield return $"a: {tag}\n- x\n";
            yield return $"a: {tag}\n  k: v\n";
            yield return $"a: [{tag} 5]\n";
            yield return $"a: {{k: {tag} 5}}\n";
        }
    }

    [Theory]
    [MemberData(nameof(ReferenceComparableDocuments))]
    public void ProducesSameValuesAsReference(string yaml)
    {
        Assert.Equal(Normalize(Reference.Deserialize<object>(yaml)), Normalize(McsYamlReader.Parse(yaml)));
    }

    [Theory]
    [MemberData(nameof(WriterDocuments))]
    public void ReferenceReadsWhatWeWrite(Dictionary<string, object?> document)
    {
        Assert.Equal(Normalize(document), Normalize(Reference.Deserialize<object>(McsYamlWriter.Write(document))));
    }

    public static TheoryData<string> ReferenceComparableDocuments() => new()
    {
        "jsonFileName: workflows/F-abc/workflow.json\nworkflowId: 4f66c140-e032-f111-88b4-7ced8d3b6119\nname: Agent Flow 1\ntype: 1\ndescription: Version 6 - response.\nsubprocess: false\nstateCode: 0\nstatusCode: 1\nisTransacted: true\nintroducedVersion: 1.0\nisCustomizable:\n  value: true\n  canBeChanged: true\n  managedPropertyLogicalName: iscustomizableanddeletable\nprimaryEntity: none\nconnectionReferences:\n- new_sharedsendmail_9800e\n- cre98_AgentC1.cr.WX3p-EQ4\n",
        "aIModelId: 3b5436b4-d7b4-4389-96e8-107446c9094a\nname: prompt child 1\ntemplateId: edfdb190-3791-45d8-9a6c-8f90a37c278a\n",
        "jsonFileName: \nworkflowId: 00000000-0000-0000-0000-000000000000\nname: \ndescription: \nisCustomizable: \nconnectionReferences: []\n",
        "name: 'Name: with colon'\nother: '#hash'\npadded: '  pad  '\nempty: ''\n",
        "description: >-\n  line1\n\n  line2\n\n  line3\nname: Flow\n",
        "description: |-\n  literal1\n  literal2\nname: Flow\n",
        "items:\n- kind: Http\n  method: POST\n- kind: Timer\n  interval: 5\n",
        "config:\n  nested:\n    deep: value\n  list:\n  - a\n  - b\n",
        "flow: [one, two, three]\nmap: {x: 1, y: 2}\n",
    };

    public static TheoryData<Dictionary<string, object?>> WriterDocuments() => new()
    {
        new Dictionary<string, object?> { ["name"] = "My Flow", ["subprocess"] = "false" },
        new Dictionary<string, object?> { ["name"] = "x", ["description"] = null },
        new Dictionary<string, object?> { ["connectionReferences"] = new List<object?> { "shared_a", "shared_b" } },
        new Dictionary<string, object?> { ["connectionReferences"] = new List<object?>() },
        new Dictionary<string, object?> { ["isCustomizable"] = new Dictionary<string, object?> { ["value"] = "true" } },
        new Dictionary<string, object?> { ["description"] = "line1\nline2" },
        new Dictionary<string, object?> { ["name"] = "Name: with colon" },
        new Dictionary<string, object?> { ["name"] = "#hash", ["padded"] = "  pad  ", ["empty"] = "" },
        new Dictionary<string, object?> { ["name"] = "it's" },
        new Dictionary<string, object?> { ["unicode"] = "é中文" },
        new Dictionary<string, object?> { ["introducedVersion"] = "1.0" },
        new Dictionary<string, object?> { ["tabbed"] = "tab\there" },
    };

    private static bool ReferenceAccepts(string yaml)
    {
        try
        {
            Reference.Deserialize<object>(yaml);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool AreEquivalent(object? left, object? right) => StructurallyEqual(Normalize(left), Normalize(right));

    private static bool StructurallyEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is SortedDictionary<string, object?> leftMap && right is SortedDictionary<string, object?> rightMap)
        {
            if (leftMap.Count != rightMap.Count)
            {
                return false;
            }

            foreach (var entry in leftMap)
            {
                if (!rightMap.TryGetValue(entry.Key, out var other) || !StructurallyEqual(entry.Value, other))
                {
                    return false;
                }
            }

            return true;
        }

        if (left is List<object?> leftList && right is List<object?> rightList)
        {
            return leftList.Count == rightList.Count && !leftList.Where((item, index) => !StructurallyEqual(item, rightList[index])).Any();
        }

        return left is string leftText && right is string rightText ? string.Equals(leftText, rightText, StringComparison.Ordinal) : Equals(left, right);
    }

    internal static object? Normalize(object? value)
    {
        if (value is string text)
        {
            return text;
        }

        if (value is IDictionary map)
        {
            var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in map)
            {
                normalized[entry.Key?.ToString() ?? string.Empty] = Normalize(entry.Value);
            }

            return normalized;
        }

        if (value is IEnumerable sequence)
        {
            return sequence.Cast<object?>().Select(Normalize).ToList();
        }

        return value?.ToString();
    }
}
