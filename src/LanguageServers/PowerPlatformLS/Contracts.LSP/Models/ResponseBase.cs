namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public abstract class ResponseBase
    {
        /// <summary>
        /// The status code of the response.
        /// </summary>
        public required int Code { get; set; }

        /// <summary>
        /// The error message associated with the response when the status code is not 200.
        /// </summary>
        public string? Message { get; set; }
    }
}
