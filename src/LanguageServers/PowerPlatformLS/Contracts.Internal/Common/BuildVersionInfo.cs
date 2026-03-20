namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// For logging, describe git info that we were built against.
    /// Add this to the service collection. 
    /// </summary>
    public class BuildVersionInfo
    {
        /// <summary>
        /// Git commit Hash of the current version.
        /// Useful for logging. 
        /// </summary>
        public string? Hash { get; init; }

        /// <summary>
        /// Version of vsix. Like "1.2.3".
        /// This should correlate with git hash.
        /// </summary>
        public string? VsixVersion { get; init; }
    }
}
