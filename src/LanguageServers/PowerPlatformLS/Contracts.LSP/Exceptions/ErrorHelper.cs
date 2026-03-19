namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Exceptions
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Text.Json;

    public static class ErrorHelper
    {
        public static JsonRpcResponse CreateErrorResponseFromException(this Exception exception, int? id = null)
        {
            return exception switch
            {
                ParseException or MissingIdForLspParsingException => new JsonRpcResponse
                {
                    Id = id,
                    Error = new JsonRpcError
                    {
                        Code = ErrorCodes.ParseError,
                        Message = exception.Message,
                    }
                },
                _ => new JsonRpcResponse
                {
                    Id = id,
                    Error = new JsonRpcError
                    {
                        Code = ErrorCodes.InternalError,
                        Message = exception.Message,
                    }
                }
            };
        }
    }
}