namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    internal interface ICapabilitiesProvider
    {
        IReadOnlySet<string> TriggerCharacters { get; }
    }
}