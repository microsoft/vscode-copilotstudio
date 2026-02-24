namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.LSP.Models;
    using System;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(LspMethods.ListWorkspaces, LanguageServerConstants.DefaultLanguageName)]
    internal class ListWorkspacesHandler : IRequestHandler<ListWorkspacesResponse, RequestContext>
    {
        private readonly ILanguageProvider _languageProvider;

        public ListWorkspacesHandler(ILanguageProvider languageProvider)
        {
            _languageProvider = languageProvider ?? throw new ArgumentNullException(nameof(languageProvider));
        }

        public bool MutatesSolutionState => false;

        public Task<ListWorkspacesResponse> HandleRequestAsync(RequestContext context, CancellationToken cancellationToken)
        {
            ListWorkspacesResponse response;
            if (_languageProvider.TryGetLanguage(Contracts.Internal.LanguageType.CopilotStudio, out var language))
            {
                response = new ListWorkspacesResponse { WorkspaceUris = language.Workspaces.Select(ws => new Uri(ws.FolderPath.ToString())).ToImmutableArray() };
            }
            else
            {
                response = new ListWorkspacesResponse { };
            }

            return Task.FromResult(response);
        }
    }
}
