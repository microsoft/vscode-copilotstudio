// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// F3 acceptance: "Run test CLI clone and extension clone against the same agent.
/// Verify workspace files are byte-identical (excluding .mcs/ internal state)."
///
/// The "clone" command uses the test harness's direct auth plumbing (AuthProvider →
/// DataverseHttpClientAccessor). The "clone-via-bridge" command uses the extension's
/// actual bridge types (TokenManager → LspSyncAuthProvider → LspDataverseHttpClientAccessor).
/// Both exercise the same shared library — this test proves the extension bridge stack
/// produces identical output.
/// </summary>
[Collection(LiveTenantCollection.Name)]
public sealed class CloneComparisonTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TempWorkspace _cliWorkspace;
    private readonly TempWorkspace _bridgeWorkspace;

    public CloneComparisonTests(ITestOutputHelper output)
    {
        _output = output;
        _cliWorkspace = new TempWorkspace();
        _bridgeWorkspace = new TempWorkspace();
    }

    public void Dispose()
    {
        _cliWorkspace.Dispose();
        _bridgeWorkspace.Dispose();
    }

    [LiveTenantFact]
    public async Task ExtensionBridgeCloneMatchesCliClone()
    {
        var envUrl = Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_URL")!;
        var agentSchema = Environment.GetEnvironmentVariable("COPILOT_TEST_AGENT_SCHEMA_NAME")!;

        // 1. Clone via direct CLI path (AuthProvider → DataverseHttpClientAccessor)
        var cliResult = await CliRunner.RunAsync(
            $"clone --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_cliWorkspace.Path}\"");
        _output.WriteLine("CLI CLONE STDOUT:\n" + cliResult.Stdout);
        Assert.Equal(0, cliResult.ExitCode);

        // 2. Clone via extension bridge path (TokenManager → LspSyncAuthProvider → LspDataverseHttpClientAccessor)
        var bridgeResult = await CliRunner.RunAsync(
            $"clone-via-bridge --environment \"{envUrl}\" --agent-schema-name \"{agentSchema}\" --output \"{_bridgeWorkspace.Path}\"");
        _output.WriteLine("BRIDGE CLONE STDOUT:\n" + bridgeResult.Stdout);
        Assert.Equal(0, bridgeResult.ExitCode);

        // 3. Compare all workspace files excluding .mcs/ (internal state — timestamps, tokens differ)
        var cliFiles = GetWorkspaceFiles(_cliWorkspace.Path);
        var bridgeFiles = GetWorkspaceFiles(_bridgeWorkspace.Path);

        _output.WriteLine($"CLI workspace: {cliFiles.Count} file(s)");
        _output.WriteLine($"Bridge workspace: {bridgeFiles.Count} file(s)");

        // Same file set
        var cliKeys = cliFiles.Keys.OrderBy(k => k).ToList();
        var bridgeKeys = bridgeFiles.Keys.OrderBy(k => k).ToList();
        Assert.Equal(cliKeys, bridgeKeys);

        // Byte-identical content
        var mismatches = new List<string>();
        foreach (var (relativePath, cliBytes) in cliFiles)
        {
            if (!bridgeFiles.TryGetValue(relativePath, out var bridgeBytes))
            {
                mismatches.Add($"Missing in bridge: {relativePath}");
                continue;
            }

            if (!cliBytes.AsSpan().SequenceEqual(bridgeBytes))
            {
                mismatches.Add($"Content differs: {relativePath} (CLI={cliBytes.Length} bytes, Bridge={bridgeBytes.Length} bytes)");
            }
        }

        if (mismatches.Count > 0)
        {
            foreach (var m in mismatches)
            {
                _output.WriteLine($"MISMATCH: {m}");
            }
        }

        Assert.Empty(mismatches);
        _output.WriteLine($"All {cliFiles.Count} workspace file(s) are byte-identical between CLI and extension bridge paths.");
    }

    private static Dictionary<string, byte[]> GetWorkspaceFiles(string root)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relativePath.StartsWith(".mcs/", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(".mcs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result[relativePath] = File.ReadAllBytes(file);
        }

        return result;
    }
}
