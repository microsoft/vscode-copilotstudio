namespace Microsoft.PowerPlatformLS.LanguageServerHost
{
    using Microsoft.ApplicationInsights.Channel;

    internal static class AppInsightsConfiguration
    {
        // https://learn.microsoft.com/en-us/azure/azure-monitor/app/ilogger#console-application
        // This will hook ILogger<T> to log to app insights.
        // These logs will show in "traces" table in AI. 
        public static void ConfigureAppInsightsLogging(
            this IServiceCollection services,
            InMemoryChannel channel,
            bool isTelemetryEnabled)
        {
            // Environment variable set by the VS Code client process.
            var connectionString = Environment.GetEnvironmentVariable("TELEMETRY_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.WriteLine("Connection string is not provided. Skipping Application Insights logs configuration.");
                return;
            }

            if (isTelemetryEnabled)
            {
                services.AddLogging(builder =>
                {
                    builder.AddApplicationInsights(
                        configureTelemetryConfiguration: (config) =>
                        {
                            config.ConnectionString = connectionString;
                            config.TelemetryChannel = channel;
                        },
                        configureApplicationInsightsLoggerOptions: _ => { }
                    );
                });
            }
        }
    }
}
