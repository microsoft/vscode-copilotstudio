
namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Exceptions
{
    public class UnsupportedLspDocumentTypeException : Exception
    {
        public UnsupportedLspDocumentTypeException(string currentDocumentTypeName, string expectedDocumentTypeName)
            : base($"Unsupported document type: {currentDocumentTypeName}. Current language supports: {expectedDocumentTypeName}")
        {
        }
    }
}