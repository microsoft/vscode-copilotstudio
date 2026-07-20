namespace Microsoft.PowerPlatformLS.LanguageServerHost
{
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Defense-in-depth telemetry initializer that scrubs known PII patterns
    /// (file paths, email addresses) from trace messages and custom properties
    /// before they leave the process. This is a safety net — call-site-level protection
    /// via LogSensitiveInformation/LogSensitiveError is preferred for intentional redaction.
    /// </summary>
    internal sealed partial class PiiScrubTelemetryInitializer : ITelemetryInitializer
    {
        public void Initialize(ITelemetry telemetry)
        {
            switch (telemetry)
            {
                case TraceTelemetry trace:
                    trace.Message = ScrubMessage(trace.Message);
                    ScrubProperties(trace.Properties);
                    break;
                case ExceptionTelemetry ex:
                    ScrubProperties(ex.Properties);
                    break;
            }
        }

        internal static string ScrubMessage(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message ?? string.Empty;
            }

            // Scrub Windows file paths (e.g., C:\Users\...\agent.mcs.yml)
            message = WindowsPathRegex().Replace(message, "<path>");

            // Scrub Unix file paths (e.g., /home/user/agents/MyBot/)
            message = UnixPathRegex().Replace(message, "<path>");

            // Scrub email addresses
            message = EmailRegex().Replace(message, "<email>");

            return message;
        }

        private static void ScrubProperties(IDictionary<string, string> properties)
        {
            // Scrub known property keys that may contain PII
            string[] sensitiveKeys = ["FormattedMessage", "Message", "{OriginalFormat}", "Agent"];
            foreach (var key in sensitiveKeys)
            {
                if (properties.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                {
                    properties[key] = ScrubMessage(value);
                }
            }
        }

        // Matches Windows paths like C:\Users\username\... or D:\folder\file.ext
        [GeneratedRegex(@"[A-Za-z]:\\(?:[\w\-.]+\\)*[\w\-.]+")]
        private static partial Regex WindowsPathRegex();

        // Matches Unix paths starting with /home/, /Users/, or /tmp/ (common user-specific roots)
        [GeneratedRegex(@"(?:/home/|/Users/|/tmp/)[\w\-./]+")]
        private static partial Regex UnixPathRegex();

        // Matches email addresses
        [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
        private static partial Regex EmailRegex();
    }
}
