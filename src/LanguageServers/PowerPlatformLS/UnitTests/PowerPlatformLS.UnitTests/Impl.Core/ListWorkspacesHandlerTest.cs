namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class ListWorkspacesHandlerTests
    {
        [Fact]
        public async Task HandleRequestAsyncReturnsEmptyWhenLanguageNotFound()
        {
            var provider = new FakeLanguageProvider(false, null);
            var clientInfo = CreateClientInfoEmpty();
            var handler = new ListWorkspacesHandler(provider, clientInfo, new FakeLogger());

            var result = await handler.HandleRequestAsync(CreateRequestContext(), CancellationToken.None);

            Assert.Empty(result.WorkspaceUris);
        }

        [Fact]
        public async Task HandleRequestAsyncFindsValidAgentDirectory()
        {
            var tempRoot = CreateTempDirectoryPath();
            var agentDir = new DirectoryPath(Path.Combine(tempRoot.ToString(), "agentA").Replace('\\', '/'));
            Directory.CreateDirectory(agentDir.ToString());

            try
            {
                var language = new FakeLanguageWithValidAgent("agentA");
                var provider = new FakeLanguageProvider(true, language);
                var clientInfo = CreateClientInfo(tempRoot);
                var handler = new ListWorkspacesHandler(provider, clientInfo, new FakeLogger());

                var result = await handler.HandleRequestAsync(CreateRequestContext(), CancellationToken.None);

                Assert.Contains(result.WorkspaceUris, u => u.AbsolutePath.Contains("agentA"));
            }
            finally
            {
                Directory.Delete(tempRoot.ToString(), true);
            }
        }

        [Fact]
        public async Task HandleRequestAsyncStopsScanningInsideValidAgent()
        {
            var tempRoot = CreateTempDirectoryPath();
            var agentDir = new DirectoryPath(Path.Combine(tempRoot.ToString(), "agentA").Replace('\\', '/'));
            Directory.CreateDirectory(agentDir.ToString());
            Directory.CreateDirectory(Path.Combine(agentDir.ToString(), "nested"));

            try
            {
                var language = new FakeLanguageWithValidAgent("agentA");
                int validationCount = 0;
                language.ValidationCallback = () => validationCount++;
                var provider = new FakeLanguageProvider(true, language);
                var clientInfo = CreateClientInfo(tempRoot);
                var handler = new ListWorkspacesHandler(provider, clientInfo, new FakeLogger());

                await handler.HandleRequestAsync(CreateRequestContext(), CancellationToken.None);

                Assert.Equal(2, validationCount);
            }
            finally
            {
                Directory.Delete(tempRoot.ToString(), true);
            }
        }

        [Fact]
        public async Task HandleRequestAsyncHandlesMultipleWorkspaceFolders()
        {
            var temp1 = CreateTempDirectoryPath();
            var temp2 = CreateTempDirectoryPath();
            Directory.CreateDirectory(Path.Combine(temp1.ToString(), "agentA"));
            Directory.CreateDirectory(Path.Combine(temp2.ToString(), "agentB"));

            try
            {
                var language = new FakeLanguageWithValidAgent("agentA", "agentB");
                var provider = new FakeLanguageProvider(true, language);
                var clientInfo = CreateClientInfo(new[] { temp1, temp2 });
                var handler = new ListWorkspacesHandler(provider, clientInfo, new FakeLogger());

                var result = await handler.HandleRequestAsync(CreateRequestContext(), CancellationToken.None);

                Assert.Contains(result.WorkspaceUris, u => u.AbsolutePath.Contains("agentA"));
                Assert.Contains(result.WorkspaceUris, u => u.AbsolutePath.Contains("agentB"));
            }
            finally
            {
                Directory.Delete(temp1.ToString(), true);
                Directory.Delete(temp2.ToString(), true);
            }
        }

        private static RequestContext CreateRequestContext()
        {
            var language = new FakeLanguage();
            var workspace = new Workspace(CreateTempDirectoryPath());
            return new RequestContext(language, workspace, null, 0);
        }

        private static DirectoryPath CreateTempDirectoryPath()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(path);
            var fullPath = Path.GetFullPath(path).Replace('\\', '/');
            return new DirectoryPath(fullPath);
        }

        private static ClientInformation CreateClientInfo(DirectoryPath folderPath)
        {
            var clientInfo = new ClientInformation(new FakeLogger());
            var initializeParams = new InitializeParams
            {
                Capabilities = new ClientCapabilities(),
                WorkspaceFolders = new[]
                {
                    new WorkspaceFolder
                    {
                        Uri = new Uri(Path.GetFullPath(folderPath.ToString()).Replace('\\', '/'), UriKind.Absolute),
                        Name = "TestWorkspace"
                    }
                }
            };
            clientInfo.Initialize(initializeParams);
            return clientInfo;
        }

        private static ClientInformation CreateClientInfo(DirectoryPath[] folderPaths)
        {
            var clientInfo = new ClientInformation(new FakeLogger());
            var initializeParams = new InitializeParams
            {
                Capabilities = new ClientCapabilities(),
                WorkspaceFolders = folderPaths
                    .Select(p => new WorkspaceFolder
                    {
                        Uri = new Uri(Path.GetFullPath(p.ToString()).Replace('\\', '/'), UriKind.Absolute),
                        Name = "TestWorkspace"
                    })
                    .ToArray()
            };
            clientInfo.Initialize(initializeParams);
            return clientInfo;
        }

        private static ClientInformation CreateClientInfoEmpty()
        {
            var clientInfo = new ClientInformation(new FakeLogger());
            var initializeParams = new InitializeParams
            {
                Capabilities = new ClientCapabilities(),
                WorkspaceFolders = Array.Empty<WorkspaceFolder>()
            };
            clientInfo.Initialize(initializeParams);
            return clientInfo;
        }

        private class FakeLanguageProvider : ILanguageProvider
        {
            private readonly bool _returnValue;
            private readonly ILanguageAbstraction? _language;

            public FakeLanguageProvider(bool returnValue, ILanguageAbstraction? language)
            {
                _returnValue = returnValue;
                _language = language;
            }

            bool ILanguageProvider.TryGetLanguage(LanguageType languageType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ILanguageAbstraction? language)
            {
                language = _language;
                return _returnValue;
            }

            bool ILanguageProvider.TryGetLanguageForDocument(LspUri uri, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ILanguageAbstraction? language)
            {
                language = _language; 
                return _returnValue;
            }
        }

        private class FakeLanguage : ILanguageAbstraction
        {
            public virtual Workspace[] Workspaces => Array.Empty<Workspace>();
            public virtual LanguageType LanguageType => LanguageType.CopilotStudio;

            public virtual LspDocument CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath workspacePath)
                => throw new NotImplementedException();

            public virtual bool IsValidAgentDirectory(DirectoryPath directory, out DirectoryPath validDirectory)
            {
                validDirectory = directory;
                return false;
            }
        }

        private class FakeLanguageWithValidAgent : FakeLanguage
        {
            public Action? ValidationCallback;
            private readonly string[] _validNames;

            public FakeLanguageWithValidAgent(params string[] validNames)
            {
                _validNames = validNames;
            }

            public override bool IsValidAgentDirectory(DirectoryPath directory, out DirectoryPath validDirectory)
            {
                ValidationCallback?.Invoke();
                if (_validNames.Any(n => directory.ToString().Contains(n)))
                {
                    validDirectory = directory;

                    return true;
                }
                validDirectory = directory;

                return false;
            }
        }

        private class FakeLogger : ILspLogger
        {
            public void LogEndContext(string message, params object[] @params) { }

            public void LogError(string message) { }

            public void LogError(string message, params object[] @params) { }

            public void LogException(Exception exception, string? message = null, params object[] @params) { }

            public void LogInfo(string message) { }

            public void LogInformation(string message, params object[] @params) { }

            public void LogSensitiveInformation(string message, string? altSafeMessage = null) { }

            public void LogStartContext(string message, params object[] @params) { }

            public void LogWarning(string message) { }

            public void LogWarning(string message, params object[] @params) { }
        }
    }
}