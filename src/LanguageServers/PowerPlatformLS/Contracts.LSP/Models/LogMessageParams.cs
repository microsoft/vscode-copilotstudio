namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// Parameters for the LSP <c>window/logMessage</c> notification. The client
    /// uses <see cref="Type"/> to render the message at the matching severity
    /// (Error = 1, Warning = 2, Info = 3, Log = 4, Debug = 5).
    /// </summary>
    public class LogMessageParams
    {
        public int Type { get; set; }

        public string Message { get; set; } = string.Empty;
    }
}
