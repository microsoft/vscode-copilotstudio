// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// E2E tests for clone operations. Exercises the shared library through the test CLI.
/// </summary>
[Collection(LiveTenantCollection.Name)]
public sealed class CloneTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TempWorkspace _workspace;

    public CloneTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = new TempWorkspace();
    }

    public void Dispose() => _workspace.Dispose();

    [LiveTenantFact]
    public async Task CloneExitsZeroAndProducesExpectedLayout()
    {
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        var result = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_workspace.Path}\"");

        _output.WriteLine("STDOUT:\n" + result.Stdout);
        _output.WriteLine("STDERR:\n" + result.Stderr);

        Assert.Equal(0, result.ExitCode);

        // SYNC-SEMANTICS.md: clone must produce agent.mcs.yml and .mcs/conn.json
        Assert.True(File.Exists(Path.Combine(_workspace.Path, "agent.mcs.yml")),
            "Clone must produce agent.mcs.yml");
        Assert.True(File.Exists(Path.Combine(_workspace.Path, ".mcs", "conn.json")),
            "Clone must produce .mcs/conn.json");
        Assert.True(File.Exists(Path.Combine(_workspace.Path, ".mcs", "botdefinition.json")),
            "Clone must produce .mcs/botdefinition.json (cloud cache)");
    }

    [LiveTenantFact]
    public async Task CloneOutputIncludesEntityTypeInventory()
    {
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        var result = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_workspace.Path}\"");

        _output.WriteLine("STDOUT:\n" + result.Stdout);

        Assert.Equal(0, result.ExitCode);

        // Diagnostic output must include entity type inventory
        Assert.Contains("Entity type inventory:", result.Stdout);
        Assert.Contains("Clone complete.", result.Stdout);
    }

    [LiveTenantFact]
    public async Task CloneOutputMatchesKnownEntityTypes()
    {
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        var result = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_workspace.Path}\"");

        _output.WriteLine("STDOUT:\n" + result.Stdout);
        Assert.Equal(0, result.ExitCode);

        // All subdirectories must map to known entity types per SYNC-SEMANTICS.md
        var knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "topics", "actions", "agents", "knowledge", "variables",
            "settings", "entities", "skills", "trigger", "translations",
            "environmentvariables", "workflows", ".mcs",
        };

        var actualDirs = Directory.GetDirectories(_workspace.Path)
            .Select(d => Path.GetFileName(d))
            .ToList();

        foreach (var dir in actualDirs)
        {
            Assert.True(knownDirs.Contains(dir),
                $"Unexpected directory '{dir}' not in SYNC-SEMANTICS.md entity type set");
        }
    }
}
