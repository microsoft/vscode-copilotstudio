

namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Exceptions
{
    public sealed class ParseException(string msg) : Exception(msg)
    {
    }
}