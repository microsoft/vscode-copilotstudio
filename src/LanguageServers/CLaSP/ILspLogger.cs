// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    using System;

    public enum HandlerOutcome
    {
        Success,
        Failure,
        Canceled
    }

    public interface ILspLogger
    {
        // -------------------------------------------------------------------
        // Request lifecycle
        // -------------------------------------------------------------------

        void SetCurrentRequestId(int requestId) { }
        void LogStartContext(string methodName);
        void LogEndContext(string methodName, long durationMs = -1, HandlerOutcome outcome = HandlerOutcome.Success);

        // -------------------------------------------------------------------
        // Standard logging
        // -------------------------------------------------------------------

        void LogTrace(string message, params object[] @params) { }
        void LogDebug(string message, params object[] @params);
        void LogInformation(string message, params object[] @params);

        // -------------------------------------------------------------------
        // Warning/Error logging
        // -------------------------------------------------------------------

        void LogWarning(string message, params object[] @params);
        void LogError(string message, params object[] @params);
        void LogException(Exception exception, string? message = null, params object[] @params);

        // -------------------------------------------------------------------
        // Sensitive logging (PII-safe in production, full in debug/test)
        // -------------------------------------------------------------------

        void LogSensitiveInformation(string message, string safeMessage);
        void LogSensitiveWarning(string message, string safeMessage) { }
        void LogSensitiveError(string message, string safeMessage) { }
    }
}