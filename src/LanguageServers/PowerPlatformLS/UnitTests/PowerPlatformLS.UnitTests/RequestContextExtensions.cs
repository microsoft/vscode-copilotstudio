namespace Microsoft.PowerPlatformLS.UnitTests
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    public static class RequestContextExtensions
    {
        public static TextDocumentPositionParams GetTextDocumentPositionParams(this RequestContext requestContext)
        {
            var doc = requestContext.Document;

            var request = new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = doc.Uri
                },
                Position = doc.MarkResolver.GetPosition(requestContext.Index)
            };

            return request;
        }
    }
}
