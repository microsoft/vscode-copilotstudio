namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution
{

    /// <summary>
    /// Converts between the ${workspace} placeholder used in journal JSON
    /// and the absolute file:/// URI resolved at runtime.
    /// </summary>
    public static class UriPlaceholder
    {
        /// <summary>
        /// The token that represents the workspace root URI in serialized journals.
        /// </summary>
        public const string Token = "${workspace}";

        /// <summary>
        /// Replace <c>${workspace}</c> with the resolved absolute <c>file:///</c> URI
        /// in a raw JSON string. Used at load time before sending to the server.
        /// </summary>
        public static string Expand(string json, string resolvedWorkspaceUri) =>
            json.Replace(Token, resolvedWorkspaceUri);

        /// <summary>
        /// Replace the resolved absolute <c>file:///</c> URI prefix with <c>${workspace}</c>
        /// in a raw JSON string. Used before comparison and before write-back.
        /// </summary>
        public static string Relativize(string json, string resolvedWorkspaceUri) =>
            json.Replace(resolvedWorkspaceUri, Token);
    }
}