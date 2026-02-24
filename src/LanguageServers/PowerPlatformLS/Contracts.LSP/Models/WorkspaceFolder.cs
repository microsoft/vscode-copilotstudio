
namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    /// <summary>Describes a workspace folder</summary>
    /// <remarks>
    /// Since LSP 3.6.
    /// Borrowed from https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Protocol/Protocol/WorkspaceFolder.cs on Feb 2025.
    /// </remarks>
    public sealed class WorkspaceFolder
    {
        /// <summary>
        /// The URI for this workspace folder.
        /// </summary>
        [JsonRequired]
        public required Uri Uri { get; init; }

        /// <summary>
        /// The name of the workspace folder used in the UI.
        /// </summary>
        [JsonRequired]
        public required string Name { get; init; }
    }

}