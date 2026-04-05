// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// Tests that the CLI exits with correct codes for error conditions.
/// These tests do NOT require live tenant access — they test CLI behavior
/// with invalid inputs that fail before any network calls.
/// </summary>
public sealed class ExitCodeTests
{
    private readonly ITestOutputHelper _output;

    public ExitCodeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PushWithMissingWorkspaceExitsNonZero()
    {
        var result = await CliRunner.RunAsync("push --workspace /nonexistent/path");

        _output.WriteLine("STDOUT:\n" + result.Stdout);
        _output.WriteLine("STDERR:\n" + result.Stderr);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(".mcs/conn.json", result.Stderr);
    }

    [Fact]
    public async Task PullWithMissingWorkspaceExitsNonZero()
    {
        var result = await CliRunner.RunAsync("pull --workspace /nonexistent/path");

        _output.WriteLine("STDOUT:\n" + result.Stdout);
        _output.WriteLine("STDERR:\n" + result.Stderr);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(".mcs/conn.json", result.Stderr);
    }

    [Fact]
    public async Task VerifyWithMissingWorkspaceExitsNonZero()
    {
        var result = await CliRunner.RunAsync("verify --workspace /nonexistent/path");

        _output.WriteLine("STDOUT:\n" + result.Stdout);
        _output.WriteLine("STDERR:\n" + result.Stderr);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(".mcs/conn.json", result.Stderr);
    }

    [Fact]
    public async Task HelpExitsZero()
    {
        var result = await CliRunner.RunAsync("--help");

        _output.WriteLine("STDOUT:\n" + result.Stdout);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("clone", result.Stdout);
        Assert.Contains("push", result.Stdout);
        Assert.Contains("pull", result.Stdout);
        Assert.Contains("verify", result.Stdout);
    }
}
