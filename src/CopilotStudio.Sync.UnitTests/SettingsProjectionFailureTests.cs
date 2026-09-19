// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SettingsProjectionFailureTests
{
    private const string MalformedMergedSettings =
        "displayName: templ6\nschemaName: templ6_templ6\nconfiguration:\n" +
        "  recognizer:\n    kind: CLICopilotRecognizer\n" +
        "template: cliagent-1.0.0\nlanguage: 1033  authoringModel: CliCopilot\n";

    private const string ValidSettings =
        "displayName: templ6\nschemaName: templ6_templ6\ntemplate: cliagent-1.0.0\nlanguage: 1033\n";

    [Fact]
    public void RawDeserializeOfMalformedMergeOutputThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => CodeSerializer.Deserialize<BotEntity>(MalformedMergedSettings));
    }

    [Fact]
    public void FormatExceptionCountsAsAProjectionFailure()
    {
        Assert.True(WorkspaceSynchronizer.IsProjectionSerializationFailure(new FormatException("bad int")));
    }

    [Fact]
    public void TryDeserializeSettingsYamlSwallowsMalformedMergeOutput()
    {
        Assert.Null(WorkspaceSynchronizer.TryDeserializeSettingsYaml(MalformedMergedSettings));
    }

    [Fact]
    public void TryDeserializeSettingsYamlReturnsTheEntityForValidYaml()
    {
        var entity = WorkspaceSynchronizer.TryDeserializeSettingsYaml(ValidSettings);

        Assert.NotNull(entity);
        Assert.Equal("templ6", entity!.DisplayName?.ToString());
    }

    [Fact]
    public void TryGetSettingsYamlTreatsAMissingEntityAsProjectable()
    {
        Assert.True(WorkspaceSynchronizer.TryGetSettingsYaml(null, out var settingsYaml));
        Assert.Null(settingsYaml);
    }

    [Fact]
    public void TryGetSettingsYamlProjectsARealEntity()
    {
        var entity = CodeSerializer.Deserialize<BotEntity>(ValidSettings)!;

        Assert.True(WorkspaceSynchronizer.TryGetSettingsYaml(entity, out var settingsYaml));
        Assert.NotNull(settingsYaml);
        Assert.Contains("displayName: templ6", settingsYaml!, StringComparison.Ordinal);
        Assert.Contains("language: 1033", settingsYaml!, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectedSettingsYamlAlwaysSurvivesItsOwnRoundTrip()
    {
        var entity = CodeSerializer.Deserialize<BotEntity>(ValidSettings)!;
        Assert.True(WorkspaceSynchronizer.TryGetSettingsYaml(entity, out var settingsYaml));

        Assert.NotNull(WorkspaceSynchronizer.TryDeserializeSettingsYaml(settingsYaml!));
    }
}
