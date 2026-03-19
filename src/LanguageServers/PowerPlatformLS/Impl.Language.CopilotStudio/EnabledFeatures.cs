namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel.Abstractions;
    using System.Globalization;

    /// <summary>
    /// Default configuration for enabled features.
    /// Could be populated from configurations if any overrides is necessary.
    /// </summary>
    internal class EnabledFeatures : IFeatureConfiguration
    {
        public CultureInfo? CultureInfoOverride => null;

        public long GetInt64Value(string settingName, long defaultValue) => defaultValue;

        public string GetStringValue(string settingName, string defaultValue) => defaultValue;

        public bool IsEnvironmentFeatureEnabled(string featureName, bool defaultValue) => true;

        public bool IsTenantFeatureEnabled(string featureName, bool defaultValue) => true;
    }
}