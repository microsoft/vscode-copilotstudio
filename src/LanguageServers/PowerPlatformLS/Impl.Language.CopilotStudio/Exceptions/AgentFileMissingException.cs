namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class AgentFileMissingException : McsException
    {
        private readonly IClientInformation _clientInfo;

        public AgentFileMissingException(IClientInformation clientInfo) : base("Agent file is missing.", DiagnosticSeverity.Warning)
        {
            _clientInfo = clientInfo;
        }

        protected override DiagnosticData GetDiagnosticData(LspDocument document, RequestContext context)
        {
            CodeAction createAgentFileAction = GetCreateAgentFileAction(context);
            IEnumerable<CodeAction> moveFileActions = GetMoveCurrentDocumentToOtherAgentActions(document, context);
            return new DiagnosticData
            {
                Quickfix = new[] { createAgentFileAction }.Concat(moveFileActions).ToArray(),
            };
        }

        private CodeAction GetCreateAgentFileAction(RequestContext context)
        {
            const string AgentFileTemplate = "instructions: ";
            var agentFileUri = new Uri(context.Workspace.FolderPath.GetChildFilePath("agent.mcs.yml").ToString());
            return new CodeAction
            {
                Title = $"Create agent.mcs.yml",
                Kind = CodeActionKind.QuickFix,
                IsPreferred = true,
                Edit = new WorkspaceEdit
                {
                    DocumentChanges =
                    [
                        new CreateFile
                        {
                            Uri = agentFileUri,
                        },
                        new TextDocumentEdit
                        {
                            TextDocument = new VersionedTextDocumentIdentifier
                            {
                                Uri = agentFileUri,
                            },
                            Edits =
                            [
                                new TextEdit
                                {
                                    NewText = AgentFileTemplate,
                                    Range = Range.Zero,
                                },
                            ],
                        },
                    ],
                },
            };
        }

        private IEnumerable<CodeAction> GetMoveCurrentDocumentToOtherAgentActions(LspDocument document, RequestContext context)
        {
            const int MaxMoveActions = 5;
            int moveActionsCount = 0;
            foreach (var agentDirectory in context.Language.Workspaces.Except([context.Workspace]))
            {
                var agentDirectoryPath = agentDirectory.FolderPath;
                var agentDirectoryRelativePath = _clientInfo.GetRelativePath(agentDirectoryPath);
                var currentAgentRootPath = context.Workspace.FolderPath;
                var currentDocumentPath = document.FilePath;
                var documentRelativePath = currentAgentRootPath.Contains(currentDocumentPath) ? currentDocumentPath.GetRelativeTo(currentAgentRootPath).ToString() : currentDocumentPath.FileName;
                var newDocumentUri = new Uri(agentDirectoryPath.GetChildFilePath(documentRelativePath).ToString());
                yield return new CodeAction
                {
                    Kind = CodeActionKind.QuickFix,
                    Title = $"Move file to {agentDirectoryRelativePath}",
                    Edit = new WorkspaceEdit
                    {
                        DocumentChanges =
                        [
                            new RenameFile
                            {
                                OldUri = document.Uri,
                                NewUri = newDocumentUri,
                            },
                        ],
                    },
                };
                if (++moveActionsCount >= MaxMoveActions)
                {
                    break;
                }
            }
        }
    }
}
