// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    using System;

    public interface ILspLogger
    {
        void LogStartContext(string methodName);
        void LogEndContext(string methodName, long durationMs = -1);
        void LogDebug(string message, params object[] @params);
        void LogInformation(string message, params object[] @params);

        /// <summary>
        /// Log sensitive information. This should be used for logging information that may contain sensitive data like PII or other secrets.
        /// This will only be logged in debug mode when the logs are only surfaced to the local client.
        /// </summary>
        void LogSensitiveInformation(string message, string? altSafeMessage = null);
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
