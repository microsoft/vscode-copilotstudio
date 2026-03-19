namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;

    internal class LspMethodHandlerAttribute : LanguageServerEndpointAttribute
    {
        public LspMethodHandlerAttribute(string method)
            : base(method, Constants.LanguageIds.Yaml)
        {
        }
    }
}