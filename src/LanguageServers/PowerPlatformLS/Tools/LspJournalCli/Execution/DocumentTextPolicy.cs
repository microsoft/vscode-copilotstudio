namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution
{
    using System.Security.Cryptography;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Replaces full document text with a file reference and hash for storage,
    /// and rehydrates the text from disk at execution time.
    /// </summary>
    public static class DocumentTextPolicy
    {
        private const string TextRefPrefix = "${file:";

        public static JsonElement? PrepareParamsForExecution(JsonElement? @params, string workspaceRootPath)
        {
            if (!@params.HasValue)
            {
                return null;
            }

            var node = JsonNode.Parse(@params.Value.GetRawText());
            if (node is null)
            {
                return @params;
            }

            if (node is JsonObject obj)
            {
                if (obj["textDocument"] is JsonObject textDocument)
                {
                    ExpandTextDocument(textDocument, workspaceRootPath);
                }

                if (obj["contentChanges"] is JsonArray contentChanges)
                {
                    foreach (var change in contentChanges.OfType<JsonObject>())
                    {
                        ExpandTextNode(change, workspaceRootPath);
                    }
                }
            }

            using var doc = JsonDocument.Parse(node.ToJsonString(Transport.SerializationOptions.Default));
            return doc.RootElement.Clone();
        }

        public static JsonElement? ScrubParamsForStorage(JsonElement? @params, string workspaceRootPath)
        {
            if (!@params.HasValue)
            {
                return null;
            }

            var node = JsonNode.Parse(@params.Value.GetRawText());
            if (node is null)
            {
                return @params;
            }

            if (node is JsonObject obj && obj["textDocument"] is JsonObject textDocument)
            {
                ScrubTextDocument(textDocument, workspaceRootPath);
            }

            using var doc = JsonDocument.Parse(node.ToJsonString(Transport.SerializationOptions.Default));
            return doc.RootElement.Clone();
        }

        private static void ExpandTextDocument(JsonObject textDocument, string workspaceRootPath)
        {
            ExpandTextNode(textDocument, workspaceRootPath);
        }

        private static void ExpandTextNode(JsonObject node, string workspaceRootPath)
        {
            if (node["text"] is not JsonValue textValue)
            {
                return;
            }

            var text = textValue.GetValue<string?>();
            if (!TryParseTextRef(text, out var relPath))
            {
                return;
            }

            var fullPath = ResolveFilePath(workspaceRootPath, relPath);
            var bytes = File.ReadAllBytes(fullPath);

            // Normalize CRLF → LF to match VS Code's didOpen behavior.
            // On Windows, git text=auto checks out CRLF; the LSP server expects LF.
            var content = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
            var normalizedBytes = Encoding.UTF8.GetBytes(content);

            var hash = ComputeSha256Hex(normalizedBytes);
            var expectedHash = node["textHash"]?.GetValue<string?>();
            var expectedBytes = node["textBytes"]?.GetValue<int?>();

            if (expectedHash is null)
            {
                throw new InvalidOperationException($"Missing textHash for '{relPath}'.");
            }

            if (!string.Equals(expectedHash, "sha256:" + hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Text hash mismatch for '{relPath}'. Expected {expectedHash}.");
            }

            if (expectedBytes.HasValue && expectedBytes.Value != normalizedBytes.Length)
            {
                throw new InvalidOperationException($"Text byte length mismatch for '{relPath}'. Expected {expectedBytes}.");
            }

            node["text"] = content;
        }

        private static void ScrubTextDocument(JsonObject textDocument, string workspaceRootPath)
        {
            if (textDocument["text"] is not JsonValue textValue)
            {
                return;
            }

            var text = textValue.GetValue<string?>();
            if (string.IsNullOrEmpty(text) || TryParseTextRef(text, out _))
            {
                return;
            }

            var uri = textDocument["uri"]?.GetValue<string?>();
            var relPath = GetRelativePathFromUri(uri, workspaceRootPath);
            if (relPath is null)
            {
                throw new InvalidOperationException("Cannot scrub document text without a workspace-relative uri.");
            }

            var fullPath = ResolveFilePath(workspaceRootPath, relPath);
            var bytes = File.ReadAllBytes(fullPath);

            // Normalize CRLF → LF for hashing — ensures baselines are OS-independent.
            var normalizedText = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
            var normalizedBytes = Encoding.UTF8.GetBytes(normalizedText);

            var hash = ComputeSha256Hex(normalizedBytes);

            textDocument["text"] = BuildTextRef(relPath);
            textDocument["textHash"] = "sha256:" + hash;
            textDocument["textBytes"] = normalizedBytes.Length;
        }

        private static bool TryParseTextRef(string? text, out string relPath)
        {
            relPath = string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (!text.StartsWith(TextRefPrefix, StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            relPath = text[TextRefPrefix.Length..^1];
            return !string.IsNullOrWhiteSpace(relPath);
        }

        private static string BuildTextRef(string relPath)
        {
            var normalized = relPath.Replace('\\', '/');
            return TextRefPrefix + normalized + "}";
        }

        private static string ResolveFilePath(string workspaceRootPath, string relPath)
        {
            if (relPath.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Invalid document path '{relPath}'.");
            }

            return Path.GetFullPath(Path.Combine(workspaceRootPath, relPath));
        }

        private static string? GetRelativePathFromUri(string? uri, string workspaceRootPath)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return null;
            }

            if (uri.StartsWith("${workspace}/", StringComparison.Ordinal))
            {
                return uri["${workspace}/".Length..];
            }

            if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute) && absolute.IsFile)
            {
                var fullPath = Path.GetFullPath(absolute.LocalPath);
                var rootPath = Path.GetFullPath(workspaceRootPath);
                if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
                }
            }

            return null;
        }

        private static string ComputeSha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(bytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}