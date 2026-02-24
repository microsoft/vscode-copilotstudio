namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class RequestContextFactory : AbstractRequestContextFactory<RequestContext>
    {
        private readonly ILspServices _lspServices;
        private readonly IRequestContextResolver _requestContextResolver;

        public RequestContextFactory(ILspServices lspServices, IRequestContextResolver requestContextResolver)
        {
            _lspServices = lspServices;
            _requestContextResolver = requestContextResolver;
        }

        public override Task<RequestContext> CreateRequestContextAsync<TRequestParam>(IQueueItem<RequestContext> queueItem, IMethodHandler methodHandler, TRequestParam requestParam, CancellationToken cancellationToken)
        {
            var logger = _lspServices.GetRequiredService<ILspLogger>();
            var requestContext = InternalCreateRequestContext(requestParam, logger);
            return Task.FromResult(requestContext);
        }

        private RequestContext InternalCreateRequestContext<TRequestParam>(TRequestParam requestParam, ILspLogger logger)
        {
            switch (requestParam)
            {
                case IHasWorkspace workspaceRequest:
                    return _requestContextResolver.Resolve(workspaceRequest);
                case TextDocumentPositionParams textIdPositionParams:
                    return _requestContextResolver.Resolve(textIdPositionParams.TextDocument, textIdPositionParams.Position);
                case TextDocumentIdentifierParams textIdParams:
                    return _requestContextResolver.Resolve(textIdParams.TextDocument);
                case VersionedTextDocumentIdentifierParams verTextIdParams:
                    return _requestContextResolver.Resolve(verTextIdParams.TextDocument);
                case TextDocumentItemParams textIdParams:
                    return _requestContextResolver.Resolve(textIdParams.TextDocument);
                case IDefaultContextRequest:
                case NoValue:
                    // work on default context by design
                    break;
                default:
                    logger.LogWarning($"RequestContextFactory: Unable to resolve request context for {requestParam?.GetType().ToString() ?? Constants.Null}. Using default context.");
                    break;
            }

            return new RequestContext();
        }
    }
}