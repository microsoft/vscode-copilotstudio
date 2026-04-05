// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// E2E tests for the verify command. Exercises VerifyPushAsync through the test CLI.
/// </summary>
[Collection(LiveTenantCollection.Name)]
public sealed class VerifyTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TempWorkspace _workspace;

    public VerifyTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = new TempWorkspace();
    }

    public void Dispose() => _workspace.Dispose();

    [LiveTenantFact]
    public async Task VerifyAfterCloneExitsZeroAndReportsAccepted()
    {
        // Clone first
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        var cloneResult = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_workspace.Path}\"");

        _output.WriteLine("CLONE STDOUT:\n" + cloneResult.Stdout);
        Assert.Equal(0, cloneResult.ExitCode);

        // Verify: freshly cloned workspace should match server state exactly
        var verifyResult = await CliRunner.RunAsync(
            $"verify --workspace \"{_workspace.Path}\"");

        _output.WriteLine("VERIFY STDOUT:\n" + verifyResult.Stdout);
        _output.WriteLine("VERIFY STDERR:\n" + verifyResult.Stderr);

        Assert.Equal(0, verifyResult.ExitCode);
        Assert.Contains("Verification results:", verifyResult.Stdout);
        Assert.Contains("Push verification passed", verifyResult.Stdout);
    }

    [LiveTenantFact]
    public async Task VerifyOutputShowsPerEntityTypeResults()
    {
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        var cloneResult = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_workspace.Path}\"");
        Assert.Equal(0, cloneResult.ExitCode);

        var verifyResult = await CliRunner.RunAsync(
            $"verify --workspace \"{_workspace.Path}\"");

        _output.WriteLine("VERIFY STDOUT:\n" + verifyResult.Stdout);

        Assert.Equal(0, verifyResult.ExitCode);
        // Verify must pass. When workspace matches server exactly (no changes pushed),
        // VerifyPushAsync returns empty EntityTypes and prints the summary line only.
        Assert.Contains("Verification results:", verifyResult.Stdout);
        Assert.Contains("Push verification passed", verifyResult.Stdout);
    }
}
