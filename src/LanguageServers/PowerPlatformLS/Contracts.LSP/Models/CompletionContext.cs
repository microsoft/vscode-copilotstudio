namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public class CompletionContext
    {
        public CompletionTriggerKind TriggerKind { get; init; }

        public string? TriggerCharacter { get; init; }
    }
}