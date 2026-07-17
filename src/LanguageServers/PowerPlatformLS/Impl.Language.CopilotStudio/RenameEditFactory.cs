namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Range = Contracts.Lsp.Models.Range;

    internal static class RenameEditFactory
    {
        private static readonly Regex ValidNameRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        public static bool IsValidNewName(string? newName) => !string.IsNullOrEmpty(newName) && ValidNameRegex.IsMatch(newName!);

        public static bool RangeContains(Range range, Position position)
        {
            var afterStart = position.Line > range.Start.Line || (position.Line == range.Start.Line && position.Character >= range.Start.Character);
            var beforeEnd = position.Line < range.End.Line || (position.Line == range.End.Line && position.Character <= range.End.Character);
            return afterStart && beforeEnd;
        }

        public static WorkspaceEdit Build(IReadOnlyList<GlobalVariableReference> references, string newName, Uri? definitionUri)
        {
            var operations = new List<IFileOperation>();

            foreach (var group in references.GroupBy(reference => reference.SourceUri))
            {
                operations.Add(new TextDocumentEdit
                {
                    TextDocument = new VersionedTextDocumentIdentifier { Uri = group.Key },
                    Edits = group.Select(reference => new TextEdit { Range = reference.Range, NewText = newName }).ToArray(),
                });
            }

            if (definitionUri != null)
            {
                var newUri = ChangeFileName(definitionUri, newName);
                if (newUri != definitionUri)
                {
                    operations.Add(new RenameFile { OldUri = definitionUri, NewUri = newUri });
                }
            }

            return new WorkspaceEdit { DocumentChanges = operations.ToArray() };
        }

        internal static Uri ChangeFileName(Uri sourceUri, string newVariableName)
        {
            var sourceFilePath = sourceUri.ToFilePath();
            var sourceExtension = WorkspacePath.GetExtension(sourceFilePath);
            var currentName = sourceFilePath.FileNameWithoutExtension;
            var lastSeparator = currentName.LastIndexOf('.');
            var newName = lastSeparator >= 0 ? string.Concat(currentName.Substring(0, lastSeparator + 1), newVariableName) : newVariableName;
            var newFilePath = sourceFilePath.ParentDirectoryPath.GetChildFilePath($"{newName}{sourceExtension}");
            return new Uri(newFilePath.ToString());
        }
    }
}
