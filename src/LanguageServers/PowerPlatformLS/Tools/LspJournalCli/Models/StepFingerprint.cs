namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Models
{
    using System.Text.Json;

    /// <summary>
    /// Fingerprint for a journal step, used to match steps across rebaselines
    /// for annotation preservation. The fingerprint is:
    ///   (step type, textDocument.uri, position line:char, occurrence index)
    ///
    /// Two steps with the same (step, uri, position) are disambiguated by
    /// occurrence index — their ordinal within that fingerprint group.
    /// </summary>
    public readonly record struct StepFingerprint(string Step, string? Uri, int? Line, int? Character, int OccurrenceIndex)
    {
        /// <summary>
        /// Build a fingerprinted step list from a journal's steps.
        /// Each step gets a fingerprint with an occurrence index that disambiguates
        /// steps sharing the same (step, uri, position) triple.
        /// </summary>
        public static List<StepFingerprint> BuildFingerprints(IReadOnlyList<JournalStep> steps)
        {
            var fingerprints = new List<StepFingerprint>(steps.Count);
            var occurrenceCounts = new Dictionary<(string, string?, int?, int?), int>();

            foreach (var step in steps)
            {
                var (uri, line, character) = ExtractLocationFromParams(step.Params);
                var key = (step.Step, uri, line, character);

                occurrenceCounts.TryGetValue(key, out var count);
                fingerprints.Add(new StepFingerprint(step.Step, uri, line, character, count));
                occurrenceCounts[key] = count + 1;
            }

            return fingerprints;
        }

        /// <summary>
        /// Extract textDocument.uri and position (or range.start) from step params.
        /// Returns nulls for steps without these fields (initialize, shutdown, exit, etc.).
        /// </summary>
        private static (string? Uri, int? Line, int? Character) ExtractLocationFromParams(JsonElement? @params)
        {
            if (!@params.HasValue)
                return (null, null, null);

            var p = @params.Value;
            string? uri = null;
            int? line = null;
            int? character = null;

            // Extract textDocument.uri
            if (p.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var uriProp))
            {
                uri = uriProp.GetString();
            }

            // Extract position.line / position.character
            if (p.TryGetProperty("position", out var pos))
            {
                if (pos.TryGetProperty("line", out var lineProp))
                    line = lineProp.GetInt32();
                if (pos.TryGetProperty("character", out var charProp))
                    character = charProp.GetInt32();
            }
            // Fallback: range.start.line / range.start.character
            else if (p.TryGetProperty("range", out var range) && range.TryGetProperty("start", out var start))
            {
                if (start.TryGetProperty("line", out var lineProp))
                    line = lineProp.GetInt32();
                if (start.TryGetProperty("character", out var charProp))
                    character = charProp.GetInt32();
            }

            return (uri, line, character);
        }
    }
}