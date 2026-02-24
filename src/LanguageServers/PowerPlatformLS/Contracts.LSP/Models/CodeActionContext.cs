namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// See https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#codeActionContext
    /// </summary>
    public sealed class CodeActionContext
    {
        public required Diagnostic[] Diagnostics { get; set; }
        public string[]? Only { get; set; }
        public CodeActionTriggerKind? TriggerKind { get; set; }
    }
}