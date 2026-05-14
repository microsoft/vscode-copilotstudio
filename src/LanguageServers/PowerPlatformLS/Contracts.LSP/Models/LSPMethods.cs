namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public static class LspMethods
    {
        #region Language Features
        public const string GoToDefinition = "textDocument/definition";
        public const string Completion = "textDocument/completion";
        public const string SignatureHelp = "textDocument/signatureHelp";
        public const string Initialize = "initialize";
        public const string SemanticTokensFull = "textDocument/semanticTokens/full";
        public const string CodeAction = "textDocument/codeAction";
        public const string ListWorkspaces = "workspace/listWorkspaces";
        public const string GetWorkspaceDetails = "workspace/getWorkspaceDetails";
        #endregion

        #region Document Sync
        public const string DidOpen = "textDocument/didOpen";
        public const string DidClose = "textDocument/didClose";
        public const string DidChange = "textDocument/didChange";
        public const string Diagnostics = "textDocument/publishDiagnostics";
        public const string DidSave = "textDocument/didSave";
        public const string DidRename = "workspace/didRenameFiles";
        #endregion

        #region Workspace Files Watching
        public const string DidChangeWatchedFiles = "workspace/didChangeWatchedFiles";
        #endregion

        #region Notifications
        public const string Initialized = "initialized";
        public const string Shutdown = "shutdown";
        public const string Exit = "exit";

        // Custom log notification. We deliberately do NOT use the standard
        // window/logMessage because vscode-languageclient intercepts that and
        // writes its own "[Error - h:mm:ss AM]" prefix via appendLine, which
        // the LogOutputChannel then wraps with another "[info]" prefix,
        // producing duplicated/mismatched levels in the output panel.
        public const string PowerPlatformLogMessage = "powerplatformls/logMessage";
        #endregion
    }
}