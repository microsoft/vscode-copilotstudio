// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using System.Text;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// Runs the test harness CLI as a child process and captures output.
/// All E2E tests exercise the shared library through this runner.
/// </summary>
internal sealed class CliRunner
{
    private static readonly string ProjectPath = ResolveProjectPath();

    /// <summary>
    /// Result of a CLI invocation.
    /// </summary>
    public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Runs the test harness CLI with the given arguments.
    /// </summary>
    public static async Task<CliResult> RunAsync(string arguments, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(5);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{ProjectPath}\" -- {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Forward auth env vars to child process
        foreach (var envVar in new[]
        {
            "COPILOT_TEST_TENANT_ID",
            "COPILOT_TEST_CLIENT_ID",
            "COPILOT_TEST_ENVIRONMENT_ID",
            "COPILOT_TEST_ENVIRONMENT_URL",
            "COPILOT_TEST_AGENT_SCHEMA_NAME",
        })
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (value != null)
            {
                psi.Environment[envVar] = value;
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CLI process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout.Value);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"CLI process timed out after {timeout.Value.TotalSeconds}s. Args: {arguments}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new CliResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Resolves the test harness .csproj path relative to this test assembly.
    /// </summary>
    private static string ResolveProjectPath()
    {
        // Walk from the test assembly directory to find the TestHarness project.
        // Layout: ext/src/CopilotStudio.Sync.E2ETests/bin/Debug/net10.0/ → ext/src/CopilotStudio.Sync.TestHarness/
        var assemblyDir = Path.GetDirectoryName(typeof(CliRunner).Assembly.Location)!;
        var srcDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));
        var projectPath = Path.Combine(srcDir, "CopilotStudio.Sync.TestHarness", "CopilotStudio.Sync.TestHarness.csproj");

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException(
                $"Test harness project not found at {projectPath}. " +
                "Ensure the test harness is built before running E2E tests.",
                projectPath);
        }

        return projectPath;
    }
}
