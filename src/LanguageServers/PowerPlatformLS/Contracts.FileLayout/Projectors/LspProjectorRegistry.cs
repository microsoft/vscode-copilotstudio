namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel.FileProjection;

    /// <summary>
    /// Shared projector registry for Language Server usage.
    /// </summary>
    /// <remarks>
    /// <para>For projection operations, prefer using <see cref="LspProjectorService"/> which
    /// applies legacy behavior. This registry provides direct access to ObjectModel projectors.</para>
    /// </remarks>
    internal static class LspProjectorRegistry
    {
        /// <summary>
        /// The ObjectModel's default projector registry.
        /// </summary>
        internal static readonly IProjectorRegistry Instance = DefaultProjectorRegistry.Instance;
    }
}
