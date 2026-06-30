namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;

    /// <summary>
    /// Context for requests handled by <see cref="ILanguageAbstraction"/> or method handler.
    /// </summary>
    /// <remarks>
    /// This is declared a read-only struct to mimic Roslyn's https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Protocol/Handler/RequestContext.cs.
    /// Roslyn benefits from the property of **Value Copy Semantics**:
    /// Copying a struct ensures that each method receives its own independent copy, which can help avoid issues related to shared state and **make the code easier to reason about**.
    /// We should measure the trade-off between performance and code clarity for our implementation.
    /// </remarks>
    public readonly struct RequestContext : IHasAgentName
    {
        private readonly ILanguageAbstraction? _language;
        private readonly Workspace? _workspace;
        private readonly LspDocument? _document;

        public RequestContext()
        {
            _language = null;
            _workspace = null;
            _document = null;
        }

        public RequestContext(
            ILanguageAbstraction languageAnalyzer,
            Workspace workspace,
            LspDocument? document,
            int index)
        {
            _language = languageAnalyzer;
            _workspace = workspace;
            _document = document;
            Index = index;
        }

        public Workspace Workspace => _workspace ?? throw new InvalidDataException("Operating on invalid context.");
        public LspDocument Document => _document ?? throw new InvalidDataException("Operating on invalid context.");
        public ILanguageAbstraction Language => _language ?? throw new InvalidDataException("Operating on invalid context.");
        public bool IsUnsupported => _language == null;
        public int Index { get; }
        public bool IsInvalid => _document == null || Index < 0;

        /// <summary>
        /// Extracts the agent folder name from the workspace path (last path segment).
        /// </summary>
        public string? AgentName
        {
            get
            {
                if (_workspace == null) return null;
                var path = _workspace.FolderPath.ToString();
                if (path.Length < 2) return path;
                var trimmed = path.TrimEnd('/');
                var lastSlash = trimmed.LastIndexOf('/');
                return lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
            }
        }
    }
}
