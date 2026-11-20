// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Abstractions;
using System.Globalization;

namespace Microsoft.CopilotStudio.McsCore;

internal sealed class DefaultFeatureConfiguration : IFeatureConfiguration
{
    public static DefaultFeatureConfiguration Instance { get; } = new DefaultFeatureConfiguration();

    public CultureInfo? CultureInfoOverride => null;

    public long GetInt64Value(string settingName, long defaultValue) => defaultValue;

    public string GetStringValue(string settingName, string defaultValue) => defaultValue;

    public bool IsEnvironmentFeatureEnabled(string featureName, bool defaultValue) => featureName == FeatureFlags.ClientSettingsIvrFeatureEnabled.FeatureName || defaultValue;

    public bool IsTenantFeatureEnabled(string featureName, bool defaultValue) => defaultValue;
}
