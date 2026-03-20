namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;

    class LspMethodHandlerAttribute : LanguageServerEndpointAttribute
    {
        public LspMethodHandlerAttribute(string method) : base (method, Constants.LanguageIds.PowerFx)
        {
        }
    }
}
