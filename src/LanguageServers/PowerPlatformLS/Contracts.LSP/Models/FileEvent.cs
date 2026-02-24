
namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Class which represents a file change event.
    /// <para>
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#fileEvent">Language Server Protocol specification</see> for additional information.
    /// </para>
    /// </summary>
    public sealed class FileEvent
    {
        /// <summary>
        /// Gets or sets the URI of the file.
        /// </summary>
        public required Uri Uri { get; set; }

        /// <summary>
        /// Gets or sets the file change type.
        /// </summary>
        public FileChangeType Type
        {
            get;
            set;
        }
    }

}