namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using System;
    using System.Collections.Generic;

    public interface IWorkspaceCompiler<T>
    {
        /// <summary>
        /// Compile the workspace documents into a compilation model.
        /// </summary>
        /// <param name="documents">Documents to compile.</param>
        /// <param name="workspacePath">Workspace directory path, in case additional content needs to be loaded upon compilation.</param>
        /// <param name="isFull">Whether the compilation model contains all metadata, which may invalidate syntax nodes.</param>
        /// <returns>The compilation result.</returns>
        Compilation<T> Compile(IReadOnlyDictionary<FilePath, LspDocument> documents, DirectoryPath workspacePath, bool isFull = false);
    }
}