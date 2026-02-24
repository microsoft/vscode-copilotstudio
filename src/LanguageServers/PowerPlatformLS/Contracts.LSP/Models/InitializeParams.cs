namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Class which represents the parameter sent with an initialize method request.
    /// <para>
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#initializeParams">Language Server Protocol specification</see> for additional information.
    /// </para>
    /// </summary>
    public sealed class InitializeParams : IDefaultContextRequest
    {
        /// <summary>
        /// Gets or sets the ID of the process which launched the language server.
        /// </summary>
        public int? ProcessId
        {
            get;
            set;
        }

        /// <summary>
        /// Information about the client.
        /// </summary>
        /// <remarks>Since LSP 3.15</remarks>
        public ClientInfo? ClientInfo { get; set; }

        /// <summary>
        /// Gets or sets the locale the client is currently showing the user interface in.
        /// This must not necessarily be the locale of the operating system.
        ///
        /// Uses IETF language tags as the value's syntax.
        /// (See https://en.wikipedia.org/wiki/IETF_language_tag)
        /// </summary>
        /// <remarks>Since LSP 3.16</remarks>
        public string? Locale
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the workspace root path.
        /// </summary>
        [Obsolete("Deprecated in favor of RootUri")]
        public string? RootPath
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the workspace root path. Take precedence over <see cref="RootPath"/> if both are set.
        /// </summary>
        [Obsolete("Deprecated in favor of WorkspaceFolders")]
        public Uri? RootUri
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the initialization options as specified by the client.
        /// </summary>
        public object? InitializationOptions
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the capabilities supported by the client.
        /// </summary>
        public required ClientCapabilities Capabilities
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the initial trace setting.
        /// </summary>
        public string? Trace { get; set; } = "off";

        /// <summary>
        /// Workspace folders configured in the client when the server starts.
        /// <para>
        /// An empty array indicates that the client supports workspace folders but none are open,
        /// and <see langword="null"/> indicates that the client does not support workspace folders.
        /// </para>
        /// <para>
        /// Note that this is a minor change from the raw protocol, where if the property is present in JSON but <see langword="null"/>,
        /// it is equivalent to an empty array value. This distinction cannot easily be represented idiomatically in .NET,
        /// but is not important to preserve.
        /// </para>
        /// </summary>
        /// <remarks>Since LSP 3.6</remarks>
        public WorkspaceFolder[]? WorkspaceFolders { get; init; }
    }
}