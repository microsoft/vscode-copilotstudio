namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(LspMethods.References, LanguageServerConstants.DefaultLanguageName)]
    internal sealed class DefaultReferencesHandler : IRequestHandler<ReferenceParams, Location[]?, RequestContext>
    {
        public bool MutatesSolutionState => false;

        public Task<Location[]?> HandleRequestAsync(ReferenceParams request, RequestContext context, CancellationToken cancellationToken) => Task.FromResult<Location[]?>(null);
    }

    [LanguageServerEndpoint(LspMethods.PrepareRename, LanguageServerConstants.DefaultLanguageName)]
    internal sealed class DefaultPrepareRenameHandler : IRequestHandler<TextDocumentPositionParams, PrepareRenameResult?, RequestContext>
    {
        public bool MutatesSolutionState => false;

        public Task<PrepareRenameResult?> HandleRequestAsync(TextDocumentPositionParams request, RequestContext context, CancellationToken cancellationToken) => Task.FromResult<PrepareRenameResult?>(null);
    }

    [LanguageServerEndpoint(LspMethods.Rename, LanguageServerConstants.DefaultLanguageName)]
    internal sealed class DefaultRenameHandler : IRequestHandler<RenameParams, WorkspaceEdit?, RequestContext>
    {
        public bool MutatesSolutionState => false;

        public Task<WorkspaceEdit?> HandleRequestAsync(RenameParams request, RequestContext context, CancellationToken cancellationToken) => Task.FromResult<WorkspaceEdit?>(null);
    }
}
