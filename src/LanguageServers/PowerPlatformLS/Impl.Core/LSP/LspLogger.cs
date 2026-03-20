
namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System;

    internal class LspLogger : ILspLogger
    {
        private readonly ILogger<LspLogger> _logger;
        private readonly bool _isTestLogger;

        public LspLogger(ILogger<LspLogger> logger, BuildVersionInfo? gitInfo = null, SessionInformation? sessionInformation = null)
        {
            if (gitInfo != null)
            {
                // This will add as a custom dimension to all logs. 
                logger.BeginScope(new Dictionary<string, object>
                {
                    ["GitHash"] = gitInfo.Hash ?? string.Empty,
                    ["Vsix"] = gitInfo.VsixVersion ?? string.Empty,
                    ["pid"] = Environment.ProcessId,
                    ["SessionId"] = sessionInformation?.SessionId ?? string.Empty
                });
            }

            _logger = logger;
            _isTestLogger = _logger.GetType().AssemblyQualifiedName?.StartsWith("Microsoft.PowerPlatformLS.UnitTests") == true;
        }

        public void LogEndContext(string message, params object[] @params)
        {
            _logger.LogInformation($"EndContext: {message}", @params);
        }

        public void LogError(string message, params object[] @params)
        {
            _logger.LogError(message, @params);
        }

        public void LogException(Exception exception, string? message = null, params object[] @params)
        {
            _logger.LogError(exception, message, @params);
        }

        public void LogInformation(string message, params object[] @params)
        {
            _logger.LogInformation(message, @params);
        }

        public void LogSensitiveInformation(string message, string? altSafeMsg = null)
        {
#if DEBUG
            _logger.LogInformation(message);
#else
            if (_isTestLogger)
            {
                _logger.LogInformation(message);
            }
            else if (altSafeMsg != null)
            {
                _logger.LogInformation(altSafeMsg);
            }
#endif
        }

        public void LogStartContext(string message, params object[] @params)
        {
            _logger.LogInformation($"StartContext: {message}", @params);
        }

        public void LogWarning(string message, params object[] @params)
        {
            _logger.LogWarning(message, @params);
        }
    }
}
