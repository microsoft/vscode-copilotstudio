namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// Parameters for the LSP <c>window/logMessage</c> notification.
    /// <see cref="Type"/>: Error=1, Warning=2, Info=3, Trace=4, Debug=5.
    /// </summary>
    public class LogMessageParams
    {
        public int Type { get; set; }

        public string Message { get; set; } = string.Empty;
    }
}
