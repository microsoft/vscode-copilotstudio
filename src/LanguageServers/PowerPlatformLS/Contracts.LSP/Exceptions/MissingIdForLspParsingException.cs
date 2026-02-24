namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Exceptions
{
    internal sealed class MissingIdForLspParsingException : Exception
    {
        public MissingIdForLspParsingException(string methodName) : base($"'{methodName}' method requires an id")
        {
        }
    }
}