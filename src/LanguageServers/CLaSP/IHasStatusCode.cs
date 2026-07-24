namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    /// <summary>
    /// Marker interface for handler responses that carry an application-level status code.
    /// When a handler returns a response implementing this interface with a non-success code,
    /// the request queue logs the outcome as Failure rather than Completed.
    /// </summary>
    public interface IHasStatusCode
    {
        int Code { get; }

        bool IsSuccess => Code is >= 200 and < 300;
    }
}
