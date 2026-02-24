namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class CompletionOptions
    {
        /// <summary>
        /// The additional characters, beyond the defaults provided by the client (typically
        /// [a-zA-Z]), that should automatically trigger a completion request. For example
        /// `.` in JavaScript represents the beginning of an object property or method and is
        /// thus a good candidate for triggering a completion request.
        /// <br/><br/>
        /// Most tools trigger a completion request automatically without explicitly
        /// requesting it using a keyboard shortcut (e.g. Ctrl+Space). Typically they
        /// do so when the user starts to type an identifier. For example if the user
        /// types `c` in a JavaScript file code complete will automatically pop up
        /// present `console` besides others as a completion item. Characters that
        /// make up identifiers don't need to be listed here.
        /// <br/><br/>
        /// Copied from https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#textDocument_didOpen to avoid misunderstandings: completion events are not limited to those characters.
        /// </summary>
        public string[]? TriggerCharacters { get; set; } = [];

        public bool ResolveProvider { get; set; } = false;

        public string[]? AllCommitCharacters { get; set; } = [];
    }
}