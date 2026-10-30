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
    public async Task HelpExitsZero()
    {
        var result = await CliRunner.RunAsync("--help");

        _output.WriteLine("STDOUT:\n" + result.Stdout);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("clone", result.Stdout);
        Assert.Contains("push", result.Stdout);
        Assert.Contains("pull", result.Stdout);
    }

    [Theory]
    [InlineData("clone", "--environment")]
    [InlineData("clone-via-bridge", "--environment")]
    [InlineData("list-agents", "--environment")]
    [InlineData("push", "--workspace")]
    [InlineData("pull", "--workspace")]
    public async Task MissingRequiredOptionExitsNonZero(string command, string option)
    {
        var result = await CliRunner.RunAsync(command);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(option, result.Stderr);
    }

    [Theory]
    [InlineData("clone")]
    [InlineData("clone-via-bridge")]
    public async Task CloneWithInvalidEnvironmentExitsNonZero(string command)
    {
        var result = await CliRunner.RunAsync(
            $"{command} --environment not-an-absolute-uri --environment-id test --agent-schema-name test");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Error:", result.Stderr);
        Assert.Contains("URI", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }
}
