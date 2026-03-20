namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;

    internal class ClientInformation : IClientInformation, IClientInformationInitializer
    {
        private InitializeParams? _initializeParams = null;
        private CultureInfo? _cultureInfo = null;
        private readonly ILspLogger _logger;

        public ClientInformation(ILspLogger logger)
        {
            _logger = logger;
        }

        public CultureInfo CultureInfo => _cultureInfo ??= GetCultureInfo(InitializeParams);

        public InitializeParams InitializeParams => _initializeParams ?? throw new InvalidOperationException("initialize request must complete first");

        private DirectoryPath[]? _workspaceFolders = null;

        internal DirectoryPath[] WorkspaceFolders
        {
            get
            {
                if (_workspaceFolders == null)
                {
                    var workspaceFolders = InitializeParams?.WorkspaceFolders;
                    if (workspaceFolders == null)
                    {
#pragma warning disable CS0618 // Type or member is obsolete
                        if (_initializeParams?.RootUri != null)
                        {
                            _logger.LogWarning("Client is using deprecated 'RootUri'. We will use that as workspace but this behavior is deprecated. Client should be updated.");
                            _workspaceFolders = [_initializeParams.RootUri.ToDirectoryPath()];
                        }
#pragma warning restore CS0618 // Type or member is obsolete
                        else
                        {
                            _workspaceFolders = [];
                        }
                    }
                    else if (workspaceFolders.Length == 0)
                    {
                        _workspaceFolders = [];
                    }
                    else
                    {
                        _workspaceFolders = GetDisjointFolders(workspaceFolders.Select(x => x.Uri));
                    }
                }

                return _workspaceFolders;
            }
        }

        private static DirectoryPath[] GetDisjointFolders(IEnumerable<Uri> folders)
        {
            // Sort by absolute URI length to ensure parent folders come first
            var sortedFolders = folders.Select(f => f.ToDirectoryPath())
                                       .OrderBy(f => f.Length)
                                       .ToList();

            var disjointFolders = new List<DirectoryPath>();

            foreach (var folder in sortedFolders)
            {
                if (!disjointFolders.Any(parent => parent.Contains(folder)))
                {
                    disjointFolders.Add(folder);
                }
            }

            return disjointFolders.ToArray();
        }

        bool IClientInformation.TryGetWorkspaceFolder(DirectoryPath directoryPath, [MaybeNullWhen(false)] out DirectoryPath clientWorkspaceFolder)
        {
            // this will throw InvalidOperationException throw if LSP.initialize method was not completed
            var workspaceFolders = WorkspaceFolders;

            foreach(var x in workspaceFolders)
            {
                if (x.Contains(directoryPath))
                {
                    clientWorkspaceFolder = x;
                    return true;
                }
            }
            clientWorkspaceFolder = default;
            return false;      
        }

        public void Initialize(InitializeParams intializeParams)
        {
            _initializeParams = intializeParams;
        }

        private static CultureInfo GetCultureInfo(InitializeParams initializeParams)
        {
            if (string.IsNullOrWhiteSpace(initializeParams.Locale))
            {
                return CultureInfo.InvariantCulture;
            }
            else
            {
                return new CultureInfo(initializeParams.Locale);
            }
        }
    }
}