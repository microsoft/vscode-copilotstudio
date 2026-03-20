
namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// File event type enum.
    /// <para>
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#fileChangeType">Language Server Protocol specification</see> for additional information.
    /// </para>
    /// </summary>
    public enum FileChangeType
    {
        /// <summary>
        /// File was created.
        /// </summary>
        Created = 1,

        /// <summary>
        /// File was changed.
        /// </summary>
        Changed = 2,

        /// <summary>
        /// File was deleted.
        /// </summary>
        Deleted = 3,
    }

}