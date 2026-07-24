namespace Microsoft.PowerPlatformLS.LanguageServerHost
{
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Defense-in-depth telemetry initializer that scrubs known PII patterns
    /// (file paths, email addresses) from trace messages and custom properties
    /// before they leave the process. This is a safety net — call-site-level protection
    /// via LogSensitiveInformation/LogSensitiveError is preferred for intentional redaction.
    /// Also enriches telemetry with agent/env context from AsyncLocal as separate dimensions.
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
                    EnrichWithAgentContext(trace.Properties);
                    break;
                case ExceptionTelemetry ex:
                    ScrubProperties(ex.Properties);
                    EnrichWithAgentContext(ex.Properties);
                    break;
            }
        }

        internal static string ScrubMessage(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message ?? string.Empty;
            }

            // Step 1: Process <pii>...</pii> tags — known patterns → named placeholder, unknown → <redacted>
            message = PiiTagRegex().Replace(message, match =>
            {
                var inner = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
                var sanitized = ApplyKnownPatterns(inner);
                return sanitized != inner ? sanitized : "<redacted>";
            });

            // Step 2: Apply known patterns to remaining untagged content
            message = ApplyKnownPatterns(message);

            return message;
        }

        /// <summary>
        /// Applies all known PII pattern replacements to a string.
        /// Returns the input unchanged if no patterns match.
        /// </summary>
        private static string ApplyKnownPatterns(string text)
        {
            text = WindowsPathRegex().Replace(text, "<path>");
            text = UnixPathRegex().Replace(text, "<path>");
            text = EmailRegex().Replace(text, "<email>");
            text = UrlQueryStringRegex().Replace(text, "$1?<query>");
            text = GuidRegex().Replace(text, "<id>");
            return text;
        }

        private static void ScrubProperties(IDictionary<string, string> properties)
        {
            // Scrub known property keys that may contain PII
            string[] sensitiveKeys = ["FormattedMessage", "Message", "{OriginalFormat}", "Agent", "ErrorMessage", "Error", "Source", "ContentRoot"];
            foreach (var key in sensitiveKeys)
            {
                if (properties.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                {
                    properties[key] = ScrubMessage(value);
                }
            }
        }

        // Matches <pii>...</pii> tagged content, with optional type and encoded attributes
        [GeneratedRegex(@"<pii(?:\s+type=""[^""]*"")?(?:\s+encoded=""[^""]*"")?>(.*?)</pii>", RegexOptions.Singleline)]
        private static partial Regex PiiTagRegex();

        // Matches Windows paths like C:\Users\username\... or D:\folder\file.ext
        [GeneratedRegex(@"[A-Za-z]:\\(?:[\w\-.]+\\)*[\w\-.]+")]
        private static partial Regex WindowsPathRegex();

        // Matches Unix paths starting with /home/, /Users/, or /tmp/ (common user-specific roots)
        [GeneratedRegex(@"(?:/home/|/Users/|/tmp/)[\w\-./]+")]
        private static partial Regex UnixPathRegex();

        // Matches email addresses
        [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
        private static partial Regex EmailRegex();

        // Matches URL query strings (e.g., ?key=value&other=data)
        [GeneratedRegex(@"(https?://[^\s?]+)\?[^\s""<>]+")]
        private static partial Regex UrlQueryStringRegex();

        // Matches standalone GUIDs NOT preceded by known safe prefixes (e.g., sessionId=)
        [GeneratedRegex(@"(?<!sessionId=)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
        private static partial Regex GuidRegex();

        /// <summary>
        /// Adds agent/env context and shared dimensions from AsyncLocal as separate telemetry properties.
        /// Only sets a property if it has a non-empty value and isn't already present.
        /// </summary>
        private static void EnrichWithAgentContext(IDictionary<string, string> properties)
        {
            var reqId = LspRequestContext.CurrentRequestId;
            var agentId = LspRequestContext.AgentId;
            var envId = LspRequestContext.EnvironmentId;
            var durationMs = LspRequestContext.PendingDurationMs;

            if (reqId > 0 && !properties.ContainsKey(TelemetryDimensions.ReqId))
            {
                properties[TelemetryDimensions.ReqId] = reqId.ToString();
            }
            if (!string.IsNullOrEmpty(agentId) && !properties.ContainsKey(TelemetryDimensions.AgentId))
            {
                properties[TelemetryDimensions.AgentId] = agentId;
            }
            if (!string.IsNullOrEmpty(envId) && !properties.ContainsKey(TelemetryDimensions.EnvironmentId))
            {
                properties[TelemetryDimensions.EnvironmentId] = envId;
            }
            if (durationMs.HasValue && !properties.ContainsKey(TelemetryDimensions.DurationMs))
            {
                properties[TelemetryDimensions.DurationMs] = durationMs.Value.ToString();
            }
        }
    }
}
