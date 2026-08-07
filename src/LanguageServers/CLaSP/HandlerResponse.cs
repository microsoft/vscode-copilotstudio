namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    /// <summary>
    /// Base class for handler responses that carry an application-level status code.
    /// When a handler returns a <see cref="HandlerResponse"/> with a non-success code,
    /// the request queue logs the outcome as Failure and includes the error message.
    /// </summary>
    public abstract class HandlerResponse
    {
        /// <summary>
        /// The status code of the response.
        /// </summary>
        public int Code { get; set; }

        /// <summary>
        /// The error message associated with the response when the status code is not 200.
        /// </summary>
        public string? Message { get; set; }

        public bool IsSuccess => Code is >= 200 and < 300;
    }
}
