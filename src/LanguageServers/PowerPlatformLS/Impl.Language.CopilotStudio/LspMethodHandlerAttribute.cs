namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;

    internal class LspMethodHandlerAttribute : LanguageServerEndpointAttribute
    {
        public LspMethodHandlerAttribute(string methodName) : base(methodName, Constants.LanguageIds.CopilotStudio)
        {
        }
    }
}
