using System.Text;
using System.Text.Json;
using Microsoft.CopilotStudio.McsCore.Yaml;
using Microsoft.CopilotStudio.Sync.Copy;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

public class CopyTextEditsTests
{
    public static TheoryData<string, string> ReplacementsForEveryQuoteStyle
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var original in new[] { "Old", "'Old'", "\"Old\"" })
            {
                foreach (var replacement in new[] { "Line\nbreak", "Tab\there", "Start\u0001", "It's", "Say \"hi\"", "Back\\slash", "Support, East", "[a]", "Team {b}" })
                {
                    data.Add(original, replacement);
                }
            }

            return data;
        }
    }

    [Fact]
    public void RewriteMcsYaml_KeepsQuoteStylesAndEverythingElse()
    {
        var text = "kind: AdaptiveDialog\nschemaName: 'cr1_a.topic.Quoted'\nother: \"cr1_a.topic.Double\"\nplain: cr1_a.topic.Plain\nflowIds:\n  - cr1_a.topic.SeqA\n  - 'cr1_a.topic.SeqB'\noutputs:\n  - propertyName: @odata.etag\n";

        var rewritten = CopyTextEdits.RewriteMcsYaml(text, scalar => scalar.Value.StartsWith("cr1_a.", StringComparison.Ordinal) ? "cr1_b." + scalar.Value.Substring("cr1_a.".Length) : null);

        Assert.Equal(text.Replace("cr1_a.", "cr1_b.", StringComparison.Ordinal), rewritten);
    }

    [Fact]
    public void VisitYaml_ReportsTheOwningKeyAndWhetherTheScalarIsAtTheRootOrInASequence()
    {
        var text = "kind: TaskDialog\naction:\n  kind: InvokeFlowTaskAction\n  flowId: 94945b98-fe8c-f111-8077-7ced8d702753\nconnectionReferences:\n  - cr1_a.cr.teams\n";
        var visited = new List<CopyYamlScalar>();

        CopyTextEdits.VisitYaml("actions/Flow.mcs.yml", text, visited.Add);

        Assert.Contains(visited, scalar => scalar.Key == "kind" && scalar.Value == "TaskDialog" && scalar.AtRoot && !scalar.InSequence);
        Assert.Contains(visited, scalar => scalar.Key == "flowId" && scalar.Value == "94945b98-fe8c-f111-8077-7ced8d702753" && !scalar.AtRoot && !scalar.InSequence);
        Assert.Contains(visited, scalar => scalar.Key == "connectionReferences" && scalar.Value == "cr1_a.cr.teams" && scalar.AtRoot && scalar.InSequence);
    }

    [Fact]
    public void RewriteYaml_ReadsComponentFilesLenientlyAndOverlaysStrictly()
    {
        var text = "kind: TaskDialog\npropertyName: @odata.etag\nname: cr1_a.x\n";

        Assert.Equal("kind: TaskDialog\npropertyName: @odata.etag\nname: cr1_b.x\n", CopyTextEdits.RewriteYaml("topics/Flow.mcs.yml", text, scalar => scalar.Key == "name" ? "cr1_b.x" : null));
        Assert.Throws<McsYamlFormatException>(() => CopyTextEdits.RewriteYaml("infrastructure/connections/x.sync.yaml", text, _ => null));
    }

    [Fact]
    public void RewriteMcsYaml_RejectsMergeConflictMarkers()
    {
        var text = "kind: TaskDialog\n<<<<<<< HEAD\nname: one\n=======\nname: two\n>>>>>>> branch\n";

        Assert.ThrowsAny<Exception>(() => CopyTextEdits.RewriteMcsYaml(text, _ => null));
    }

    [Fact]
    public void RewriteYaml_QuotesAReplacementThatNeedsIt()
    {
        var rewritten = CopyTextEdits.RewriteYaml("name: old\nkeep: value\n", scalar => scalar.Key == "name" ? "needs: quoting # really" : null);

        var parsed = McsYamlReader.Parse(rewritten);
        Assert.Equal("needs: quoting # really", parsed["name"]);
        Assert.Equal("value", parsed["keep"]);
    }

    [Theory]
    [MemberData(nameof(ReplacementsForEveryQuoteStyle))]
    public void RewriteYaml_WritesAReplacementThatReadsBackExactlyInBlockAndFlowMappings(string original, string replacement)
    {
        var block = McsYamlReader.Parse(CopyTextEdits.RewriteYaml($"name: {original}\nkeep: value\n", scalar => scalar.Key == "name" ? replacement : null));
        var flow = McsYamlReader.Parse(CopyTextEdits.RewriteYaml($"{{name: {original}, other: x}}\n", scalar => scalar.Key == "name" ? replacement : null));

        Assert.Equal(2, block.Count);
        Assert.Equal(replacement, block["name"]);
        Assert.Equal("value", block["keep"]);
        Assert.Equal(2, flow.Count);
        Assert.Equal(replacement, flow["name"]);
        Assert.Equal("x", flow["other"]);
    }

    [Fact]
    public void RewriteYaml_LeavesBlockScalarsAlone()
    {
        var text = "name: |\n  cr1_a.x\nother: cr1_a.y\n";

        var rewritten = CopyTextEdits.RewriteYaml(text, scalar => scalar.Value.StartsWith("cr1_a.", StringComparison.Ordinal) ? "cr1_b." + scalar.Value.Substring("cr1_a.".Length).TrimEnd() : null);

        Assert.Equal("name: |\n  cr1_a.x\nother: cr1_b.y\n", rewritten);
    }

    [Fact]
    public void RewriteJson_ChangesOnlyTargetedValuesAndKeepsTheByteOrderMark()
    {
        var original = WithPreamble("{\n  \"connectionReferences\": {\n    \"shared_teams\": { \"connection\": { \"connectionReferenceLogicalName\": \"old\" } }\n  },\n  \"name\": \"old\"\n}\n");

        var rewritten = CopyTextEdits.RewriteJson(original, json => !json.IsPropertyName && json.OwnerProperty == "connectionReferenceLogicalName" ? "new" : null);

        Assert.Equal(WithPreamble("{\n  \"connectionReferences\": {\n    \"shared_teams\": { \"connection\": { \"connectionReferenceLogicalName\": \"new\" } }\n  },\n  \"name\": \"old\"\n}\n"), rewritten);
    }

    [Fact]
    public void RewriteJson_EscapesTheReplacement()
    {
        var rewritten = CopyTextEdits.RewriteJson(Encoding.UTF8.GetBytes("{\"name\":\"old\"}"), json => json.IsPropertyName ? null : "say \"hi\" \\ there");

        using var document = JsonDocument.Parse(rewritten);
        Assert.Equal("say \"hi\" \\ there", document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void RewriteJson_ReportsPropertyNamesAndDepth()
    {
        var visited = new List<CopyJsonString>();

        CopyTextEdits.RewriteJson(Encoding.UTF8.GetBytes("{\"outer\":{\"inner\":\"value\"}}"), json =>
        {
            visited.Add(json);
            return null;
        });

        Assert.Contains(visited, json => json.IsPropertyName && json.Value == "inner");
        Assert.Contains(visited, json => !json.IsPropertyName && json.OwnerProperty == "inner" && json.Value == "value" && json.Depth == 2);
    }

    [Fact]
    public void RemoveRootProperty_RemovesOnlyARootScalarLine()
    {
        Assert.Equal("a: 1\nnested:\n  publishedOn: keep\n", CopyTextEdits.RemoveRootProperty("a: 1\npublishedOn: 2026-01-01T00:00:00Z\nnested:\n  publishedOn: keep\n", "publishedOn"));
        Assert.Equal("a: 1\r\nb: 2\r\n", CopyTextEdits.RemoveRootProperty("a: 1\r\npublishedOn: 2026-01-01T00:00:00Z\r\nb: 2\r\n", "publishedOn"));
        Assert.Equal("publishedOn: |\n  block\n", CopyTextEdits.RemoveRootProperty("publishedOn: |\n  block\n", "publishedOn"));
        Assert.Equal("publishedOnLater: x\n", CopyTextEdits.RemoveRootProperty("publishedOnLater: x\n", "publishedOn"));
    }

    [Fact]
    public void Preamble_IsDetectedAndStrippedOnlyAtTheStart()
    {
        var withPreamble = WithPreamble("{}");
        var embedded = Encoding.UTF8.GetBytes("{}").Concat(Encoding.UTF8.GetPreamble()).ToArray();

        Assert.True(CopyTextEdits.HasPreamble(withPreamble));
        Assert.Equal(Encoding.UTF8.GetBytes("{}"), CopyTextEdits.WithoutPreamble(withPreamble).ToArray());
        Assert.False(CopyTextEdits.HasPreamble(embedded));
        Assert.Equal(embedded, CopyTextEdits.WithoutPreamble(embedded).ToArray());
        Assert.False(CopyTextEdits.HasPreamble(Array.Empty<byte>()));
    }

    private static byte[] WithPreamble(string text) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
}
