

namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Utilities;
    using System.Globalization;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;

    internal class McsLanguage : ILanguageAbstraction
    {
        LanguageType ILanguageAbstraction.LanguageType { get; } = LanguageType.CopilotStudio;
        private readonly Dictionary<DirectoryPath, McsWorkspace> _agentDirectories = new();
        private readonly IClientInformation _clientInfo;
        private readonly ILspServices _lspServices;
        private readonly ILspLogger _logger;
        private readonly IAgentFilesAnalyzer _mcsFilesAnalyzer;
        private readonly IClientWorkspaceFileProvider _fileProvider;
        private readonly ILspTransport _transport;

        public McsLanguage(IClientInformation clientInfo, ILspServices lspServices, ILspLogger logger, IAgentFilesAnalyzer fileAnalyzer, IClientWorkspaceFileProvider fileProvider, ILspTransport transport)
        {
            _clientInfo = clientInfo;
            _lspServices = lspServices;
            _logger = logger;
            _mcsFilesAnalyzer = fileAnalyzer;
            _fileProvider = fileProvider;
            _transport = transport;
        }

        IEnumerable<Workspace> ILanguageAbstraction.Workspaces
        {
            get
            {
                return _agentDirectories.Values;
            }
        }

        LspDocument ILanguageAbstraction.CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath agentDirectoryPath)
        {
            var document = new McsLspDocument(path, text, agentDirectoryPath);
            return document;
        }

        Workspace ILanguageAbstraction.ResolveWorkspace(DirectoryPath directoryPath)
        {
            var candidates = _agentDirectories.Where(w => w.Key.Contains(directoryPath));
            McsWorkspace agentDirectory;
            if (candidates.Any())
            {
                agentDirectory = candidates.MaxBy(w => w.Key.Length).Value;
                LogAgentDirectorySelected(agentDirectory.FolderPath, candidates.Count());
            }
            else
            {
                agentDirectory = FindAgentDirectoryFolderAndCreate(directoryPath);
            }

            return agentDirectory;
        }

        private DirectoryPath? _lastAgentSelected = null;

        private void LogAgentDirectorySelected(DirectoryPath agentDirectory, int candidatesCount)
        {
            // only log agent selected event when changing agent directory, which should be rare in most cases
            if (!agentDirectory.Equals(_lastAgentSelected))
            {
                var agentName = GetAgentName(agentDirectory);
                LogAgentResolvingInfoEvent($"Agent Directory selected: '{agentDirectory}'" + (candidatesCount > 1 ? $" (Out of {candidatesCount} parent directories)" : string.Empty), $"Active agent directory changed to: {agentName}");
                _lastAgentSelected = agentDirectory;
            }
        }

        private void LogAgentResolvingInfoEvent(string sensitiveMsg, string altSafeMsg)
        {
            _logger.LogSensitiveInformation("[AgentInit] " + sensitiveMsg, "[AgentInit] " + altSafeMsg);
        }

        private void LogAgentResolvingDebugEvent(string message)
        {
            _logger.LogDebug("[AgentInit] " + message);
        }

        /// <summary>
        /// Extracts the agent folder name from a directory path (last segment before trailing slash).
        /// </summary>
        private static string GetAgentName(DirectoryPath directoryPath)
        {
            var path = directoryPath.ToString();
            if (path.Length < 2) return path;
            var trimmed = path.TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            return lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
        }

        private McsWorkspace FindAgentDirectoryFolderAndCreate(DirectoryPath documentDirectory)
        {
            // Try to find a valid agent directory within lookup depth
            if (IsValidAgentDirectory(documentDirectory, out var validAgentDirectory))
            {
                return CreateAgentDirectory(validAgentDirectory);
            }

            // Create a new agent directory where the document is located
            bool isWithinClientWorkspace = IsPathWithinClientWorkspace(documentDirectory);
            var safeLogMessage = "No valid agent directory detected. Initializing new directory at file location";
            LogAgentResolvingInfoEvent(safeLogMessage + $": {documentDirectory}", safeLogMessage);
            return CreateAgentDirectory(documentDirectory, isWithinClientWorkspace);
        }

        /// <summary>
        /// Checks if the given directory is valid agent directory.
        /// </summary>
        /// <returns>True if the directory is valid, otherwise false.</returns>
        public bool IsValidAgentDirectory(DirectoryPath documentDirectory, out DirectoryPath validDirectory)
        {
            const int MaxLookupDepth = 4;

            foreach (var selector in new Func<DirectoryPath, bool>[] {
                _mcsFilesAnalyzer.IsStrictAgentDirectory,
                (path) => _mcsFilesAnalyzer.GuessMaybeAgentDirectory(path)
            })
            {
                DirectoryPath currentFolder = documentDirectory;
                for (int folderCheckIdx = 0; folderCheckIdx <= MaxLookupDepth && IsPathWithinClientWorkspace(currentFolder); folderCheckIdx++)
                {
                    if (selector(currentFolder))
                    {
                        LogAgentResolvingDebugEvent($"Valid agent directory detected: '{currentFolder}'");
                        validDirectory = currentFolder;
                        return true;
                    }

                    currentFolder = currentFolder.GetParentDirectoryPath();
                }
            }

            validDirectory = documentDirectory;
            return false;
        }

        private McsWorkspace CreateAgentDirectory(DirectoryPath currentFolderPath, bool isFolderOpened = true)
        {
            // currentFolder must be a valid directory path
            var agentDirectory = new McsWorkspace(currentFolderPath, _lspServices);
            int documentCount = 0;

            if (isFolderOpened)
            {
                documentCount = AddLocalFilesToAgent(agentDirectory);
            }

            var agentName = GetAgentName(currentFolderPath);
            LogAgentResolvingInfoEvent(
                $"Agent directory initialized with {documentCount} documents tracked: '{currentFolderPath}'",
                $"Agent directory initialized with {documentCount} documents tracked for {agentName}");

            _agentDirectories.Add(currentFolderPath, agentDirectory);
            NotifyClientOfAgentDirectoryChange();
            return agentDirectory;
        }

        private int AddLocalFilesToAgent(McsWorkspace agentDirectory)
        {
            var agentDirectoryPath = agentDirectory.FolderPath;
            var agentName = GetAgentName(agentDirectoryPath);
            int totalDocumentCount = 0;

            void AddDocumentToAgent(IFileInfo fileInfo, FilePath documentPath)
            {
                agentDirectory.UpsertDocumentFromFile(documentPath, fileInfo, this, _clientInfo.CultureInfo);
                ++totalDocumentCount;
            }

            // SubAgents are in /agents/{name}/*
            IEnumerable<DirectoryPath> folders = [agentDirectoryPath];
            folders = folders.Concat(_mcsFilesAnalyzer.EnumerateChildAgentsDirectories(agentDirectoryPath));

            // Read all expected files in the workspace, create a document for each of them.
            // Expected files are the ones following predefined structure.
            // All documents have a semantic model and a single semantic model is created to merge each of the file semantic through the workspace
            var rootFiles = new List<string>();
            foreach (var folder in folders)
            {
                foreach (var relativePath in LspProjectionLayout.FileStructureMap.Keys)
                {
                    if (relativePath.EndsWith('/'))
                    {
                        int fileCount = 0;
                        var childDirPath = folder.GetChildDirectoryPath(relativePath);
                        foreach (var file in _mcsFilesAnalyzer.EnumerateMcsFiles(childDirPath))
                        {
                            RemoveDocumentFromPreviousAgent(file);
                            AddDocumentToAgent(_fileProvider.GetFileInfo(file), file);
                            ++fileCount;
                        }
                        if (fileCount > 0)
                        {
                            LogAgentResolvingDebugEvent($"{fileCount} files added under {relativePath} for {agentName}");
                        }
                    }
                    else
                    {
                        var pathWithoutExt = folder.GetChildFilePath(relativePath);
                        if (AgentFilePath.IsDefinition(pathWithoutExt) || AgentFilePath.IsIcon(pathWithoutExt))
                        {
                            var fileInfo = _fileProvider.GetFileInfo(pathWithoutExt);
                            if (fileInfo.Exists)
                            {
                                AddDocumentToAgent(fileInfo, pathWithoutExt);
                                rootFiles.Add(fileInfo.Name);
                            }
                        }

                        if (_mcsFilesAnalyzer.TryGetMcsFilePath(pathWithoutExt, out var path))
                        {
                            // per TryGetMcsFilePath, file must exist
                            var fileInfo = _fileProvider.GetFileInfo(path);
                            AddDocumentToAgent(fileInfo, path);
                            rootFiles.Add(path.FileName);
                        }
                    }
                }
            }

            if (rootFiles.Count > 0)
            {
                LogAgentResolvingDebugEvent($"{rootFiles.Count} files added to root directory for {agentName}: {string.Join(", ", rootFiles)}");            }

            agentDirectory.BuildCompilationModel();
            return totalDocumentCount;
        }

        private void RemoveDocumentFromPreviousAgent(FilePath file)
        {
            // this could happen if agent.mcs.yml is added after topics have been "registered".
            // in this case, we need to remove the document from the previous agent.
            // if the agent becomes empty, it should be deleted, to prevent adding documents to it again.
            var candidates = _agentDirectories.Where(w => w.Key.Contains(file));
            List<DirectoryPath> emptyAgentDirectories = new();
            foreach (var candidate in candidates)
            {
                var previousAgent = candidate.Value;
                if (previousAgent.RemoveDocument(file))
                {
                    LogAgentResolvingDebugEvent($"Document '{file}' removed from previous MCS directory '{previousAgent.FolderPath}'");

                    if (previousAgent.IsEmpty)
                    {
                        LogAgentResolvingDebugEvent($"Deleting previous MCS directory '{previousAgent.FolderPath}'. All documents ownership have been transferred to a new valid agent directory.");
                        emptyAgentDirectories.Add(candidate.Key);
                    }
                }
            }

            emptyAgentDirectories.ForEach(agentDir => _agentDirectories.Remove(agentDir));
        }

        private class AgentDirectoryParams
        {
            public required Uri[] Uris { get; set; }
        }

        private void NotifyClientOfAgentDirectoryChange()
        {
            // send notification to client
            var agentDirectoryParams = new AgentDirectoryParams
            {
                Uris = _agentDirectories.Keys.Select(dir => new Uri(dir.ToString())).ToArray(),
            };
            var message = new LspJsonRpcMessage
            {
                Method = Constants.JsonRpcMethods.AgentDirectoryChange,
                Params = JsonSerializer.SerializeToElement(agentDirectoryParams, Constants.DefaultSerializationOptions),
            };
            _ = _transport.SendAsync(message, CancellationToken.None);
        }

        private bool IsPathWithinClientWorkspace(DirectoryPath directory)
        {
            return _clientInfo.TryGetWorkspaceFolder(directory, out _);
        }
    }
}