namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Validation
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Range = Microsoft.PowerPlatformLS.Contracts.Lsp.Models.Range;

    internal sealed class ConnectionReferenceValidationRule : IValidationRule<McsLspDocument>
    {
        private const string UnknownConnectionReferenceCode = "UnknownConnectionReference";
        private const string UndeclaredConnectionReferenceCode = "UndeclaredConnectionReference";
        private const string UnboundConnectionReferenceCode = "UnboundConnectionReference";
        private static readonly Regex ConnectionReferenceLine = new(@"^[ \t]*connectionReference:[ \t]*(?:'(?<value>[^']*)'|""(?<value>[^""]*)""|(?<value>[^\s#'\""]+))", RegexOptions.Compiled);
        private readonly IFileAccessorFactory _fileAccessorFactory;

        public ConnectionReferenceValidationRule(IFileAccessorFactory fileAccessorFactory)
        {
            if (fileAccessorFactory == null)
            {
                throw new ArgumentNullException(nameof(fileAccessorFactory));
            }

            _fileAccessorFactory = fileAccessorFactory;
        }

        IEnumerable<Diagnostic> IValidationRule<McsLspDocument>.ComputeValidation(RequestContext context, McsLspDocument document)
        {
            if (document == null)
            {
                yield break;
            }

            var workspace = context.Workspace;
            if (workspace == null)
            {
                yield break;
            }

            var catalog = ReadCatalog(workspace.FolderPath);
            if (catalog == null)
            {
                yield break;
            }

            var text = document.Text;
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            var lines = text.Split('\n');
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var match = ConnectionReferenceLine.Match(lines[lineIndex]);
                if (!match.Success)
                {
                    continue;
                }

                var valueGroup = match.Groups["value"];
                var value = valueGroup.Value;
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var range = new Range
                {
                    Start = new Position { Line = lineIndex, Character = valueGroup.Index },
                    End = new Position { Line = lineIndex, Character = valueGroup.Index + value.Length },
                };

                if (!catalog.TryGetValue(value, out var entry))
                {
                    yield return new Diagnostic
                    {
                        Code = UnknownConnectionReferenceCode,
                        Range = range,
                        Severity = DiagnosticSeverity.Error,
                        Message = $"Connection reference '{value}' was not found among the agent's connections. Use Manage Connections to bind it to an existing connection or create a new one.",
                    };
                }
                else if (!entry.IsDeclared)
                {
                    yield return new Diagnostic
                    {
                        Code = UndeclaredConnectionReferenceCode,
                        Range = range,
                        Severity = DiagnosticSeverity.Error,
                        Message = $"Connection reference '{value}' is used but not declared in this agent. Use Manage Connections to declare and bind it before pushing.",
                    };
                }
                else if (!entry.BoundConnectionExists)
                {
                    yield return new Diagnostic
                    {
                        Code = UnboundConnectionReferenceCode,
                        Range = range,
                        Severity = DiagnosticSeverity.Warning,
                        Message = $"Connection reference '{value}' is not bound to an existing connection. Use Manage Connections to bind it to an existing connection or create a new one.",
                    };
                }
            }
        }

        private Dictionary<string, ConnectionCacheEntry>? ReadCatalog(DirectoryPath folderPath)
        {
            var entries = ConnectionsCacheReader.ReadConnections(_fileAccessorFactory, folderPath);
            if (entries == null)
            {
                return null;
            }

            var result = new Dictionary<string, ConnectionCacheEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var logicalName = entry?.ConnectionReferenceLogicalName;
                if (!string.IsNullOrEmpty(logicalName))
                {
                    result[logicalName!] = entry!;
                }
            }

            return result;
        }
    }
}
