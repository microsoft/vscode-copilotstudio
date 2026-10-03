namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    /// <summary>Builds the diagnostic shown for a document that still contains merge conflict markers.</summary>
    public static class MergeConflictDiagnostic
    {
        /// <summary>Returns a diagnostic positioned at the first conflict marker, or null when the text has none.</summary>
        public static Diagnostic? TryCreate(string? text)
        {
            if (!McsConflictMarkers.Contains(text))
            {
                return null;
            }

            var line = McsConflictMarkers.FindFirstMarkerLine(text);

            return new Diagnostic
            {
                Range = line > 0 ? new Range { Start = new Position { Line = line - 1, Character = 0 }, End = new Position { Line = line - 1, Character = 0 } } : Range.Zero,
                Severity = DiagnosticSeverity.Error,
                Message = McsConflictMarkers.Message
            };
        }
    }
}
