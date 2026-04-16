namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Globalization;

    /// <summary>
    /// Contract definition for language abstraction. Provide empty default implementation to ease integration.
    /// Allows concrete language to only implement a subset of IDE features at any given time.
    /// <br /><br />
    /// This is one area where Power Platform LS is different from Roslyn language server.
    /// Roslyn workspace supports multipe languages.
    /// PowerPlatform language abstraction is different. Each language are analyzed in isolation from each other.
    /// Hence, each language as a distinct virtual workspace where the files matching that language definition are stored.
    /// This way, we can create a "compilation" for a workspace, the same way Roslyn does, without having to worry about cross languages interactions.
    /// </summary>
    public interface ILanguageAbstraction
    {
        private static readonly Workspace DefaultWorkspace = new(new DirectoryPath(string.Empty));

        LanguageType LanguageType { get; }

        LspDocument CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath workspacePath);

        Workspace ResolveWorkspace(FilePath documentPath) => ResolveWorkspace(documentPath.ParentDirectoryPath);

        Workspace ResolveWorkspace(DirectoryPath directoryPath)
        {
            // A default workspace implementation is provided for languages that do not require cross-files semantics (or references).
            return DefaultWorkspace;
        }

        IEnumerable<Workspace> Workspaces => [DefaultWorkspace];

        bool IsValidAgentDirectory(DirectoryPath documentDirectory, out DirectoryPath validDirectory);
    }
}