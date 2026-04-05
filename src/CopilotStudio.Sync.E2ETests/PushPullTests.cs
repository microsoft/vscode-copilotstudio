// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// E2E tests for push and pull operations through the test CLI.
/// </summary>
public sealed class PushPullTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TempWorkspace _workspace;

    public PushPullTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = new TempWorkspace();
    }

    public void Dispose() => _workspace.Dispose();

    [LiveTenantFact]
    public async Task PullAfterCloneExitsZeroAndShowsDiagnostics()
    {
        // Clone first
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        var cloneResult = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_workspace.Path}\"");

        _output.WriteLine("CLONE STDOUT:\n" + cloneResult.Stdout);
        Assert.Equal(0, cloneResult.ExitCode);

        // Pull immediately after clone — should succeed with no remote changes
        var pullResult = await CliRunner.RunAsync(
            $"pull --workspace \"{_workspace.Path}\"");

        _output.WriteLine("PULL STDOUT:\n" + pullResult.Stdout);
        _output.WriteLine("PULL STDERR:\n" + pullResult.Stderr);

        Assert.Equal(0, pullResult.ExitCode);
        Assert.Contains("Pull complete.", pullResult.Stdout);
        // Should show diagnostic context
        Assert.Contains("Environment:", pullResult.Stdout);
        Assert.Contains("Agent ID:", pullResult.Stdout);
    }

    [LiveTenantFact]
    public async Task PushWithNoChangesExitsZero()
    {
        // Clone first
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        var cloneResult = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_workspace.Path}\"");

        _output.WriteLine("CLONE STDOUT:\n" + cloneResult.Stdout);
        Assert.Equal(0, cloneResult.ExitCode);

        // Push with no local changes — should exit 0, report "no local changes"
        var pushResult = await CliRunner.RunAsync(
            $"push --workspace \"{_workspace.Path}\"");

        _output.WriteLine("PUSH STDOUT:\n" + pushResult.Stdout);
        _output.WriteLine("PUSH STDERR:\n" + pushResult.Stderr);

        Assert.Equal(0, pushResult.ExitCode);
        Assert.Contains("No local changes detected.", pushResult.Stdout);
    }
}
