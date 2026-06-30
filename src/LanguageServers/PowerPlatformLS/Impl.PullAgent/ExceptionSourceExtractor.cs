namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using System;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Extracts a short, meaningful source reference from an exception stack trace.
    /// Used to make error logs self-contained and searchable in the codebase.
    /// </summary>
    internal static class ExceptionSourceExtractor
    {
        /// <summary>
        /// Returns the first non-framework stack frame as "FileName.cs:Line" or "Type.Method".
        /// Returns null if no meaningful frame is found.
        /// </summary>
        internal static string? GetSource(Exception ex)
        {
            var trace = new StackTrace(ex, fNeedFileInfo: true);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var frame = trace.GetFrame(i);
                if (frame == null) continue;

                var method = frame.GetMethod();
                if (method == null) continue;

                var declaringType = method.DeclaringType;
                if (declaringType == null) continue;

                // Skip framework/infrastructure frames
                var ns = declaringType.Namespace ?? string.Empty;
                if (ns.StartsWith("System.", StringComparison.Ordinal) ||
                    ns.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ||
                    ns.StartsWith("Microsoft.CommonLanguageServerProtocol.", StringComparison.Ordinal))
                {
                    continue;
                }

                // Prefer file:line if available (from PDB)
                var fileName = frame.GetFileName();
                var lineNumber = frame.GetFileLineNumber();
                if (!string.IsNullOrEmpty(fileName) && lineNumber > 0)
                {
                    return $"{Path.GetFileName(fileName)}:{lineNumber}";
                }

                // Fallback to Type.Method
                return $"{declaringType.Name}.{method.Name}";
            }

            return null;
        }

        /// <summary>
        /// Formats exception source as " (at Source)" or empty string if unavailable.
        /// </summary>
        internal static string FormatSource(Exception ex)
        {
            var source = GetSource(ex);
            return source != null ? $" (at {source})" : string.Empty;
        }
    }
}
