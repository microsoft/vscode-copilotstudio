namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Text.Json;

    internal class PowerPlatformEndpointsConfig
    {
        private static readonly Lazy<PowerPlatformEndpointsConfig> LazyInstance =
            new Lazy<PowerPlatformEndpointsConfig>(() => LoadConfiguration());

        private readonly Dictionary<string, string> _endpointSuffixes;

        public static PowerPlatformEndpointsConfig Instance => LazyInstance.Value;

        private PowerPlatformEndpointsConfig(Dictionary<string, string> endpointSuffixes)
        {
            _endpointSuffixes = endpointSuffixes ?? throw new ArgumentNullException(nameof(endpointSuffixes));
        }

        public string GetEndpointSuffix(CoreServicesClusterCategory category)
        {
            var key = category.ToString();
            if (_endpointSuffixes.TryGetValue(key, out var suffix))
            {
                return suffix;
            }

            throw new KeyNotFoundException($"Configuration mapping missing for cluster category: {category}");
        }

        private static PowerPlatformEndpointsConfig LoadConfiguration()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Microsoft.PowerPlatformLS.Impl.PullAgent.Configuration.PowerPlatformEndpoints.json";
            return LoadConfigurationFromResource(assembly, resourceName);
        }

        internal static PowerPlatformEndpointsConfig LoadConfigurationFromResource(Assembly assembly, string resourceName)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException(
                    $"Failed to load embedded resource '{resourceName}'. " +
                    "Ensure that the PowerPlatformEndpoints.json file is included as an EmbeddedResource in the project.");
            }

            return LoadConfigurationFromStream(stream);
        }

        internal static PowerPlatformEndpointsConfig LoadConfigurationFromStream(Stream stream)
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            var json = reader.ReadToEnd();
            var config = JsonSerializer.Deserialize<ConfigurationRoot>(json);

            if (config?.EndpointSuffixes == null || config.EndpointSuffixes.Count == 0)
            {
                throw new InvalidOperationException(
                    "The PowerPlatformEndpoints.json configuration file is invalid or empty. " +
                    "It must contain an 'EndpointSuffixes' object with endpoint mappings.");
            }

            return new PowerPlatformEndpointsConfig(config.EndpointSuffixes);
        }

        private class ConfigurationRoot
        {
            public Dictionary<string, string>? EndpointSuffixes { get; set; }
        }
    }
}
