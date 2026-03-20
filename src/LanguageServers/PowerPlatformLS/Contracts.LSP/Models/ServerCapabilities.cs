namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// Subset of server capabilities currently supported for some scenarios.
    /// See https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#serverCapabilities for extensibility.
    /// </summary>
    public sealed class ServerCapabilities
    {
        public string? PositionEncoding { get; set; } = null;

        public TextDocumentSyncOptions? TextDocumentSync { get; set; } = null;

        public CompletionOptions? CompletionProvider { get; set; } = null;

        public SignatureHelpOptions? SignatureHelpProvider { get; set; } = null;

        public bool DefinitionProvider { get; set; } = false;

        public SemanticTokensOptions? SemanticTokensProvider { get; set; } = null;

        public CodeActionOptions? CodeActionProvider { get; set; } = null;

        public ServerWorkspaceCapabilities? Workspace { get; set; } = null;
    }
}