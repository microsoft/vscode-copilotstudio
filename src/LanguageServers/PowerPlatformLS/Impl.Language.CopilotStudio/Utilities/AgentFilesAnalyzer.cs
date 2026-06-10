namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Utilities
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Yaml;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.Collections.Immutable;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// File reader that has access to everything within the client workspace.
    /// It used for instance to analyze the workspace structure, read files, and check if a directory is an agent directory.
    /// </summary>
    internal interface IAgentFilesAnalyzer
    {
        /// <summary>
        /// Given a directory path, enumerates all mcs files in the directory.
        /// </summary>
        IEnumerable<FilePath> EnumerateMcsFiles(DirectoryPath path);

        /// <summary>
        /// Given a directory, check if it is a root agent directory. The classic signal is a
        /// root <c>agent.mcs.yml</c> that is a <c>GptComponentMetadata</c>; the CLI signal
        /// (TDD D29) is the root <c>agent.sync.yaml</c> layout marker (a CliCopilot workspace,
        /// D22, has no root <c>agent.mcs.yml</c>). This is the preferred check.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public bool IsStrictAgentDirectory(DirectoryPath path);

        /// <summary>
        /// Given a directory path, checks if it is a valid agent directory.
        /// A valid agent directory has any of the files or folders specified in <see cref="LspProjectionLayout.FileStructureMap" />.
        /// </summary>
        public bool GuessMaybeAgentDirectory(DirectoryPath path);

        /// <summary>
        /// Given a file name without extension, returns full path to the mcs file if it exists.
        /// </summary>
        bool TryGetMcsFilePath(FilePath filePathWithoutExtension, [MaybeNullWhen(false)] out FilePath mcsFilePath);

        IEnumerable<DirectoryPath> EnumerateChildAgentsDirectories(DirectoryPath currentFolderPath);


    }

    internal class AgentFilesAnalyzer : IAgentFilesAnalyzer
    {
        private static readonly ImmutableArray<string> CompoundExtensionNames = [".mcs.yml", ".mcs.yaml"];
        private readonly IClientWorkspaceFileProvider _fileProvider;

        public AgentFilesAnalyzer(IClientWorkspaceFileProvider fileProvider)
        {
            _fileProvider = fileProvider;
        }

        // Has an agent.mcs.yml that's a GptComponentMetadata (classic), or the agent.sync.yaml
        // layout marker (TDD D29) for a CLI agent (which has no agent.mcs.yml, D22).
        public bool IsStrictAgentDirectory(DirectoryPath path)
        {
            var agentFilePathWithoutExt = path.GetChildFilePath("agent");

            if (TryGetMcsFilePath(agentFilePathWithoutExt, out var agentFilePath))
            {
                var fileText = _fileProvider.GetFileInfo(agentFilePath).ReadAllText();

                try
                {
                    // Verify we have a Agent.mcs.yml, and it's a GptComponentMetadata
                    // (not some aub agent). 
                    var root = YamlSerializer.Deserialize<BotElement>(fileText);

                    if (root is GptComponentMetadata)
                    {
                        return true;
                    }
                    else if (root is AgentDialog)
                    {
                        // It's a sub agent. Keep looking for the root agent. 
                        return false;
                    }

                    // This is bad - it's a file that we don't recognize at all. 
                    return false;
                }
                catch (Exception)
                {
                    // File was not a match. 
                }
            }

            // Check for component collections
            agentFilePathWithoutExt = path.GetChildFilePath("collection");

            if (TryGetMcsFilePath(agentFilePathWithoutExt, out agentFilePath))
            {
                var fileText = _fileProvider.GetFileInfo(agentFilePath).ReadAllText();

                try
                {
                    // Verify we have a Agent.mcs.yml, and it's a GptComponentMetadata
                    // (not some aub agent). 
                    var root = YamlSerializer.Deserialize<BotComponentCollection>(fileText);

                    if (root != null)
                    {
                        return true;
                    }                 
                    
                    return false;
                }
                catch (Exception)
                {
                    // File was not a match. 
                }
            }

            // CLI agents (TDD D22) have no root agent.mcs.yml. A CliCopilot workspace always
            // carries the agent.sync.yaml layout marker (TDD D29), so its presence is dispositive
            // for the CLI agent root. Anchoring here as a first-class strict signal avoids relying
            // on the loose folder-guess fallback, which can mis-root a CLI subfolder (e.g.
            // capabilities/ matches the classic knowledge/ key via capabilities/knowledge/).
            var markerFilePath = path.GetChildFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName);

            if (_fileProvider.GetFileInfo(markerFilePath).Exists)
            {
                return true;
            }

            return false;
        }

        // Apply loose heuristics to guess if this might be an agent directory.
        // This can easily be tricked into false positives. 
        public bool GuessMaybeAgentDirectory(DirectoryPath path)
        {
            foreach (var fileToType in LspProjectionLayout.FileStructureMap)
            {
                if (fileToType.Key.EndsWith('/'))
                {
                    var dirPath = path.GetChildDirectoryPath(fileToType.Key);
                    var candidate = _fileProvider.GetFileInfo(dirPath);
                    if (candidate.IsDirectory)
                    {
                        // We are being pretty loose here and assume that any directory with
                        // a recognized name will contain expected files.
                        return true;
                    }
                }
                else
                {
                    var filePath = path.GetChildFilePath(fileToType.Key);
                    var agentFilePathWithoutExt = filePath;
                    if (TryGetMcsFilePath(agentFilePathWithoutExt, out var agentFilePath))
                    {
                        var fileText = _fileProvider.GetFileInfo(agentFilePath).ReadAllText();
                        if (string.IsNullOrEmpty(fileText))
                        {
                            // accept empty file, assuming they can be fixed later
                            return true;
                        }

                        BotElement? element;
                        try
                        {
                            // Discard file if its content is not aligned with expected file structure.
                            // This allows ignoring name conflicts, where Topic name is "Agent", for instance.
                            element = YamlSerializer.Deserialize<BotElement>(fileText);
                        }
                        catch (Exception)
                        {
                            // Typically YamlReaderException, or ArgumentException.
                            // We don't really care as the file is invalid in any case.
                            element = null;
                        }

                        var elementType = element?.GetType();
                        if (elementType == typeof(UnknownBotElement) || fileToType.Value.Contains(elementType))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public bool TryGetMcsFilePath(FilePath filePathWithoutExtension, [MaybeNullWhen(false)] out FilePath mcsFilePath)
        {
            var filePathValue = filePathWithoutExtension.ToString();
            var mcsFilePathEnumerator = CompoundExtensionNames.Select(ext => new FilePath(filePathValue + ext)).Where(x => _fileProvider.GetFileInfo(x).Exists);
            if (mcsFilePathEnumerator.Any())
            {
                mcsFilePath = mcsFilePathEnumerator.First();
                return true;
            }

            mcsFilePath = default;
            return false;
        }

        public IEnumerable<FilePath> EnumerateMcsFiles(DirectoryPath path)
        {
            IDirectoryContents directoryContents = _fileProvider.GetDirectoryContents(path);

            if (!directoryContents.Exists)
            {
                yield break;
            }

            foreach (var fileInfo in directoryContents)
            {
                if (!fileInfo.IsDirectory && CompoundExtensionNames.Any(ext => fileInfo.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) && fileInfo.PhysicalPath != null)
                {
                    yield return fileInfo.ToFilePath();
                }
            }
        }

        public IEnumerable<DirectoryPath> EnumerateChildAgentsDirectories(DirectoryPath currentFolderPath)
        {
            var subAgentsRootPath = currentFolderPath.GetChildDirectoryPath("agents");
            IDirectoryContents agentsContent = _fileProvider.GetDirectoryContents(subAgentsRootPath);

            if (!agentsContent.Exists)
            {
                yield break;
            }

            foreach (var fileInfo in agentsContent)
            {
                if (fileInfo.IsDirectory && fileInfo.PhysicalPath != null)
                {
                    yield return fileInfo.ToDirectoryPath();
                }
            }
        }
    }
}
