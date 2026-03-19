namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using System.Text.Json;

    /// <summary>
    /// Factory for creating typed LSP URIs from JSON inputs.
    /// Handles all supported and unsupported schemes without throwing.
    /// Uses pure ErrorObject pattern - always returns LspUri instance.
    /// </summary>
    internal static class LspUriFactory
    {
        private static readonly HashSet<string> SupportedSchemes = new(StringComparer.OrdinalIgnoreCase)
        {
            "file"
        };

        private static readonly HashSet<string> KnownUnsupportedSchemes = new(StringComparer.OrdinalIgnoreCase)
        {
            "git",
            "ssh", 
            "merge-conflict.conflict-diff",
            "vscode-remote",
            "vscode-notebook",
            "vscode-notebook-cell",
            "vscode-interactive",
            "untitled"
        };

        // Track logged schemes to throttle per-scheme
        private static readonly HashSet<string> LoggedSchemes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object LogLock = new object();

        /// <summary>
        /// Creates an LspUri from a JSON element, handling VS Code LSP parameter shapes.
        /// Always returns a concrete LspUri instance (never null).
        /// Check result.IsSupported to determine if URI is supported.
        /// </summary>
        public static LspUri FromJsonElement(JsonElement rootOrUriToken, ILspLogger? logger = null)
        {
            try
            {
                Uri? uri = null;

                // Try direct URI deserialization first
                if (rootOrUriToken.ValueKind == JsonValueKind.String)
                {
                    uri = JsonSerializer.Deserialize<Uri>(rootOrUriToken);
                }
                else if (rootOrUriToken.ValueKind == JsonValueKind.Object)
                {
                    // Look for textDocument.uri pattern
                    if (rootOrUriToken.TryGetProperty("textDocument", out var textDocumentToken) ||
                        rootOrUriToken.TryGetProperty("_vs_textDocument", out textDocumentToken))
                    {
                        var uriToken = textDocumentToken.GetProperty("uri");
                        uri = JsonSerializer.Deserialize<Uri>(uriToken);
                    }
                    else if (rootOrUriToken.TryGetProperty("data", out var dataToken))
                    {
                        // Handle resolve data pattern - simplified extraction
                        if (dataToken.TryGetProperty("TextDocument", out var dataTextDocToken) &&
                            dataTextDocToken.TryGetProperty("uri", out var dataUriToken))
                        {
                            uri = JsonSerializer.Deserialize<Uri>(dataUriToken);
                        }
                    }
                    else if (rootOrUriToken.TryGetProperty("uri", out var directUriToken))
                    {
                        // Direct uri property
                        uri = JsonSerializer.Deserialize<Uri>(directUriToken);
                    }
                }

                if (uri != null)
                {
                    return CreateFromUri(uri, logger);
                }

                // No URI found in structure
                return new UnsupportedLspUri(new Uri("about:blank"), "unknown", UnsupportedReasonCodes.ParseError);
            }
            catch (Exception ex) when (ex is JsonException || ex is UriFormatException || ex is InvalidOperationException)
            {
                logger?.LogWarning($"Failed to parse URI from JSON: {ex.Message}");
                return new UnsupportedLspUri(new Uri("about:blank"), "unknown", UnsupportedReasonCodes.ParseError);
            }
        }

        /// <summary>
        /// Creates an LspUri from a System.Uri object.
        /// Always returns a concrete LspUri instance (never null).
        /// Check result.IsSupported to determine if URI is supported.
        /// </summary>
        public static LspUri FromUri(Uri uri, ILspLogger? logger = null)
        {
            return CreateFromUri(uri, logger);
        }

        /// <summary>
        /// Creates an LspUri from a System.Uri, handling all validation.
        /// Private helper method for internal use.
        /// </summary>
        private static LspUri CreateFromUri(Uri uri, ILspLogger? logger = null)
        {
            if (uri == null)
            {
                return new UnsupportedLspUri(new Uri("about:blank"), "unknown", UnsupportedReasonCodes.ParseError);
            }

            if (!uri.IsAbsoluteUri)
            {
                var schemeForError = "unknown";
                try { schemeForError = uri.Scheme ?? "unknown"; } catch { }
                return new UnsupportedLspUri(uri, schemeForError, UnsupportedReasonCodes.NotAbsolute);
            }

            var scheme = uri.Scheme;

            if (SupportedSchemes.Contains(scheme))
            {
                return scheme.ToLowerInvariant() switch
                {
                    "file" => new FileLspUri(uri, scheme),
                    _ => throw new InvalidOperationException($"Supported scheme '{scheme}' not handled in factory")
                };
            }

            // Handle unsupported schemes
            LogUnsupportedSchemeOnce(scheme, uri.OriginalString, logger);
            return new UnsupportedLspUri(uri, scheme, UnsupportedReasonCodes.UnsupportedScheme);
        }

        private static void LogUnsupportedSchemeOnce(string scheme, string sampleRaw, ILspLogger? logger)
        {
            if (logger == null) return;

            lock (LogLock)
            {
                if (LoggedSchemes.Add(scheme))
                {
                    // Truncate sample for logging
                    var truncatedSample = sampleRaw.Length > 100 ? sampleRaw[..97] + "..." : sampleRaw;
                    logger.LogInformation($"UnsupportedSchemeObserved: scheme='{scheme}', sample='{truncatedSample}'");
                }
            }
        }
    }
}
