// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    /// <summary>
    /// Optional interface for request contexts that can provide an agent name
    /// for inclusion in handler start/end log messages.
    /// </summary>
    public interface IHasAgentName
    {
        /// <summary>
        /// Returns the agent name for logging, or null if not available.
        /// </summary>
        string? AgentName { get; }
    }
}
