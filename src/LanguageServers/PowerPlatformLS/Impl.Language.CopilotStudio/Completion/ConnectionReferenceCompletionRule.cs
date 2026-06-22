// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Range = Microsoft.PowerPlatformLS.Contracts.Lsp.Models.Range;

    /// <summary>
    /// IntelliSense completions for connectionReference values in agent component files.
    /// </summary>
    internal sealed class ConnectionReferenceCompletionRule : ICompletionRule<McsLspDocument>
    {
        private const string DeclareCommand = "microsoft-copilot-studio.declareConnectionReference";
        private static readonly Regex ConnectionReferenceLineBeforeCursor = new(@"^[ \t]*connectionReference:[ \t]*[^\s#'""]*$", RegexOptions.Compiled);
        private static readonly Regex ConnectionReferenceFullLine = new(@"^(?<prefix>[ \t]*connectionReference:[ \t]*)(?<value>[^\s#'""]*)", RegexOptions.Compiled);
        private readonly IFileAccessorFactory _fileAccessorFactory;

        public IEnumerable<string>? CharacterTriggers { get; } = [":", ".", " "];

        public ConnectionReferenceCompletionRule(IFileAccessorFactory fileAccessorFactory)
        {
            _fileAccessorFactory = fileAccessorFactory ?? throw new ArgumentNullException(nameof(fileAccessorFactory));
        }

        public IEnumerable<CompletionItem> ComputeCompletion(RequestContext requestContext, CompletionContext triggerContext)
        {
            var document = requestContext.Document?.As<McsLspDocument>();
            if (document == null)
            {
                yield break;
            }

            var text = document.Text;
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            var index = requestContext.Index;
            if (index < 0 || index > text.Length)
            {
                yield break;
            }

            var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            var lineBeforeCursor = text.Substring(lineStart, index - lineStart);
            if (!ConnectionReferenceLineBeforeCursor.IsMatch(lineBeforeCursor))
            {
                yield break;
            }

            var workspace = requestContext.Workspace;
            if (workspace == null)
            {
                yield break;
            }

            var entries = ConnectionsCacheReader.ReadConnections(_fileAccessorFactory, workspace.FolderPath);
            if (entries == null)
            {
                yield break;
            }

            foreach (var entry in entries)
            {
                var logicalName = entry.ConnectionReferenceLogicalName;
                if (string.IsNullOrEmpty(logicalName))
                {
                    continue;
                }

                var status = entry.BoundConnectionExists ? "bound" : entry.IsDeclared ? "not bound" : "not declared";
                var connector = string.IsNullOrEmpty(entry.ConnectorName) ? "connection" : entry.ConnectorName;

                var item = new CompletionItem
                {
                    Label = logicalName!,
                    Kind = CompletionKind.Reference,
                    Detail = $"{connector} ({status})",
                    FilterText = logicalName,
                    InsertText = logicalName,
                    TextEdit = new TextEdit { Range = ComputeValueRange(text, lineStart, index), NewText = logicalName! },
                    SortText = entry.IsDeclared ? "0" : "1",
                };

                if (!entry.IsDeclared)
                {
                    item.Command = new LspCommand
                    {
                        Title = "Declare connection reference",
                        Command = DeclareCommand,
                        Arguments = new object[] { logicalName! },
                    };
                }

                yield return item;
            }
        }

        private static Range ComputeValueRange(string text, int lineStart, int index)
        {
            var lineEnd = text.IndexOf('\n', index);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var match = ConnectionReferenceFullLine.Match(text.Substring(lineStart, lineEnd - lineStart));
            var valueColumn = match.Success ? match.Groups["value"].Index : index - lineStart;
            var valueLength = match.Success ? match.Groups["value"].Length : 0;

            var lineNumber = 0;
            for (int i = 0; i < lineStart && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lineNumber++;
                }
            }

            return new Range
            {
                Start = new Position { Line = lineNumber, Character = valueColumn },
                End = new Position { Line = lineNumber, Character = valueColumn + valueLength },
            };
        }
    }
}
