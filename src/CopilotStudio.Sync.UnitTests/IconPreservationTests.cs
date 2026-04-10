// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Tests that icon (IconBase64) is preserved during 3-way merge of BotEntity settings.
/// This verifies the fix for the "icon pull failure" bug where WithOnlySettingsYamlProperties()
/// stripped IconBase64 from the merge inputs, causing the merged entity to lose the icon.
/// </summary>
public class IconPreservationTests
{
    [Fact]
    public void WithOnlySettingsYamlProperties_StripsIconBase64()
    {
        // Verify the precondition: WithOnlySettingsYamlProperties strips IconBase64
        var entity = CreateBotEntityWithIcon("testIconData123");
        var settingsOnly = entity.WithOnlySettingsYamlProperties();

        Assert.Null(settingsOnly.IconBase64);
    }

    [Fact]
    public void ApplySettingsYamlProperties_RestoresIconBase64()
    {
        // Verify the fix mechanism: ApplySettingsYamlProperties restores IconBase64
        var originalWithIcon = CreateBotEntityWithIcon("originalIconData");
        var settingsOnly = originalWithIcon.WithOnlySettingsYamlProperties();

        // settingsOnly has no icon
        Assert.Null(settingsOnly.IconBase64);

        // ApplySettingsYamlProperties restores icon from the source entity
        var restored = originalWithIcon.ApplySettingsYamlProperties(settingsOnly);
        Assert.Equal("originalIconData", restored.IconBase64);
    }

    [Fact]
    public void ApplySettingsYamlProperties_PreservesSettingsChanges()
    {
        // Verify that ApplySettingsYamlProperties preserves settings from the
        // parameter while restoring metadata from this
        var remoteWithIcon = CreateBotEntityWithIcon("remoteIcon");

        // Simulate a settings-only entity (e.g., after 3-way merge)
        var mergedSettings = remoteWithIcon.WithOnlySettingsYamlProperties();

        // Apply: should have remote icon + merged settings
        var result = remoteWithIcon.ApplySettingsYamlProperties(mergedSettings);

        Assert.Equal("remoteIcon", result.IconBase64);
    }

    private static BotEntity CreateBotEntityWithIcon(string iconBase64)
    {
        // Deserialize a minimal BotEntity and set the icon
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: testbot")
            ?? throw new InvalidOperationException("Failed to create BotEntity");

        var builder = entity.ToBuilder();
        builder.IconBase64 = iconBase64;
        return builder.Build();
    }
}
