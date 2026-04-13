// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// Validates the full push round-trip: clone → edit topic → push → pull → verify.
/// Confirms that local edits survive the round-trip through Dataverse and that
/// VerifyPushAsync passes after the push.
/// </summary>
[Collection(LiveTenantCollection.Name)]
public sealed class PushRoundTripTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TempWorkspace _workspace;
    private readonly TempWorkspace _verifyWorkspace;

    public PushRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = new TempWorkspace();
        _verifyWorkspace = new TempWorkspace();
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _verifyWorkspace.Dispose();
    }

    [LiveTenantFact]
    public async Task EditTopicPushPullVerifyRoundTrip()
    {
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        // 1. Clone
        var cloneResult = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_workspace.Path}\"");
        _output.WriteLine("CLONE STDOUT:\n" + cloneResult.Stdout);
        Assert.Equal(0, cloneResult.ExitCode);

        // 2. Edit a topic — modify actual dialog content (not mcs.metadata, which is
        //    stripped by StripMetaInfo before comparison). The sync library compares
        //    RootElement content, so we must change text within the dialog definition.
        var topicsDir = Path.Combine(_workspace.Path, "topics");
        Assert.True(Directory.Exists(topicsDir), "Clone must produce a topics/ directory");

        var topicFiles = Directory.GetFiles(topicsDir, "*.mcs.yml", SearchOption.AllDirectories);
        Assert.True(topicFiles.Length > 0, "Agent must have at least one topic to test push round-trip");

        // Find a topic with a SendActivity containing text we can safely modify
        string? targetTopic = null;
        string? originalContent = null;
        string? editedContent = null;
        var testTag = $"F3-{Guid.NewGuid().ToString("N")[..8]}";

        foreach (var topicFile in topicFiles)
        {
            var content = await File.ReadAllTextAsync(topicFile);

            // Look for a text value inside an activity (dialog content, not metadata).
            // Pattern: a YAML list item under activity.text, e.g.:
            //   text:
            //     - Hello, I'm {System.Bot.Name}. How can I help?
            var match = Regex.Match(content, @"(        - )(.+)$", RegexOptions.Multiline);
            if (match.Success)
            {
                targetTopic = topicFile;
                originalContent = content;
                // Append test tag to the text value
                editedContent = content.Substring(0, match.Groups[2].Index + match.Groups[2].Length)
                    + $" [{testTag}]"
                    + content.Substring(match.Groups[2].Index + match.Groups[2].Length);
                break;
            }
        }

        Assert.True(targetTopic != null && editedContent != null,
            "Agent must have a topic with SendActivity text to test push round-trip");

        var topicName = Path.GetFileName(targetTopic!);
        await File.WriteAllTextAsync(targetTopic, editedContent!);
        _output.WriteLine($"Edited topic: {topicName}");
        _output.WriteLine($"Test tag: [{testTag}]");

        // 3. Push — should detect the change, push it, and verify
        var pushResult = await CliRunner.RunAsync(
            $"push --workspace \"{_workspace.Path}\"");
        _output.WriteLine("PUSH STDOUT:\n" + pushResult.Stdout);
        _output.WriteLine("PUSH STDERR:\n" + pushResult.Stderr);

        Assert.Equal(0, pushResult.ExitCode);
        // Push must report changes (not "no local changes")
        Assert.DoesNotContain("No local changes detected.", pushResult.Stdout);
        // Push includes verify — must pass
        Assert.Contains("Push verification passed", pushResult.Stdout);

        // 4. Pull — should succeed with no further remote changes
        var pullResult = await CliRunner.RunAsync(
            $"pull --workspace \"{_workspace.Path}\"");
        _output.WriteLine("PULL STDOUT:\n" + pullResult.Stdout);
        Assert.Equal(0, pullResult.ExitCode);

        // 5. Verify — confirm workspace still matches server state
        var verifyResult = await CliRunner.RunAsync(
            $"verify --workspace \"{_workspace.Path}\"");
        _output.WriteLine("VERIFY STDOUT:\n" + verifyResult.Stdout);
        Assert.Equal(0, verifyResult.ExitCode);
        Assert.Contains("Push verification passed", verifyResult.Stdout);

        // 6. Fresh clone to independent workspace — confirm the edit persisted
        var freshClone = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_verifyWorkspace.Path}\"");
        _output.WriteLine("FRESH CLONE STDOUT:\n" + freshClone.Stdout);
        Assert.Equal(0, freshClone.ExitCode);

        var freshTopicPath = Path.Combine(_verifyWorkspace.Path, "topics", topicName);
        Assert.True(File.Exists(freshTopicPath), $"Fresh clone must contain {topicName}");

        var freshContent = await File.ReadAllTextAsync(freshTopicPath);
        _output.WriteLine($"Fresh clone topic length: {freshContent.Length} chars");

        // The text edit is real dialog content that survives the round-trip
        Assert.Contains(testTag, freshContent);
    }
}
