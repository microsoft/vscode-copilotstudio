namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    public interface IClientInformationInitializer
    {
        void Initialize(InitializeParams request);
    }
}
