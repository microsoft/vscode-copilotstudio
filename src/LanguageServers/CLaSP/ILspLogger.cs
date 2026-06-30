// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    using System;

    /// <summary>
    /// Outcome of an LSP request handler execution.
    /// </summary>
    public enum HandlerOutcome
    {
        Success,
        Failure,
        Canceled
    }

    public interface ILspLogger
    {
        void LogStartContext(string methodName, string? agentName = null);
        void LogEndContext(string methodName, long durationMs = -1, HandlerOutcome outcome = HandlerOutcome.Success, string? agentName = null);
        void LogDebug(string message, params object[] @params);
        void LogTrace(string message, params object[] @params) { }
        void LogInformation(string message, params object[] @params);

        /// <summary>
        /// Log sensitive information. This should be used for logging information that may contain sensitive data like PII or other secrets.
        /// This will only be logged in debug mode when the logs are only surfaced to the local client.
        /// </summary>
        void LogSensitiveInformation(string message, string? altSafeMessage = null);

        /// <summary>
        /// Log a warning that may contain sensitive data (file paths, agent names, Dataverse error payloads).
        /// In Release: the safe message goes to telemetry at Warning level; the full message goes to the output channel at Debug level.
        /// In Debug: the full message is logged at Warning level (visible locally).
        /// </summary>
        void LogSensitiveWarning(string message, string safeMessage) { }

        /// <summary>
        /// Log an error that may contain sensitive data (exception messages with customer content, OData error bodies).
        /// In Release: the safe message goes to telemetry at Error level; the full message goes to the output channel at Debug level.
        /// In Debug: the full message is logged at Error level (visible locally).
        /// </summary>
        void LogSensitiveError(string message, string safeMessage) { }
        void LogWarning(string message, params object[] @params);
        void LogError(string message, params object[] @params);
        void LogException(Exception exception, string? message = null, params object[] @params);

        /// <summary>
        /// Sets the ambient request ID for the current execution context.
        /// Called by the queue before handler execution to restore correlation context
        /// that cannot flow via AsyncLocal across queue boundaries.
        /// </summary>
        void SetCurrentRequestId(int requestId) { }
    }
}
